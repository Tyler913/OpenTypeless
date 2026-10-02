import Foundation
import Testing
@testable import TypelessCore

/// Answers transcription requests, recording each body; rejects the ones carrying hints when `rejectHints` is set.
final class MockTranscriber: URLProtocol {
    nonisolated(unsafe) static var bodies: [Data] = []
    nonisolated(unsafe) static var rejectHints = false
    static let lock = NSLock()

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func stopLoading() {}

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
        let reject = Self.lock.withLock {
            Self.bodies.append(body)
            return Self.rejectHints && (body.range(of: Data("\"provider\"".utf8)) != nil
                || body.range(of: Data("name=\"prompt\"".utf8)) != nil)
        }
        let status = reject ? 400 : 200
        let data = reject ? Data(#"{"error":{"message":"Provider returned 400"}}"#.utf8) : Data(#"{"text":"ok"}"#.utf8)
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!,
                            cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }

    static func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockTranscriber.self]
        return URLSession(configuration: config)
    }
}

@Suite(.serialized) struct VocabularyHintsTests {
    private let vocabulary = ["OpenTypeless", " Kwazara ", "", "opentypeless", "卓维克"]

    private func body(model: String, vocabulary: [String]) throws -> [String: Any] {
        let client = APIClient(endpoint: .openRouter(apiKey: "test"))
        let request = try client.transcriptionRequest(wav: Data([0]), options: .init(model: model, vocabulary: vocabulary), timeout: 10)
        let data = try #require(request.httpBody)
        return try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    @Test func termsAreTrimmedDedupedAndCapped() {
        #expect(VocabularyHints.terms(vocabulary) == ["OpenTypeless", "Kwazara", "卓维克"])
        #expect(VocabularyHints.terms((0..<300).map { "t\($0)" }).count == VocabularyHints.maxTerms)
    }

    @Test func promptStopsAtAWholeTerm() {
        let prompt = VocabularyHints.prompt((0..<200).map { "term\($0)" })
        #expect(prompt.count <= VocabularyHints.maxPromptCharacters)
        #expect(prompt.hasSuffix(prompt.split(separator: ",").last.map(String.init) ?? ""))
        #expect(!prompt.hasSuffix(","))
    }

    @Test func microsoftGetsAPhraseList() throws {
        let provider = try #require(try body(model: "microsoft/mai-transcribe-2", vocabulary: vocabulary)["provider"] as? [String: Any])
        let azure = try #require((provider["options"] as? [String: Any])?["azure"] as? [String: Any])
        #expect((azure["phraseList"] as? [String: Any])?["phrases"] as? [String] == ["OpenTypeless", "Kwazara", "卓维克"])
    }

    @Test func openAIModelsGetAPrompt() throws {
        for model in ["openai/gpt-4o-transcribe", "openai/gpt-4o-mini-transcribe", "openai/whisper-1"] {
            let provider = try #require(try body(model: model, vocabulary: vocabulary)["provider"] as? [String: Any])
            let openai = try #require((provider["options"] as? [String: Any])?["openai"] as? [String: Any])
            #expect(openai["prompt"] as? String == "OpenTypeless, Kwazara, 卓维克")
        }
    }

    @Test func assemblyAIGetsKeyterms() throws {
        let provider = try #require(try body(model: "assemblyai/universal-3-5-pro", vocabulary: vocabulary)["provider"] as? [String: Any])
        let options = try #require((provider["options"] as? [String: Any])?["assemblyai"] as? [String: Any])
        #expect(options["keyterms_prompt"] as? [String] == ["OpenTypeless", "Kwazara", "卓维克"])
    }

    @Test func otherModelsAndEmptyVocabularyGetNoHints() throws {
        // Whisper large served by Groq, DeepInfra or Together ignores or rejects the OpenAI field; Gemini rejects `prompt`.
        for model in ["openai/whisper-large-v3", "google/gemini-3.5-transcribe", "deepgram/nova-3", "nvidia/parakeet-tdt-0.6b-v3"] {
            #expect(try body(model: model, vocabulary: vocabulary)["provider"] == nil)
        }
        #expect(try body(model: "microsoft/mai-transcribe-2", vocabulary: [" ", ""])["provider"] == nil)
    }

    @Test func directOpenAIAndGroqSendAPromptField() {
        #expect(VocabularyHints.multipartPrompt(provider: .openai, model: "gpt-4o-transcribe", vocabulary: vocabulary) == "OpenTypeless, Kwazara, 卓维克")
        #expect(VocabularyHints.multipartPrompt(provider: .groq, model: "whisper-large-v3-turbo", vocabulary: vocabulary) != nil)
        #expect(VocabularyHints.multipartPrompt(provider: .siliconflow, model: "FunAudioLLM/SenseVoiceSmall", vocabulary: vocabulary) == nil)
        #expect(VocabularyHints.multipartPrompt(provider: .custom, model: "whisper", vocabulary: vocabulary) == nil)
        let form = MultipartForm.transcription(wav: Data([0]), options: .init(model: "m"), prompt: "OpenTypeless")
        #expect(String(decoding: form.body, as: UTF8.self).contains("name=\"prompt\"\r\n\r\nOpenTypeless\r\n"))
    }

    @Test func aRejectedHintIsSentAgainWithoutIt() async throws {
        MockTranscriber.lock.withLock { MockTranscriber.bodies = []; MockTranscriber.rejectHints = true }
        defer { MockTranscriber.lock.withLock { MockTranscriber.rejectHints = false } }
        let client = APIClient(endpoint: .openRouter(apiKey: "test"), session: MockTranscriber.session())
        let text = try await client.transcribe(wav: Data([0]), options: .init(model: "microsoft/mai-transcribe-2", vocabulary: vocabulary),
                                               timeout: 10)
        #expect(text == "ok")
        let bodies = MockTranscriber.lock.withLock { MockTranscriber.bodies }
        #expect(bodies.count == 2)
        let retry = try #require(JSONSerialization.jsonObject(with: bodies[1]) as? [String: Any])
        #expect(retry["provider"] == nil)
    }

    @Test func aRequestWithoutHintsIsNotRepeated() async throws {
        MockTranscriber.lock.withLock { MockTranscriber.bodies = []; MockTranscriber.rejectHints = false }
        let client = APIClient(endpoint: .openRouter(apiKey: "test"), session: MockTranscriber.session())
        _ = try await client.transcribe(wav: Data([0]), options: .init(model: "deepgram/nova-3", vocabulary: vocabulary), timeout: 10)
        #expect(MockTranscriber.lock.withLock { MockTranscriber.bodies.count } == 1)
    }
}
