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
/// still talking. When the user stops, only the last chunk is left to transcribe, and often not even that: when the
/// speaker pauses, the audio so far is transcribed ahead of time (a speculative tail), and if they then let go of the
/// key without saying anything more, that answer is the last chunk's.
///
/// Thread-safety: `append` is called from the audio thread; all mutable state is behind `lock`.
public final class TranscriptionPipeline: @unchecked Sendable {
    public typealias ChunkObserver = @Sendable (_ index: Int, _ state: ChunkState) -> Void

    private let client: APIClient
    private let options: APIClient.TranscriptionOptions
    /// Another provider/model, asked too when the primary is late for a chunk or fails (see `transcribe`).
    private let backup: (client: APIClient, options: APIClient.TranscriptionOptions)?
    private let latency: TranscriptionLatency
    private let policy: RetryPolicy
    private let observer: ChunkObserver?
    private let semaphore = AsyncSemaphore(3)
    private let trimSilence: Bool
    private let speculate: Bool
    private let onSpeculation: (@Sendable (String?) -> Void)?

    private let lock = NSLock()
    private let chunker: Chunker
    private var chunks: [Int: AudioChunk] = [:]
    private var results: [Int: ChunkState] = [:]
    /// Chunks whose last failure was permanent (bad key, no credit…): not worth another round.
    private var permanentFailures: Set<Int> = []
    private var tasks: [Int: Task<Void, Never>] = [:]
    /// Usage of every successful request, retries of the same chunk included (each one was billed).
    private var usages: [RequestUsage] = []
    private var backupUsages: [RequestUsage] = []
    private var finished = false
    /// Every chunk has been handed out: `finish` has scheduled the tail.
    private var flushed = false
    private var cancelled = false

    /// How long the speaker has to be quiet before the audio so far is transcribed ahead of time.
    public static let pauseSeconds = 0.3
    private let pauses = PauseTracker()
    /// The audio not yet cut into a chunk, being transcribed ahead of time since the speaker paused.
    private var speculation: Speculation?
    /// The last transcript passed to `onSpeculation`.
    private var announced: String?
    private var tailSpeculative = false

    private final class Speculation: @unchecked Sendable {
        /// The pending audio when the pause was noticed; it ends where the speculation's audio ends.
        let chunk: AudioChunk
        var task: Task<(ChunkState, Bool), Never>?
        /// Set (under the pipeline's lock) once the request is answered.
        var outcome: (state: ChunkState, permanent: Bool)?
        var end: Int { chunk.startSample + chunk.samples.count }

        init(chunk: AudioChunk) { self.chunk = chunk }
    }

    /// Whether the last chunk's transcript came from a speculative tail, so nothing was left to send when the recording
    /// ended.
    public var tailWasSpeculative: Bool { lock.withLock { tailSpeculative } }

    /// `preset` lets a retry reuse transcripts that already succeeded (keyed by chunk index).
    /// `trimSilence` sends each chunk without the silence before and after its speech (see `AudioChunk.trimmed`).
    /// `speculate` transcribes the pending audio ahead of time whenever the speaker pauses (see above).
    /// `onSpeculation` is called, on a background thread, with the whole transcript the recording would give if it ended
    /// now, once a speculative tail and every chunk before it are transcribed; with nil when that stops being true
    /// because the speaker went on. Lets the caller start cleaning the text up before the key is released.
    public init(
        client: APIClient,
        options: APIClient.TranscriptionOptions,
        chunkConfig: Chunker.Config = Chunker.Config(),
        policy: RetryPolicy = RetryPolicy(),
        preset: [Int: String] = [:],
        backup: (client: APIClient, options: APIClient.TranscriptionOptions)? = nil,
        latency: TranscriptionLatency = TranscriptionLatency(),
        trimSilence: Bool = true,
        speculate: Bool = true,
        onSpeculation: (@Sendable (String?) -> Void)? = nil,
        observer: ChunkObserver? = nil
    ) {
        self.trimSilence = trimSilence
        self.speculate = speculate
        self.onSpeculation = onSpeculation
        self.client = client
        self.options = options
        self.backup = backup
        self.latency = latency
        self.policy = policy
        self.observer = observer
        self.chunker = Chunker(config: chunkConfig)
        for (index, text) in preset { results[index] = .done(text) }
    }

    public func append(_ samples: [Int16]) {
        lock.lock()
        guard !finished, !cancelled else { lock.unlock(); return }
        let ready = chunker.append(samples)
        var speculationChanged = false
        if speculate {
            let before = speculation
            pauses.append(samples)
            if !ready.isEmpty {
                // A chunk was cut off the pending audio the speculation covers: it no longer matches the tail.
                dropSpeculation()
                pauses.forget(before: chunker.pendingStart)
            }
            updateSpeculation()
            speculationChanged = speculation !== before
        }
        lock.unlock()
        ready.forEach(schedule)
        if speculationChanged { announce() }
    }

    /// Flushes the remaining audio and waits for every chunk. Failed chunks get one more full retry
    /// round before giving up, so a brief network blip during a long dictation doesn't lose it.
    public func finish() async throws -> String {
        let (tail, adopted) = lock.withLock { () -> (AudioChunk?, Bool) in
            finished = true
            let tail = chunker.finish()
            guard let speculation else { return (tail, false) }
            self.speculation = nil
            // Only silence since the pause: the speculative answer is the tail's.
            if let tail, tail.index == speculation.chunk.index, tail.startSample == speculation.chunk.startSample,
               !pauses.speechAfter(speculation.end, includePartial: true) {
                adopt(tail, speculation)
                return (tail, true)
            }
            speculation.task?.cancel()
            return (tail, false)
        }
        if let tail {
            if adopted { observer?(tail.index, .queued) } else { schedule(tail) }
        }
        lock.withLock { flushed = true }

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
        let speculative = speculation?.task
        lock.unlock()
        running.forEach { $0.cancel() }
        speculative?.cancel()
    }

    /// Per-chunk transcripts so far (for saving progress / history).
    public func completedTranscripts() -> [Int: String] {
        lock.lock(); defer { lock.unlock() }
        return results.compactMapValues { if case let .done(text) = $0 { return text } else { return nil } }
    }

    /// How many chunks have their answer (or needed none), of how many in all; nil until `finish` has handed out
    /// the last one, since the total isn't known before.
    public func transcriptionProgress() -> (done: Int, total: Int)? {
        lock.withLock { () -> (done: Int, total: Int)? in
            guard flushed else { return nil }
            return (results.values.filter(\.isFinished).count, results.count)
        }
    }

    /// What the requests so far used, one entry per successful request.
    public func requestUsages() -> [RequestUsage] {
        lock.withLock { usages }
    }

    /// Usage of the requests the backup route answered (priced with the backup's model).
    public func backupRequestUsages() -> [RequestUsage] {
        lock.withLock { backupUsages }
    }

    // MARK: - Private

    private var isCancelled: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled
    }

    /// The recording has ended (`finish` was called), so every chunk still out is holding up the text.
    private var isFinished: Bool {
        lock.withLock { finished }
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
            self.record(index, state, permanent: permanent)
        }
        tasks[index] = task
        lock.unlock()
        observer?(index, .queued)
    }

    private func record(_ index: Int, _ state: ChunkState, permanent: Bool) {
        lock.withLock {
            results[index] = state
            if permanent { permanentFailures.insert(index) } else { permanentFailures.remove(index) }
        }
        observer?(index, state)
        announce()
    }

    // MARK: - Speculative tail

    /// After new audio: drops the speculation if the speaker has spoken since it started, and starts one when they have
    /// been quiet for `pauseSeconds` after speaking. Called under the lock.
    private func updateSpeculation() {
        if let current = speculation {
            guard pauses.speechAfter(current.end) else { return }
            dropSpeculation()
        }
        guard let lastSpeech = pauses.lastSpeechEnd(),
              pauses.sampleCount - lastSpeech >= AudioFormat.sampleCount(forSeconds: Self.pauseSeconds),
              let chunk = chunker.peek(), chunk.duration >= 0.3, !AudioLevel.isSilent(chunk.samples) else { return }
        if case .done = results[chunk.index] { return }

        let speculation = Speculation(chunk: chunk)
        speculation.task = Task { [weak self] in
            guard let self else { return (.failed(APIError.cancelled.localizedDescription), true) }
            await self.semaphore.wait()
            let outcome = await self.run(chunk, report: false)
            await self.semaphore.signal()
            self.lock.withLock { speculation.outcome = outcome }
            self.announce()
            return outcome
        }
        self.speculation = speculation
    }

    /// Cancels the speculation (the speaker went on). Called under the lock.
    private func dropSpeculation() {
        speculation?.task?.cancel()
        speculation = nil
    }

    /// Makes the speculation's request the tail's, as if the tail had been sent when the pause began. Called under the
    /// lock.
    private func adopt(_ tail: AudioChunk, _ speculation: Speculation) {
        tailSpeculative = true
        // Retries (the final round) send the tail itself.
        chunks[tail.index] = tail
        results[tail.index] = .queued
        tasks[tail.index] = Task { [weak self] in
            let request = speculation.task
            let (state, permanent) = await withTaskCancellationHandler {
                await request?.value ?? (.failed(APIError.cancelled.localizedDescription), true)
            } onCancel: {
                request?.cancel()
            }
            self?.record(tail.index, state, permanent: permanent)
        }
    }

    /// Tells `onSpeculation` when the transcript the recording would give if it ended now changes.
    private func announce() {
        guard let onSpeculation else { return }
        let change = lock.withLock { () -> String?? in
            guard !finished, !cancelled else { return .none }
            let text = speculativeTranscript()
            guard text != announced else { return .none }
            announced = text
            return .some(text)
        }
        if case let .some(text) = change { onSpeculation(text) }
    }

    /// The whole transcript with the speculation as the tail, once it and every chunk before it are done. Under the lock.
    private func speculativeTranscript() -> String? {
        guard let speculation, case let .done(tail)? = speculation.outcome?.state else { return nil }
        var parts: [String] = []
        for index in 0..<speculation.chunk.index {
            switch results[index] {
            case let .done(text)?: parts.append(text)
            case .skippedSilence?: break
            default: return nil
            }
        }
        parts.append(tail)
        return TranscriptJoiner.join(parts)
    }

    // MARK: - Requests

    /// `report` is false for a speculative tail: nothing is waiting on it yet, so the observer isn't told.
    private func run(_ chunk: AudioChunk, report: Bool = true) async -> (ChunkState, permanent: Bool) {
        if Task.isCancelled { return (.failed(APIError.cancelled.localizedDescription), true) }
        if report { observer?(chunk.index, .transcribing(attempt: 1)) }
        let chunk = trimSilence ? chunk.trimmed() : chunk
        do {
            let (result, usedBackup) = try await transcribe(chunk, report: report)
            if let usage = result.usage {
                lock.withLock { if usedBackup { backupUsages.append(usage) } else { usages.append(usage) } }
            }
            return (.done(result.text), false)
        } catch {
            let apiError = APIError.from(error)
            return (.failed(apiError.localizedDescription), !apiError.isRetryable)
        }
    }

    private enum Attempt: Sendable {
        case primary(Result<APIClient.TranscriptionResult, Error>)
        case backup(Result<APIClient.TranscriptionResult, Error>)
        case late
    }

    private func attempt(_ route: APIClient, _ options: APIClient.TranscriptionOptions, _ chunk: AudioChunk,
                         report: Bool) async throws -> APIClient.TranscriptionResult {
        try await route.transcribeDetailedWithRetry(
            samples: chunk.samples, options: options, policy: policy,
            onRetry: { [observer] attempt, error in
                if ProcessInfo.processInfo.environment["OPENTYPELESS_DEBUG"] != nil {
                    FileHandle.standardError.write(Data("  chunk \(chunk.index) attempt \(attempt) failed: \(error)\n".utf8))
                }
                if report { observer?(chunk.index, .transcribing(attempt: attempt + 1)) }
            }
        )
    }

    /// One chunk, on the primary route; with a backup route, the backup is asked too once the primary has failed,
    /// or is late (see `TranscriptionLatency`) and the recording has ended, and whichever answers first is used; the
    /// other is cancelled.
    private func transcribe(_ chunk: AudioChunk, report: Bool) async throws -> (APIClient.TranscriptionResult, usedBackup: Bool) {
        let start = ProcessInfo.processInfo.systemUptime
        guard let backup else {
            let result = try await attempt(client, options, chunk, report: report)
            latency.record(latency: ProcessInfo.processInfo.systemUptime - start, audioSeconds: chunk.duration)
            return (result, false)
        }
        let delay = latency.hedgeDelay(forAudioSeconds: chunk.duration)
        return try await withThrowingTaskGroup(of: Attempt.self) { group in
            group.addTask {
                do { return .primary(.success(try await self.attempt(self.client, self.options, chunk, report: report))) }
                catch { return .primary(.failure(error)) }
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
                // While the user is still talking nothing waits on this chunk yet, so the backup isn't worth paying
                // for; once the recording ends, a chunk that is late by then is asked of the backup at once.
                while !Task.isCancelled, !self.isFinished {
                    try? await Task.sleep(nanoseconds: 50_000_000)
                }
                return .late
            }
            var backupStarted = false
            var primaryError: Error?
            var backupFailed = false
            while let outcome = try await group.next() {
                var askBackup = false
                switch outcome {
                case .late:
                    askBackup = primaryError == nil
                case let .primary(.success(result)):
                    group.cancelAll()
                    latency.record(latency: ProcessInfo.processInfo.systemUptime - start, audioSeconds: chunk.duration)
                    return (result, false)
                case let .primary(.failure(error)):
                    if APIError.from(error) == .cancelled || backupFailed {
                        group.cancelAll()
                        throw error
                    }
                    primaryError = error
                    askBackup = true
                case let .backup(.success(result)):
                    group.cancelAll()
                    return (result, true)
                case .backup(.failure):
                    backupFailed = true
                    if let primaryError {
                        group.cancelAll()
                        throw primaryError
                    }
                }
                if askBackup, !backupStarted {
                    backupStarted = true
                    group.addTask {
                        do { return .backup(.success(try await self.attempt(backup.client, backup.options, chunk, report: report))) }
                        catch { return .backup(.failure(error)) }
                    }
                }
            }
            throw primaryError ?? APIError.cancelled
        }
    }

    /// How many chunks the backup route transcribed.
    public func backupChunkCount() -> Int {
        lock.withLock { backupUsages.count }
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
