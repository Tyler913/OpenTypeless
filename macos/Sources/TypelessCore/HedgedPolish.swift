import Foundation

/// One way to run the clean-up: a provider plus a model.
public struct PolishRoute: Sendable {
    public var client: APIClient
    public var options: PolishOptions

    public init(client: APIClient, options: PolishOptions) {
        self.client = client
        self.options = options
    }
}

public struct HedgedPolishResult: Sendable {
    public let result: APIClient.PolishResult
    /// The model whose answer was used.
    public let model: String
    public let usedBackup: Bool
    /// From the start of the call until the winning stream's first token.
    public let firstTokenSeconds: Double
    public let totalSeconds: Double
    /// The provider that answered, to price the answer.
    public var provider: ProviderID = .openrouter
    /// The other route's request, when it was sent and then cancelled because this one answered first.
    public var cancelled: [CancelledPolish] = []
}

/// A request the hedge sent and then cancelled because the other route answered first. Providers bill the prompt they
/// had already read and any text they had sent, so its usage is estimated (marked so) and counted with the answer's.
public struct CancelledPolish: Sendable {
    public let model: String
    public let provider: ProviderID
    public let usage: RequestUsage
}

/// Hedged clean-up: the primary route starts alone; if it hasn't produced its first token after
/// `hedgeDelay`, or fails before then, the backup starts too. Whichever streams first is kept and
/// the other is cancelled. A slow provider then costs at most `hedgeDelay` plus the backup's own
/// first-token time, while the extra spend is limited to the slow tail.
public enum HedgedPolish {
    /// Fixed on purpose rather than learned at runtime, since flash-model first-token latency is
    /// stable. Measured from the client (Sep 2026): gemini-3.1-flash-lite, the fastest clean-up model,
    /// has its first token at 0.41 s at the median and 0.49 s at p90, so a first token still missing at
    /// 0.55 s is already in the slow tail and the backup is worth asking.
    public static let defaultHedgeDelay: Double = 0.55

    public static func run(
        transcript: String,
        primary: PolishRoute,
        backup: PolishRoute?,
        hedgeDelay: Double = defaultHedgeDelay,
        onPartial: (@Sendable (String) -> Void)? = nil
    ) async throws -> HedgedPolishResult {
        let routes = [primary] + (backup.map { [$0] } ?? [])
        let start = ProcessInfo.processInfo.systemUptime
        let (events, continuation) = AsyncStream<Event>.makeStream()
        let firstToken = FirstTokenLatch()
        let leader = Leader()
        let streamed = StreamedText()
        var tasks: [Int: Task<Void, Never>] = [:]

        func launch(_ index: Int) {
            guard tasks[index] == nil, routes.indices.contains(index) else { return }
            let route = routes[index]
            tasks[index] = Task {
                do {
                    let result = try await route.client.polish(
                        transcript: transcript, options: route.options,
                        onPartial: { text in
                            streamed.set(index, text)
                            if firstToken.mark(index) { continuation.yield(.firstToken(index)) }
                            // Only the stream that answered first is passed on.
                            if leader.claim(index) { onPartial?(text) }
                        })
                    continuation.yield(.finished(index, .success(result)))
                } catch {
                    continuation.yield(.finished(index, .failure(error)))
                }
            }
        }

        let timer = Task {
            try? await Task.sleep(nanoseconds: UInt64(hedgeDelay * 1_000_000_000))
            if !Task.isCancelled { continuation.yield(.hedgeTimer) }
        }
        defer {
            timer.cancel()
            tasks.values.forEach { $0.cancel() }
            continuation.finish()
        }

        return try await withTaskCancellationHandler {
            launch(0)
            var winner: Int?
            var firstTokenAt: Double?
            var failures: [Int: Error] = [:]
            // Answers that came in after the other route had already won.
            var late: [Int: APIClient.PolishResult] = [:]
            for await event in events {
                switch event {
                case .hedgeTimer:
                    if winner == nil { launch(1) }
                case let .firstToken(index):
                    guard winner == nil else { continue }
                    winner = index
                    firstTokenAt = ProcessInfo.processInfo.systemUptime - start
                    timer.cancel()
                    for (other, task) in tasks where other != index { task.cancel() }
                case let .finished(index, .success(result)):
                    guard winner == nil || winner == index else { late[index] = result; continue }
                    let now = ProcessInfo.processInfo.systemUptime - start
                    // Every other request that was sent and didn't fail was cancelled when this one won.
                    let cancelled = tasks.keys.sorted().filter { $0 != index && failures[$0] == nil }.map { other in
                        let route = routes[other]
                        return CancelledPolish(
                            model: route.options.model, provider: route.client.endpoint.id,
                            usage: late[other]?.usage ?? APIClient.estimatedUsage(
                                messages: APIClient.polishMessages(transcript: transcript, options: route.options),
                                output: streamed.text(other)))
                    }
                    return HedgedPolishResult(result: result, model: routes[index].options.model,
                                              usedBackup: index != 0, firstTokenSeconds: firstTokenAt ?? now,
                                              totalSeconds: now, provider: routes[index].client.endpoint.id,
                                              cancelled: cancelled)
                case let .finished(index, .failure(error)):
                    // The chosen stream broke mid-way: its partial text is useless, so give up this round.
                    if winner == index { throw error }
                    guard winner == nil else { continue }
                    failures[index] = error
                    if APIError.from(error) == .cancelled { throw error }
                    // Failing fast is the other reason to bring in the backup early.
                    launch(1)
                    if failures.count == tasks.count { throw failures[0] ?? error }
                }
            }
            throw APIError.cancelled
        } onCancel: {
            continuation.finish()
        }
    }

    enum Event: Sendable {
        case hedgeTimer
        case firstToken(Int)
        case finished(Int, Result<APIClient.PolishResult, Error>)
    }
}

/// The route that streamed first.
private final class Leader: @unchecked Sendable {
    private let lock = NSLock()
    private var index: Int?

    func claim(_ candidate: Int) -> Bool {
        lock.withLock { () -> Bool in
            if index == nil { index = candidate }
            return index == candidate
        }
    }
}

/// Each route's text so far (written from the streaming callbacks), to estimate what a cancelled one used.
private final class StreamedText: @unchecked Sendable {
    private let lock = NSLock()
    private var texts: [Int: String] = [:]

    func set(_ index: Int, _ text: String) { lock.withLock { texts[index] = text } }
    func text(_ index: Int) -> String { lock.withLock { texts[index] ?? "" } }
}

/// Reports each route's first token exactly once (called from the streaming callbacks).
private final class FirstTokenLatch: @unchecked Sendable {
    private let lock = NSLock()
    private var seen: Set<Int> = []

    func mark(_ index: Int) -> Bool {
        lock.withLock { seen.insert(index).inserted }
    }
}
