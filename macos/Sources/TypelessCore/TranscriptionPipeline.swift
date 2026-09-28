import Foundation

/// Limits how many STT requests run at once.
actor AsyncSemaphore {
    private var available: Int
    private var waiters: [CheckedContinuation<Void, Never>] = []

    init(_ count: Int) { available = count }

    func wait() async {
        if available > 0 { available -= 1; return }
        await withCheckedContinuation { waiters.append($0) }
    }

    func signal() {
        if waiters.isEmpty { available += 1 } else { waiters.removeFirst().resume() }
    }
}

public enum ChunkState: Sendable, Equatable {
    case queued
    case transcribing(attempt: Int)
    case done(String)
    case skippedSilence
    case failed(String)

    public var isFinished: Bool {
        switch self {
        case .done, .skippedSilence, .failed: return true
        default: return false
        }
    }
}

public struct PipelineFailure: Error, LocalizedError, Sendable {
    public let partialText: String
    public let failedChunks: [Int]
    public let underlying: String

    public var errorDescription: String? {
        L("有 \(failedChunks.count) 段转写失败：\(underlying)", "\(failedChunks.count) segment(s) failed: \(underlying)")
    }
}

/// Streams live audio into chunks and transcribes each chunk in the background while the user is
/// still talking. When the user stops, only the last chunk is left to transcribe.
///
/// Thread-safety: `append` is called from the audio thread; all mutable state is behind `lock`.
public final class TranscriptionPipeline: @unchecked Sendable {
    public typealias ChunkObserver = @Sendable (_ index: Int, _ state: ChunkState) -> Void

    private let client: APIClient
    private let options: APIClient.TranscriptionOptions
    private let policy: RetryPolicy
    private let observer: ChunkObserver?
    private let semaphore = AsyncSemaphore(3)

    private let lock = NSLock()
    private let chunker: Chunker
    private var chunks: [Int: AudioChunk] = [:]
    private var results: [Int: ChunkState] = [:]
    /// Chunks whose last failure was permanent (bad key, no credit…): not worth another round.
    private var permanentFailures: Set<Int> = []
    private var tasks: [Int: Task<Void, Never>] = [:]
    /// Usage of every successful request, retries of the same chunk included (each one was billed).
    private var usages: [RequestUsage] = []
    private var finished = false
    private var cancelled = false

    /// `preset` lets a retry reuse transcripts that already succeeded (keyed by chunk index).
    public init(
        client: APIClient,
        options: APIClient.TranscriptionOptions,
        chunkConfig: Chunker.Config = Chunker.Config(),
        policy: RetryPolicy = RetryPolicy(),
        preset: [Int: String] = [:],
        observer: ChunkObserver? = nil
    ) {
        self.client = client
        self.options = options
        self.policy = policy
        self.observer = observer
        self.chunker = Chunker(config: chunkConfig)
        for (index, text) in preset { results[index] = .done(text) }
    }

    public func append(_ samples: [Int16]) {
        lock.lock()
        guard !finished, !cancelled else { lock.unlock(); return }
        let ready = chunker.append(samples)
        lock.unlock()
        ready.forEach(schedule)
    }

    /// Flushes the remaining audio and waits for every chunk. Failed chunks get one more full retry
    /// round before giving up, so a brief network blip during a long dictation doesn't lose it.
    public func finish() async throws -> String {
        let tail = lock.withLock { () -> AudioChunk? in
            finished = true
            return chunker.finish()
        }
        if let tail { schedule(tail) }

        await waitForAll()

        let failed = failedIndices().filter { index in lock.withLock { !permanentFailures.contains(index) } }
        if !failed.isEmpty, !isCancelled {
            for index in failed { restart(index) }
            await waitForAll()
        }
        if isCancelled { throw APIError.cancelled }

        let (text, stillFailed, lastError) = snapshot()
        if !stillFailed.isEmpty {
            throw PipelineFailure(partialText: text, failedChunks: stillFailed, underlying: lastError ?? "unknown")
        }
        return text
    }

    public func cancel() {
        lock.lock()
        cancelled = true
        let running = Array(tasks.values)
        lock.unlock()
        running.forEach { $0.cancel() }
    }

    /// Per-chunk transcripts so far (for saving progress / history).
    public func completedTranscripts() -> [Int: String] {
        lock.lock(); defer { lock.unlock() }
        return results.compactMapValues { if case let .done(text) = $0 { return text } else { return nil } }
    }

    /// What the requests so far used, one entry per successful request.
    public func requestUsages() -> [RequestUsage] {
        lock.withLock { usages }
    }

    // MARK: - Private

    private var isCancelled: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled
    }

    private func schedule(_ chunk: AudioChunk) {
        lock.lock()
        chunks[chunk.index] = chunk
        if case .done = results[chunk.index] {
            lock.unlock()
            observer?(chunk.index, results[chunk.index]!)
            return
        }
        lock.unlock()
        restart(chunk.index)
    }

    private func restart(_ index: Int) {
        lock.lock()
        guard let chunk = chunks[index] else { lock.unlock(); return }
        if AudioLevel.isSilent(chunk.samples) || chunk.duration < 0.3 {
            results[index] = .skippedSilence
            lock.unlock()
            observer?(index, .skippedSilence)
            return
        }
        results[index] = .queued
        let task = Task { [weak self] in
            guard let self else { return }
            await self.semaphore.wait()
            let (state, permanent) = await self.run(chunk)
            await self.semaphore.signal()
            self.lock.withLock {
                self.results[index] = state
                if permanent { self.permanentFailures.insert(index) } else { self.permanentFailures.remove(index) }
            }
            self.observer?(index, state)
        }
        tasks[index] = task
        lock.unlock()
        observer?(index, .queued)
    }

    private func run(_ chunk: AudioChunk) async -> (ChunkState, permanent: Bool) {
        if Task.isCancelled { return (.failed(APIError.cancelled.localizedDescription), true) }
        observer?(chunk.index, .transcribing(attempt: 1))
        do {
            let result = try await client.transcribeDetailedWithRetry(
                samples: chunk.samples, options: options, policy: policy,
                onRetry: { [observer] attempt, error in
                    if ProcessInfo.processInfo.environment["OPENTYPELESS_DEBUG"] != nil {
                        FileHandle.standardError.write(Data("  chunk \(chunk.index) attempt \(attempt) failed: \(error)\n".utf8))
                    }
                    observer?(chunk.index, .transcribing(attempt: attempt + 1))
                }
            )
            if let usage = result.usage { lock.withLock { usages.append(usage) } }
            return (.done(result.text), false)
        } catch {
            let apiError = APIError.from(error)
            return (.failed(apiError.localizedDescription), !apiError.isRetryable)
        }
    }

    private func waitForAll() async {
        while true {
            let pending = lock.withLock {
                tasks.filter { results[$0.key]?.isFinished != true }.map(\.value)
            }
            if pending.isEmpty { return }
            for task in pending { await task.value }
        }
    }

    private func failedIndices() -> [Int] {
        lock.lock(); defer { lock.unlock() }
        return results.compactMap { if case .failed = $0.value { return $0.key } else { return nil } }.sorted()
    }

    private func snapshot() -> (String, [Int], String?) {
        lock.lock(); defer { lock.unlock() }
        var parts: [String] = []
        var failed: [Int] = []
        var lastError: String?
        for index in results.keys.sorted() {
            switch results[index]! {
            case let .done(text): parts.append(text)
            case let .failed(message): failed.append(index); lastError = message
            default: break
            }
        }
        return (TranscriptJoiner.join(parts), failed, lastError)
    }
}
