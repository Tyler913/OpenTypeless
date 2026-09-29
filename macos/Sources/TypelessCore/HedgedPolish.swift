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
}

/// Hedged clean-up: the primary route starts alone; if it hasn't produced its first token after
/// `hedgeDelay`, or fails before then, the backup starts too. Whichever streams first is kept and
/// the other is cancelled. A slow provider then costs at most `hedgeDelay` plus the backup's own
/// first-token time, while the extra spend is limited to the slow tail.
public enum HedgedPolish {
    /// Fixed on purpose rather than learned at runtime, since flash-model first-token latency is
    /// stable. Measured from the client (Sep 2026): the flash models we tried average 0.85 s to the
    /// first token, and OpenRouter's per-provider P50 is about 1 s; the default clean-up model stays
    /// under 0.6 s, so 0.8 s means the backup only runs when something is actually wrong.
    public static let defaultHedgeDelay: Double = 0.8

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
        var tasks: [Int: Task<Void, Never>] = [:]

        func launch(_ index: Int) {
            guard tasks[index] == nil, routes.indices.contains(index) else { return }
            let route = routes[index]
            tasks[index] = Task {
                do {
                    let result = try await route.client.polish(
                        transcript: transcript, options: route.options,
                        onPartial: { text in
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
                    guard winner == nil || winner == index else { continue }
                    let now = ProcessInfo.processInfo.systemUptime - start
                    return HedgedPolishResult(result: result, model: routes[index].options.model,
                                              usedBackup: index != 0, firstTokenSeconds: firstTokenAt ?? now,
                                              totalSeconds: now, provider: routes[index].client.endpoint.id)
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

/// Reports each route's first token exactly once (called from the streaming callbacks).
private final class FirstTokenLatch: @unchecked Sendable {
    private let lock = NSLock()
    private var seen: Set<Int> = []

    func mark(_ index: Int) -> Bool {
        lock.withLock { seen.insert(index).inserted }
    }
}
