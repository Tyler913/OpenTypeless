import Foundation
import Testing
@testable import TypelessCore

/// Answers every request with `answer`, recording each one (its own handler, so these tests don't share one with the
/// pipeline tests).
final class MockProviders: URLProtocol {
    struct Sent {
        let request: URLRequest
        let body: Data
        var text: String { String(decoding: body, as: UTF8.self) }
        var path: String { request.url?.path ?? "" }
        var query: String { request.url.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.percentEncodedQuery } ?? "" }
        func header(_ name: String) -> String? { request.value(forHTTPHeaderField: name) }
    }

    nonisolated(unsafe) static var answer: ((Sent) -> (Int, String))?
    nonisolated(unsafe) static var sent: [Sent] = []
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
        let record = Sent(request: request, body: body)
        let (status, text) = Self.lock.withLock { () -> (Int, String) in
            Self.sent.append(record)
            return Self.answer?(record) ?? (500, "no handler")
        }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!,
                            cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(text.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    static func serve(_ answer: @escaping (Sent) -> (Int, String)) {
        lock.withLock {
            sent = []
            self.answer = answer
        }
    }

    static var requests: [Sent] { lock.withLock { sent } }

    static func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockProviders.self]
        return URLSession(configuration: config)
    }
}

@Suite(.serialized) struct ProvidersTests {
    private let vocabulary = ["OpenTypeless", " Kwazara ", "", "opentypeless", "C++", "卓维克"]

    private func client(_ id: ProviderID, key: String = "k") -> APIClient {
        APIClient(endpoint: .preset(id, apiKey: key), session: MockProviders.session())
    }

    private struct Cases: Decodable {
        struct Provider: Decodable {
            let id: String
            let baseURL: String
            let stt: Bool
            let chat: Bool
            let sttFormat: String
            let requiresKey: Bool
            let sttModel: String
            let chatModel: String
            let backupSttModel: String
            let backupChatModel: String
            let keysURL: String?
            let group: String
            let temperature: Bool
            let streamOptions: Bool
            let minimisesReasoning: Bool
        }
        let providers: [Provider]
    }

    /// testdata/provider-cases.json, shared with the Windows tests so both apps offer the same providers.
    private func caseFile() throws -> Data {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        return try Data(contentsOf: root.appendingPathComponent("testdata/provider-cases.json"))
    }

    // MARK: The table

    @Test func everyProviderMatchesTheSharedTable() throws {
        let cases = try JSONDecoder().decode(Cases.self, from: caseFile()).providers
        #expect(cases.map(\.id) == ProviderID.allCases.map(\.rawValue))
        for c in cases {
            let id = try #require(ProviderID(rawValue: c.id))
            #expect(id.defaultBaseURL == c.baseURL, "\(c.id)")
            #expect(id.supportsSTT == c.stt, "\(c.id)")
            #expect(id.supportsChat == c.chat, "\(c.id)")
            #expect(id.sttFormat.rawValue == c.sttFormat, "\(c.id)")
            #expect(id.requiresKey == c.requiresKey, "\(c.id)")
            #expect(id.defaultSTTModel == c.sttModel, "\(c.id)")
            #expect(id.defaultChatModel == c.chatModel, "\(c.id)")
            #expect(id.defaultBackupSTTModel == c.backupSttModel, "\(c.id)")
            #expect(id.defaultBackupChatModel == c.backupChatModel, "\(c.id)")
            #expect(id.keysURL?.absoluteString == c.keysURL, "\(c.id)")
            #expect(id.group.rawValue == c.group, "\(c.id)")
            #expect(id.chatSendsTemperature == c.temperature, "\(c.id)")
            #expect(id.chatSendsStreamOptions == c.streamOptions, "\(c.id)")
            #expect(id.minimisesReasoning == c.minimisesReasoning, "\(c.id)")
        }
    }

    @Test func everyProviderCanBeSetUp() throws {
        for id in ProviderID.allCases {
            #expect(!id.displayName.isEmpty && !id.keyPlaceholder.isEmpty)
            #expect(id.supportsSTT || id.supportsChat)
            guard id != .custom else { continue }
            let url = try #require(URL(string: id.defaultBaseURL), "\(id)")
            #expect(url.scheme == (id.isLocal ? "http" : "https"), "\(id)")
            if id.isLocal {
                #expect(url.host == "localhost" && id.keysURL == nil && !id.requiresKey)
                continue
            }
            // A provider in the cloud: a key, a page to get one from, and a model for each step it serves.
            #expect(id.requiresKey, "\(id)")
            #expect(id.keysURL?.scheme == "https", "\(id)")
            #expect(id.defaultSTTModel.isEmpty == !id.supportsSTT, "\(id)")
            #expect(id.defaultChatModel.isEmpty == !id.supportsChat, "\(id)")
        }
    }

    @Test func savedValuesKeepTheirMeaning() {
        // Settings and credentials.json store the raw values; nothing that was saved before changes.
        #expect([ProviderID.openrouter, .openai, .groq, .siliconflow, .deepseek, .custom].map(\.rawValue)
                == ["openrouter", "openai", "groq", "siliconflow", "deepseek", "custom"])
        #expect(ProviderID(rawValue: "nosuchprovider") == nil)
        #expect(Set(ProviderID.allCases.map(\.rawValue)).count == ProviderID.allCases.count)
    }

    // MARK: Clean-up

    @Test func cleanUpTurnsReasoningOffTheProvidersOwnWay() throws {
        let object = try #require(JSONSerialization.jsonObject(with: caseFile()) as? [String: Any])
        let cases = try #require(object["reasoning"] as? [[String: Any]])
        #expect(cases.count >= 10)
        let fields = ["reasoning_effort", "thinking", "enable_thinking", "reasoning"]
        for c in cases {
            let id = try #require(ProviderID(rawValue: c["provider"] as? String ?? ""))
            let model = try #require(c["model"] as? String)
            let expected = try #require(c["fields"] as? [String: Any])
            let body = client(id).polishBody(transcript: "x", options: PolishOptions(model: model), includeOptional: true)
            for field in fields {
                let sent = body[field] as? NSObject
                let wanted = expected[field] as? NSObject
                #expect(sent.map { $0.isEqual(wanted) } ?? (wanted == nil), "\(id) \(model) \(field)")
            }
            // The bare request sent after a 400 carries none of them.
            let bare = client(id).polishBody(transcript: "x", options: PolishOptions(model: model), includeOptional: false)
            #expect(fields.allSatisfy { bare[$0] == nil }, "\(id) \(model)")
        }
    }

    @Test func anthropicAlwaysGetsALimitAndNoTemperature() {
        let full = client(.anthropic).polishBody(transcript: "hello", options: PolishOptions(model: "claude-haiku-5-5"), includeOptional: true)
        #expect(full["temperature"] == nil && full["max_tokens"] as? Int == 1024 && full["stream_options"] != nil)
        let bare = client(.anthropic).polishBody(transcript: "hello", options: PolishOptions(model: "claude-haiku-5-5"), includeOptional: false)
        #expect(bare["max_tokens"] as? Int == 1024)
        // Elsewhere the bare request stays bare.
        #expect(client(.gemini).polishBody(transcript: "hello", options: PolishOptions(model: "gemini-3.8-flash"),
                                           includeOptional: false)["max_tokens"] == nil)
        #expect(client(.mistral).polishBody(transcript: "hello", options: PolishOptions(model: "mistral-small-latest"),
                                            includeOptional: true)["stream_options"] == nil)
    }

    @Test func aModelThatCantStopThinkingGetsTheBareRequest() async throws {
        MockProviders.serve { sent in
            sent.text.contains("\"thinking\"")
                ? (400, #"{"error":{"type":"invalid_request_error","message":"thinking.type: disabled is not supported for this model"}}"#)
                : (200, "data: {\"choices\":[{\"delta\":{\"content\":\"Hello.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")
        }
        let result = try await client(.anthropic, key: "sk-ant-test").polish(transcript: "hello", options: PolishOptions(model: "claude-opus-5-5"))
        #expect(result.text == "Hello.")
        let sent = MockProviders.requests
        #expect(sent.count == 2)
        #expect(sent.first?.request.url?.absoluteString == "https://api.anthropic.com/v1/chat/completions")
        #expect(sent.first?.header("Authorization") == "Bearer sk-ant-test")
        #expect(sent.last.map { !$0.text.contains("\"thinking\"") && $0.text.contains("\"max_tokens\"") } == true)
    }

    // MARK: Deepgram

    @Test func deepgramGetsTheAudioAsTheBody() async throws {
        MockProviders.serve { _ in
            (200, #"{"metadata":{"request_id":"r","duration":2.5},"results":{"channels":[{"alternatives":[{"transcript":"Hello, OpenTypeless.","confidence":0.99}]}]}}"#)
        }
        let wav = WAV.encode(samples: [1, 2, 3])
        let result = try await client(.deepgram, key: "dg-key").transcribeDetailed(
            wav: wav, options: .init(model: "nova-3", vocabulary: vocabulary), timeout: 5)
        #expect(result.text == "Hello, OpenTypeless.")
        #expect(result.usage?.audioSeconds == 2.5)
        let sent = try #require(MockProviders.requests.first)
        #expect(MockProviders.requests.count == 1)
        #expect(sent.request.url?.host == "api.deepgram.com" && sent.path == "/v1/listen")
        #expect(sent.header("Authorization") == "Token dg-key")
        #expect(sent.header("Content-Type") == "audio/wav")
        #expect(sent.body == wav)
        #expect(sent.query == "model=nova-3&smart_format=true&punctuate=true&detect_language=true"
                + "&keyterm=OpenTypeless&keyterm=Kwazara&keyterm=C%2B%2B&keyterm=%E5%8D%93%E7%BB%B4%E5%85%8B")
    }

    @Test func deepgramTakesTheSpokenLanguageAndNoKeytermsBeforeNova3() async throws {
        MockProviders.serve { _ in (200, #"{"results":{"channels":[{"alternatives":[{"transcript":"你好"}]}]}}"#) }
        let result = try await client(.deepgram).transcribeDetailed(
            wav: Data([0]), options: .init(model: "nova-2", language: "zh", vocabulary: vocabulary), timeout: 5)
        #expect(result.text == "你好" && result.usage == nil)
        #expect(MockProviders.requests.first?.query == "model=nova-2&smart_format=true&punctuate=true&language=zh")
    }

    struct DeepgramError: Sendable {
        let status: Int
        let body: String
        let message: String
        let retryable: Bool
        let refusesAll: Bool
    }

    @Test(arguments: [
        DeepgramError(status: 401, body: #"{"err_code":"INVALID_AUTH","err_msg":"Invalid credentials.","request_id":"r"}"#,
                      message: "Invalid credentials.", retryable: false, refusesAll: true),
        DeepgramError(status: 402, body: #"{"err_code":"ASR_PAYMENT_REQUIRED","err_msg":"Project does not have enough credits.","request_id":"r"}"#,
                      message: "Project does not have enough credits.", retryable: false, refusesAll: true),
        DeepgramError(status: 403, body: #"{"err_code":"INSUFFICIENT_PERMISSIONS","err_msg":"Project does not have access to the requested model."}"#,
                      message: "Project does not have access to the requested model.", retryable: false, refusesAll: true),
        DeepgramError(status: 429, body: #"{"err_code":"TOO_MANY_REQUESTS","err_msg":"Too many requests."}"#,
                      message: "Too many requests.", retryable: true, refusesAll: false),
        DeepgramError(status: 503, body: "Service Unavailable", message: "Service Unavailable", retryable: true, refusesAll: false),
    ])
    func deepgramErrorsKeepTheirMeaning(_ c: DeepgramError) async {
        let (status, body, message, retryable, refusesAll) = (c.status, c.body, c.message, c.retryable, c.refusesAll)
        MockProviders.serve { _ in (status, body) }
        do {
            _ = try await client(.deepgram).transcribe(wav: Data([0]), options: .init(model: "nova-3"), timeout: 5)
            Issue.record("expected \(status)")
        } catch {
            let error = APIError.from(error)
            #expect(error == .http(status: status, message: message))
            #expect(error.isRetryable == retryable && error.refusesEveryRequest == refusesAll)
        }
    }

    @Test func deepgramWithoutATranscriptIsABadResponse() async {
        MockProviders.serve { _ in (200, #"{"metadata":{},"results":{"channels":[]}}"#) }
        do {
            _ = try await client(.deepgram).transcribe(wav: Data([0]), options: .init(model: "nova-3"), timeout: 5)
            Issue.record("expected a bad response")
        } catch {
            guard case .badResponse = APIError.from(error) else { Issue.record("\(error)"); return }
            #expect(APIError.from(error).isRetryable)
        }
    }

    @Test func aRejectedKeytermIsSentAgainWithout() async throws {
        MockProviders.serve { sent in
            sent.query.contains("keyterm")
                ? (400, #"{"err_code":"Bad Request","err_msg":"Invalid keyterm."}"#)
                : (200, #"{"results":{"channels":[{"alternatives":[{"transcript":"ok"}]}]}}"#)
        }
        let text = try await client(.deepgram).transcribe(wav: Data([0]), options: .init(model: "nova-3", vocabulary: vocabulary), timeout: 5)
        #expect(text == "ok")
        let sent = MockProviders.requests
        #expect(sent.count == 2 && sent.last?.query.contains("keyterm") == false)
    }

    @Test func deepgramKeytermsStayUnderItsTokenLimit() {
        let terms = (0..<100).map { "Kwazara product name number \($0)" }
        let kept = VocabularyHints.keyterms(terms)
        #expect((1..<100).contains(kept.count))
        #expect(Array(terms.prefix(kept.count)) == kept)
        #expect(kept.reduce(0) { $0 + TokenEstimate.count($1) } <= VocabularyHints.maxKeytermTokens)
    }

    // MARK: ElevenLabs

    @Test func elevenLabsGetsAScribeForm() async throws {
        MockProviders.serve { _ in (200, #"{"language_code":"eng","language_probability":0.98,"text":"Hello there.","words":[]}"#) }
        let terms = ["OpenTypeless", String(repeating: "x", count: 51), "one two three four five six", "a<b", "Kwazara Labs"]
        let text = try await client(.elevenlabs, key: "sk_test").transcribe(
            wav: Data([1, 2]), options: .init(model: "scribe_v2", language: "en", vocabulary: terms), timeout: 5)
        #expect(text == "Hello there.")
        let sent = try #require(MockProviders.requests.first)
        #expect(sent.request.url?.absoluteString == "https://api.elevenlabs.io/v1/speech-to-text")
        #expect(sent.header("xi-api-key") == "sk_test" && sent.header("Authorization") == nil)
        #expect(sent.header("Content-Type")?.hasPrefix("multipart/form-data; boundary=") == true)
        let form = sent.text
        #expect(form.contains("name=\"model_id\"\r\n\r\nscribe_v2\r\n"))
        #expect(form.contains(#"name="file"; filename="audio.wav""#))
        #expect(form.contains("name=\"language_code\"\r\n\r\nen\r\n"))
        #expect(form.contains("name=\"tag_audio_events\"\r\n\r\nfalse\r\n"))
        #expect(!form.contains("name=\"model\""))
        // Only the terms ElevenLabs takes: at most 50 characters and five words, none of <>{}[]\.
        #expect(form.contains("name=\"keyterms\"\r\n\r\nOpenTypeless\r\n"))
        #expect(form.contains("name=\"keyterms\"\r\n\r\nKwazara Labs\r\n"))
        #expect(form.components(separatedBy: "name=\"keyterms\"").count - 1 == 2)
    }

    struct ScribeError: Sendable {
        let status: Int
        let body: String
        let classified: Int
        let message: String
    }

    @Test(arguments: [
        ScribeError(status: 401, body: #"{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}"#, classified: 401,
                    message: "Invalid API key"),
        ScribeError(status: 401, body: #"{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota."}}"#,
                    classified: 402, message: "This request exceeds your quota."),
        ScribeError(status: 429, body: #"{"detail":{"status":"too_many_concurrent_requests","message":"Too many concurrent requests."}}"#,
                    classified: 429, message: "Too many concurrent requests."),
        ScribeError(status: 500, body: #"{"detail":{"status":"internal_error","message":"Something went wrong"}}"#, classified: 500,
                    message: "Something went wrong"),
    ])
    func elevenLabsErrorsKeepTheirMeaning(_ c: ScribeError) async {
        let (status, body, classified, message) = (c.status, c.body, c.classified, c.message)
        MockProviders.serve { _ in (status, body) }
        do {
            _ = try await client(.elevenlabs).transcribe(wav: Data([0]), options: .init(model: "scribe_v2"), timeout: 5)
            Issue.record("expected \(status)")
        } catch {
            let error = APIError.from(error)
            #expect(error == .http(status: classified, message: message))
            #expect(error.isRetryable == (classified == 429 || classified >= 500))
        }
    }

    @Test func aKeytermThatFailsValidationIsSentAgainWithout() async throws {
        MockProviders.serve { sent in
            sent.text.contains("name=\"keyterms\"")
                ? (422, #"{"detail":[{"loc":["body","keyterms",0],"msg":"Keyterm is not supported","type":"value_error"}]}"#)
                : (200, #"{"text":"ok","words":[]}"#)
        }
        let text = try await client(.elevenlabs).transcribe(wav: Data([0]), options: .init(model: "scribe_v2", vocabulary: vocabulary),
                                                            timeout: 5)
        #expect(text == "ok")
        let sent = MockProviders.requests
        #expect(sent.count == 2 && sent.last?.text.contains("keyterms") == false)
    }

    @Test func validationErrorsNameTheField() {
        let data = Data(#"{"detail":[{"loc":["body","stream_options"],"msg":"Extra inputs are not permitted","type":"extra_forbidden"}]}"#.utf8)
        #expect(APIClient.errorMessage(from: data) == "body.stream_options: Extra inputs are not permitted")
    }

    // MARK: Mistral, Zhipu and local servers

    @Test func mistralGetsContextBiasForEachTerm() async throws {
        MockProviders.serve { _ in
            (200, #"{"model":"voxtral-mini-transcribe-3","text":"Bonjour.","usage":{"prompt_audio_seconds":3,"prompt_tokens":3,"completion_tokens":5}}"#)
        }
        let result = try await client(.mistral, key: "m-key").transcribeDetailed(
            wav: Data([0]), options: .init(model: "voxtral-mini-transcribe-3", language: "fr", vocabulary: vocabulary), timeout: 5)
        #expect(result.text == "Bonjour.")
        let sent = try #require(MockProviders.requests.first)
        #expect(sent.request.url?.absoluteString == "https://api.mistral.ai/v1/audio/transcriptions")
        #expect(sent.header("Authorization") == "Bearer m-key")
        #expect(sent.text.contains("name=\"context_bias\"\r\n\r\nOpenTypeless\r\n"))
        #expect(sent.text.contains("name=\"context_bias\"\r\n\r\n卓维克\r\n"))
        #expect(sent.text.components(separatedBy: "name=\"context_bias\"").count - 1 == 4)
        #expect(!sent.text.contains("name=\"prompt\""))
    }

    @Test func zhipuAndLocalServersGetAPlainForm() async throws {
        MockProviders.serve { _ in (200, #"{"id":"x","text":"你好"}"#) }
        #expect(try await client(.zhipu).transcribe(wav: Data([0]), options: .init(model: "glm-asr-2512", vocabulary: vocabulary),
                                                   timeout: 5) == "你好")
        // Ollama needs no key, and gets none.
        #expect(try await client(.ollama, key: "").transcribe(wav: Data([0]), options: .init(model: "whisper"), timeout: 5) == "你好")
        let sent = MockProviders.requests
        #expect(sent.count == 2)
        #expect(sent.first?.request.url?.absoluteString == "https://open.bigmodel.cn/api/paas/v4/audio/transcriptions")
        #expect(sent.first.map { !$0.text.contains("context_bias") && !$0.text.contains("name=\"prompt\"") } == true)
        #expect(sent.last?.request.url?.absoluteString == "http://localhost:11434/v1/audio/transcriptions")
        #expect(sent.last?.header("Authorization") == nil)
    }

    @Test func aCloudProviderWithoutAKeyIsNotAsked() async {
        MockProviders.serve { _ in (200, "{}") }
        for id in [ProviderID.deepgram, .elevenlabs, .mistral] {
            await #expect(throws: APIError.missingAPIKey(id.displayName)) {
                try await client(id, key: " ").transcribe(wav: Data([0]), options: .init(model: id.defaultSTTModel), timeout: 5)
            }
            await #expect(throws: APIError.missingAPIKey(id.displayName)) { try await client(id, key: "").verifyCredentials() }
        }
        #expect(MockProviders.requests.isEmpty)
    }

    // MARK: Test button and model lists

    @Test func theKeyCheckAsksEachProviderItsOwnWay() async throws {
        MockProviders.serve { sent in
            switch sent.path {
            case "/v1/projects": return (200, #"{"projects":[{"project_id":"p","name":"Default"}]}"#)
            case "/v1/user": return (200, #"{"subscription":{"tier":"free"}}"#)
            case "/v1/models": return (200, #"{"data":[{"id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5","type":"model"}],"has_more":false}"#)
            default: return (404, "Not Found")
            }
        }
        #expect(try await client(.deepgram, key: "dg").verifyCredentials() == "Key is valid")
        #expect(try await client(.elevenlabs, key: "el").verifyCredentials() == "Key is valid")
        #expect(try await client(.anthropic, key: "sk-ant").verifyCredentials() == "Connected — 1 models available")
        let sent = MockProviders.requests
        #expect(sent.count == 3)
        #expect(sent[0].header("Authorization") == "Token dg")
        #expect(sent[1].request.url?.absoluteString == "https://api.elevenlabs.io/v1/user" && sent[1].header("xi-api-key") == "el")
        #expect(sent[2].request.url?.absoluteString == "https://api.anthropic.com/v1/models?limit=1000")
        #expect(sent[2].header("x-api-key") == "sk-ant" && sent[2].header("anthropic-version") == "2023-06-01")
        #expect(sent[2].header("Authorization") == nil)
        #expect(sent.allSatisfy { ($0.request.httpMethod ?? "GET") == "GET" })
    }

    @Test func volcengineWithoutAModelListStillConnects() async throws {
        MockProviders.serve { _ in (404, #"{"error":{"code":"NotFound","message":"The requested resource was not found."}}"#) }
        #expect(try await client(.volcengine).verifyCredentials() == "Connected")
        // Elsewhere a missing list is an error: the base URL is probably wrong.
        await #expect(throws: APIError.http(status: 404, message: "The requested resource was not found.")) {
            try await client(.moonshot).verifyCredentials()
        }
        // A bad key is still a bad key.
        MockProviders.serve { _ in (401, #"{"error":{"code":"AuthenticationError","message":"The API key is invalid."}}"#) }
        await #expect(throws: APIError.http(status: 401, message: "The API key is invalid.")) {
            try await client(.volcengine).verifyCredentials()
        }
    }

    @Test func modelListsReadEachProvidersIds() async throws {
        MockProviders.serve { _ in
            (200, #"{"object":"list","data":[{"id":"models/gemini-3.8-flash","object":"model"},{"id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5"}]}"#)
        }
        let models = try await client(.gemini).listModels()
        #expect(models.map(\.id) == ["gemini-3.8-flash", "claude-haiku-5-5"])
        #expect(models.last?.name == "Claude Haiku 5.5")
        #expect(try await client(.deepgram).listModels().isEmpty)
        #expect(try await client(.elevenlabs).listModels().isEmpty)
        let sent = MockProviders.requests
        #expect(sent.count == 1)
        #expect(sent.first?.request.url?.absoluteString == "https://generativelanguage.googleapis.com/v1beta/openai/models")
        #expect(APIClient.ModelInfo(id: "voxtral-mini-transcribe-3", name: "Voxtral").looksLikeSpeechModel)
    }

    // MARK: Vocabulary hints

    @Test func hintsGoOnlyWhereTheProviderDocumentsThem() {
        #expect(VocabularyHints.style(provider: .mistral, model: "voxtral-mini-transcribe-3") == .contextBias)
        #expect(VocabularyHints.style(provider: .mistral, model: "mistral-small-latest") == nil)
        #expect(VocabularyHints.style(provider: .deepgram, model: "nova-3-medical") == .keyterm)
        #expect(VocabularyHints.style(provider: .deepgram, model: "nova-2") == nil)
        #expect(VocabularyHints.style(provider: .elevenlabs, model: "scribe_v2") == .keyterms)
        for id in [ProviderID.zhipu, .ollama, .gemini, .anthropic, .lmstudio] {
            #expect(VocabularyHints.direct(provider: id, model: id.defaultSTTModel, vocabulary: vocabulary) == nil, "\(id)")
        }
        #expect(VocabularyHints.direct(provider: .elevenlabs, model: "scribe_v2", vocabulary: vocabulary)
                == .keyterms(["OpenTypeless", "Kwazara", "C++", "卓维克"]))
        #expect(VocabularyHints.direct(provider: .elevenlabs, model: "scribe_v2", vocabulary: [String(repeating: "x", count: 60)]) == nil)
    }
}
