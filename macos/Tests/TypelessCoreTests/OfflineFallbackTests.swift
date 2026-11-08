import Foundation
import Testing
@testable import TypelessCore

/// A provider that fails its first requests with a scripted error (by request number, from 1), and answers "cloud"
/// after that, through the retry policy it's given, as `APIClient` does.
final class ScriptedProvider: SpeechToTextRoute, @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    private let failure: @Sendable (Int) -> APIError?

    init(failure: @escaping @Sendable (Int) -> APIError?) { self.failure = failure }

    var requests: Int { lock.withLock { count } }

    func transcribeDetailedWithRetry(samples: [Int16], options: APIClient.TranscriptionOptions, policy: RetryPolicy,
                                     onRetry: ((Int, APIError) -> Void)?) async throws -> APIClient.TranscriptionResult {
        try await policy.run(onRetry: onRetry) { _ in
            let number = lock.withLock { () -> Int in
                count += 1
                return count
            }
            if let error = failure(number) { throw error }
            return APIClient.TranscriptionResult(
                text: "cloud", usage: RequestUsage(audioSeconds: AudioFormat.seconds(forSampleCount: samples.count), cost: 0.01))
        }
    }
}

/// Settable from the test while the pipeline reads it.
final class NetworkFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Bool
    init(_ value: Bool) { self.value = value }
    var down: Bool {
        get { lock.withLock { value } }
        set { lock.withLock { value = newValue } }
    }
}

/// On-device recognition standing in for the provider while the network is down: the shared decisions, how errors are
/// told apart, and the pipeline going over to it.
@Suite struct OfflineFallbackTests {
    /// testdata/offline-fallback-cases.json, shared with the Windows tests so both apps go over at the same moments.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/offline-fallback-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    /// The error a case names.
    static func error(_ name: String) -> APIError {
        if let failure = NetworkFailure(rawValue: name) { return .offline(failure, name) }
        if name.hasPrefix("http"), let status = Int(name.dropFirst(4)) { return .http(status: status, message: "") }
        switch name {
        case "network": return .network("TLS")
        case "timeout": return .timeout(seconds: 30)
        case "badResponse": return .badResponse("")
        case "cancelled": return .cancelled
        case "missingKey": return .missingAPIKey("OpenRouter")
        default: fatalError("unknown error \(name)")
        }
    }

    private static func texts(_ value: Any?) -> [Int: String] {
        Dictionary(uniqueKeysWithValues: (value as! [String: String]).map { (Int($0.key)!, $0.value) })
    }

    // MARK: Shared cases

    @Test func skipsTheProviderAsTheSharedCasesSay() throws {
        for item in try Self.cases()["skipsProvider"] as! [[String: Any]] {
            let skips = OfflineFallback.skipsProvider(switched: item["switched"] as! Bool, networkDown: item["networkDown"] as! Bool,
                                                      ready: item["ready"] as! Bool)
            #expect(skips == item["skips"] as! Bool, "\(item)")
        }
    }

    @Test func takesOverAsTheSharedCasesSay() throws {
        for item in try Self.cases()["takesOver"] as! [[String: Any]] {
            let takes = OfflineFallback.takesOver(after: Self.error(item["error"] as! String),
                                                  retriesLeft: item["retriesLeft"] as! Bool,
                                                  networkDown: item["networkDown"] as! Bool, ready: item["ready"] as! Bool)
            #expect(takes == item["takesOver"] as! Bool, "\(item)")
        }
    }

    @Test func localHostsAsTheSharedCasesSay() throws {
        for item in try Self.cases()["localHosts"] as! [[String: Any]] {
            #expect(OfflineFallback.isLocalHost(item["host"] as! String) == item["local"] as! Bool, "\(item)")
        }
    }

    @Test func retriesReuseAsTheSharedCasesSay() throws {
        for item in try Self.cases()["reused"] as! [[String: Any]] {
            let reused = OfflineFallback.reused(Self.texts(item["texts"]), onDevice: item["onDevice"] as? [Int],
                                                usesProvider: item["usesProvider"] as! Bool)
            #expect(reused.texts == Self.texts(item["reusedTexts"]), "\(item)")
            #expect(reused.onDevice == item["reusedOnDevice"] as? [Int], "\(item)")
        }
    }

    // MARK: Errors

    @Test func urlSessionErrorsTellTheNetworkApartFromTheProvider() {
        #expect(NetworkFailure(.notConnectedToInternet) == .noConnection)
        #expect(NetworkFailure(.dataNotAllowed) == .noConnection)
        #expect(NetworkFailure(.internationalRoamingOff) == .noConnection)
        #expect(NetworkFailure(.cannotFindHost) == .hostUnreachable)
        #expect(NetworkFailure(.dnsLookupFailed) == .hostUnreachable)
        #expect(NetworkFailure(.cannotConnectToHost) == .hostUnreachable)
        #expect(NetworkFailure(.networkConnectionLost) == .connectionLost)
        #expect(NetworkFailure(.timedOut) == nil)
        #expect(NetworkFailure(.secureConnectionFailed) == nil)
        #expect(NetworkFailure(.badServerResponse) == nil)

        let offline = APIError.from(URLError(.notConnectedToInternet))
        #expect(offline.networkFailure == .noConnection)
        // Still a network error, retried and described as before.
        #expect(offline.isRetryable)
        #expect(!offline.refusesEveryRequest)
        #expect(offline.localizedDescription.hasPrefix(L("网络错误：", "Network error: ")))
        #expect(APIError.from(URLError(.timedOut)).networkFailure == nil)
        #expect(APIError.from(URLError(.timedOut)).isRetryable)
        #expect(APIError.from(URLError(.cancelled)) == .cancelled)
        #expect(APIError.http(status: 503, message: "").networkFailure == nil)
    }

    @Test func aPolicyStopsEarlyWhenToldTo() async {
        let tries = NetworkFlagCounter()
        let policy = RetryPolicy(maxAttempts: 4, baseDelay: 0.001, stopsEarly: { $0.networkFailure == .noConnection })
        await #expect(throws: APIError.self) {
            try await policy.run { _ in
                tries.add()
                throw APIError.offline(.noConnection, "offline")
            }
        }
        #expect(tries.count == 1)
        // Anything else is retried as before.
        let others = NetworkFlagCounter()
        await #expect(throws: APIError.self) {
            try await policy.run { _ in
                others.add()
                throw APIError.offline(.connectionLost, "lost")
            }
        }
        #expect(others.count == 4)
    }

    // MARK: Pipeline

    private func pipeline(_ provider: ScriptedProvider, recognizer: FakeRecognizer, network: NetworkFlag,
                          ready: Bool = true, preset: [Int: String] = [:]) -> TranscriptionPipeline {
        TranscriptionPipeline(
            client: provider, options: .init(model: "m", language: "en"),
            policy: RetryPolicy(maxAttempts: 3, baseDelay: 0.001), preset: preset,
            offline: OfflineFallbackRoute(route: OnDeviceRoute(recognizer: recognizer, preferredLanguages: ["en-US"]),
                                          isReady: { ready }, networkDown: { network.down }),
            speculate: false)
    }

    /// Three chunks: cut by 28 s at the latest.
    private let audio = LatencyAudio.tone(29) + LatencyAudio.tone(29) + LatencyAudio.tone(5)

    @Test func whenTheNetworkIsKnownDownNothingIsSent() async throws {
        let provider = ScriptedProvider { _ in nil }
        let recognizer = FakeRecognizer(answer: { _ in "local" })
        let pipeline = self.pipeline(provider, recognizer: recognizer, network: NetworkFlag(true))
        LatencyAudio.feed(pipeline, audio)
        let text = try await pipeline.finish()
        #expect(text == "local local local")
        #expect(provider.requests == 0)
        #expect(pipeline.offlineChunks() == [0, 1, 2])
        // Free: nothing for the provider's price to count.
        #expect(pipeline.requestUsages().isEmpty)
        #expect(pipeline.transcriptionCost(price: ModelPrice(perMinute: 0.006), preferReported: false).cost == 0)
    }

    @Test func aDictationGoesOverOnceTheConnectionIsGone() async throws {
        // The first chunk is transcribed; then the connection is gone (the system hasn't said so yet), and the provider
        // isn't asked again.
        let gone = NetworkFlag(false)
        let provider = ScriptedProvider { _ in gone.down ? .offline(.noConnection, "offline") : nil }
        let recognizer = FakeRecognizer(answer: { _ in "local" })
        let pipeline = self.pipeline(provider, recognizer: recognizer, network: NetworkFlag(false))
        LatencyAudio.feed(pipeline, LatencyAudio.tone(29))
        try await eventually { pipeline.chunkCounts().answered == 1 }
        gone.down = true
        // One more chunk is cut from this; it fails over before the last one is handed out.
        LatencyAudio.feed(pipeline, LatencyAudio.tone(29) + LatencyAudio.tone(5))
        try await eventually { pipeline.chunkCounts().answered == 2 }
        let text = try await pipeline.finish()
        #expect(text == "cloud local local")
        // No retries: one request that failed, and none for the chunk after it.
        #expect(provider.requests == 2)
        #expect(pipeline.offlineChunks() == [1, 2])
        #expect(pipeline.requestUsages().count == 1)
    }

    private func eventually(_ condition: () -> Bool) async throws {
        for _ in 0..<1000 where !condition() { try await Task.sleep(nanoseconds: 10_000_000) }
    }

    @Test func aDroppedConnectionIsRetriedBeforeGoingOver() async throws {
        // Dropped once (a server turning an upload down does that too): the retry gets through.
        let flaky = ScriptedProvider { $0 == 1 ? .offline(.connectionLost, "lost") : nil }
        let recognizer = FakeRecognizer()
        let first = pipeline(flaky, recognizer: recognizer, network: NetworkFlag(false))
        first.append(LatencyAudio.tone(3))
        #expect(try await first.finish() == "cloud")
        #expect(first.offlineChunks().isEmpty)
        #expect(recognizer.seen.isEmpty)

        // Dropped every time: once the retries are used up, the recognizer on this computer takes over.
        let lost = ScriptedProvider { _ in .offline(.connectionLost, "lost") }
        let second = pipeline(lost, recognizer: FakeRecognizer(answer: { _ in "local" }), network: NetworkFlag(false))
        second.append(LatencyAudio.tone(3))
        #expect(try await second.finish() == "local")
        #expect(lost.requests == 3)
        #expect(second.offlineChunks() == [0])
    }

    @Test func theProvidersOwnFailuresKeepTheirBehaviour() async throws {
        let refusing = ScriptedProvider { _ in .http(status: 500, message: "boom") }
        let recognizer = FakeRecognizer()
        let pipeline = self.pipeline(refusing, recognizer: recognizer, network: NetworkFlag(false))
        pipeline.append(LatencyAudio.tone(3))
        await #expect(throws: PipelineFailure.self) { try await pipeline.finish() }
        // Retried, then once more in the last round, and never recognised on this computer.
        #expect(refusing.requests == 6)
        #expect(recognizer.seen.isEmpty)
    }

    @Test func withoutTheModelNothingChanges() async throws {
        let offline = ScriptedProvider { _ in .offline(.noConnection, "offline") }
        let recognizer = FakeRecognizer()
        let pipeline = self.pipeline(offline, recognizer: recognizer, network: NetworkFlag(true), ready: false)
        pipeline.append(LatencyAudio.tone(3))
        await #expect(throws: PipelineFailure.self) { try await pipeline.finish() }
        // The usual retries and last round, as without the setting.
        #expect(offline.requests == 6)
        #expect(recognizer.seen.isEmpty)
        #expect(pipeline.offlineChunks().isEmpty)
    }

    @Test func withoutTheSettingNothingChanges() async throws {
        let offline = ScriptedProvider { _ in .offline(.noConnection, "offline") }
        let pipeline = TranscriptionPipeline(client: offline, options: .init(model: "m"),
                                             policy: RetryPolicy(maxAttempts: 3, baseDelay: 0.001), speculate: false)
        pipeline.append(LatencyAudio.tone(3))
        await #expect(throws: PipelineFailure.self) { try await pipeline.finish() }
        #expect(offline.requests == 6)
    }
}

/// Counts calls from inside a retried operation.
final class NetworkFlagCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    func add() { lock.withLock { value += 1 } }
    var count: Int { lock.withLock { value } }
}
