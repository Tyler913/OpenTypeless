import Foundation
import Testing
@testable import TypelessCore

/// Fake chat server whose behaviour depends on the requested model: how long it waits before
/// answering, and whether it fails. Separate from MockOpenRouter so it can't race those tests.
final class MockChatServer: URLProtocol {
    struct Behaviour { var delay: Double = 0; var status = 200; var text = "ok" }
    nonisolated(unsafe) static var behaviours: [String: Behaviour] = [:]
    nonisolated(unsafe) static var requested: [String] = []
    static let lock = NSLock()

    private var cancelled = false

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func stopLoading() { Self.lock.withLock { cancelled = true } }

    override func startLoading() {
        var body = request.httpBody ?? Data()
        if body.isEmpty, let stream = request.httpBodyStream {
            stream.open()
            var buffer = [UInt8](repeating: 0, count: 65536)
            while stream.hasBytesAvailable {
                let n = stream.read(&buffer, maxLength: buffer.count)
                if n <= 0 { break }
                body.append(buffer, count: n)
            }
            stream.close()
        }
        let model = (try? JSONSerialization.jsonObject(with: body) as? [String: Any])?["model"] as? String ?? ""
        let behaviour = Self.lock.withLock {
            Self.requested.append(model)
            return Self.behaviours[model] ?? Behaviour()
        }
        DispatchQueue.global().asyncAfter(deadline: .now() + behaviour.delay) { [self] in
            guard !Self.lock.withLock({ cancelled }) else { return }
            let data = behaviour.status == 200
                ? Data("data: {\"choices\":[{\"delta\":{\"content\":\"\(behaviour.text)\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n".utf8)
                : Data(#"{"error":{"message":"upstream down"}}"#.utf8)
            let response = HTTPURLResponse(url: request.url!, statusCode: behaviour.status, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        }
    }

    static func reset(_ behaviours: [String: Behaviour]) {
        lock.withLock {
            self.behaviours = behaviours
            requested = []
        }
    }

    static func route(_ model: String) -> PolishRoute {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockChatServer.self]
        let client = APIClient(endpoint: .openRouter(apiKey: "k"), session: URLSession(configuration: config))
        return PolishRoute(client: client, options: PolishOptions(model: model))
    }
}

@Suite(.serialized) struct HedgedPolishTests {
    @Test func fastPrimaryNeverStartsBackup() async throws {
        MockChatServer.reset(["primary": .init(text: "A"), "backup": .init(text: "B")])
        let outcome = try await HedgedPolish.run(transcript: "x", primary: MockChatServer.route("primary"),
                                                 backup: MockChatServer.route("backup"), hedgeDelay: 0.5)
        #expect(outcome.result.text == "A")
        #expect(!outcome.usedBackup)
        #expect(MockChatServer.lock.withLock { MockChatServer.requested } == ["primary"])
    }

    @Test func slowPrimaryIsOvertakenByBackup() async throws {
        MockChatServer.reset(["primary": .init(delay: 2, text: "A"), "backup": .init(text: "B")])
        let outcome = try await HedgedPolish.run(transcript: "x", primary: MockChatServer.route("primary"),
                                                 backup: MockChatServer.route("backup"), hedgeDelay: 0.2)
        #expect(outcome.result.text == "B")
        #expect(outcome.usedBackup && outcome.model == "backup")
        #expect(outcome.totalSeconds < 1.5)
    }

    @Test func partialTextComesOnlyFromTheStreamThatWon() async throws {
        MockChatServer.reset(["primary": .init(delay: 2, text: "A"), "backup": .init(text: "B")])
        let partials = PartialLog()
        let outcome = try await HedgedPolish.run(transcript: "x", primary: MockChatServer.route("primary"),
                                                 backup: MockChatServer.route("backup"), hedgeDelay: 0.2,
                                                 onPartial: { partials.add($0) })
        #expect(outcome.result.text == "B")
        #expect(partials.all == ["B"])
    }

    @Test func primaryFailureStartsBackupWithoutWaiting() async throws {
        MockChatServer.reset(["primary": .init(status: 503), "backup": .init(text: "B")])
        let outcome = try await HedgedPolish.run(transcript: "x", primary: MockChatServer.route("primary"),
                                                 backup: MockChatServer.route("backup"), hedgeDelay: 5)
        #expect(outcome.result.text == "B")
        #expect(outcome.totalSeconds < 2)
    }

    @Test func slowBackupDoesNotBeatPrimaryThatAnswersFirst() async throws {
        MockChatServer.reset(["primary": .init(delay: 0.4, text: "A"), "backup": .init(delay: 2, text: "B")])
        let outcome = try await HedgedPolish.run(transcript: "x", primary: MockChatServer.route("primary"),
                                                 backup: MockChatServer.route("backup"), hedgeDelay: 0.1)
        #expect(outcome.result.text == "A")
        #expect(MockChatServer.lock.withLock { MockChatServer.requested }.sorted() == ["backup", "primary"])
    }

    @Test func bothFailingThrowsThePrimaryError() async {
        MockChatServer.reset(["primary": .init(status: 401), "backup": .init(status: 503)])
        await #expect(throws: APIError.self) {
            try await HedgedPolish.run(transcript: "x", primary: MockChatServer.route("primary"),
                                       backup: MockChatServer.route("backup"), hedgeDelay: 0.1)
        }
    }

    @Test func worksWithoutBackup() async throws {
        MockChatServer.reset(["primary": .init(delay: 0.3, text: "A")])
        let outcome = try await HedgedPolish.run(transcript: "x", primary: MockChatServer.route("primary"),
                                                 backup: nil, hedgeDelay: 0.05)
        #expect(outcome.result.text == "A")
    }
}

@Suite struct PolishMetricsTests {
    @Test func translatedEnglishLowersRetention() {
        let input = "有没有办法设置一个 shortcut，然后切换 dark mode"
        #expect(PolishMetrics.latinRetention(input: input, output: "有没有办法设置一个 shortcut，切换 dark mode？") == 1)
        #expect(PolishMetrics.latinRetention(input: input, output: "有没有办法设置一个快捷键，切换深色模式？") == 0)
        // Recognition fixes that join words still count as kept.
        #expect(PolishMetrics.latinRetention(input: "我用 swift UI 写的界面", output: "我用 SwiftUI 写的界面。") == 1)
        #expect(PolishMetrics.latinRetention(input: "Send it to Sarah", output: "Send it to Sarah.") == nil)
    }

    @Test func similarityIgnoresPunctuationAndSpacing() {
        #expect(PolishMetrics.similarity("用 SwiftUI 写", "用SwiftUI写。") == 1)
        #expect(PolishMetrics.similarity("abcd", "abcf") == 0.75)
    }
}

private final class PartialLog: @unchecked Sendable {
    private let lock = NSLock()
    private var texts: [String] = []
    func add(_ text: String) { lock.withLock { texts.append(text) } }
    var all: [String] { lock.withLock { texts } }
}
