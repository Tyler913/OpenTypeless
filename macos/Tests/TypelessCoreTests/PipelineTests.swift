import Foundation
import Testing
@testable import TypelessCore

/// Fake OpenRouter: answers each STT request with the chunk's sample count so ordering can be checked,
/// and injects failures on demand.
final class MockOpenRouter: URLProtocol {
    nonisolated(unsafe) static var handler: ((URLRequest, Data) -> (Int, Data))?
    nonisolated(unsafe) static var lock = NSLock()

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
        let (status, data) = Self.handler!(request, body)
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }

    static func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MockOpenRouter.self]
        return URLSession(configuration: config)
    }
}

@Suite(.serialized) struct PipelineTests {
    private func speech(seconds: Double) -> [Int16] {
        let n = AudioFormat.sampleCount(forSeconds: seconds)
        return (0..<n).map { i in
            // Loud tone with a short pause every 7 seconds.
            let t = AudioFormat.seconds(forSampleCount: i)
            return t.truncatingRemainder(dividingBy: 7) < 0.4 ? 0 : Int16(6000 * sin(Double(i) * 0.2))
        }
    }

    @Test func longRecordingSurvivesTransientFailures() async throws {
        var attemptsPerChunk: [String: Int] = [:]
        MockOpenRouter.handler = { _, body in
            let json = try! JSONSerialization.jsonObject(with: body) as! [String: Any]
            let audio = json["input_audio"] as! [String: String]
            let wav = Data(base64Encoded: audio["data"]!)!
            let samples = WAV.decodeSamples(wav)!
            let key = audio["data"]!
            MockOpenRouter.lock.lock()
            attemptsPerChunk[key, default: 0] += 1
            let attempt = attemptsPerChunk[key]!
            MockOpenRouter.lock.unlock()
            // Every chunk fails with 503 on its first attempt.
            if attempt == 1 { return (503, Data(#"{"error":{"message":"upstream timeout"}}"#.utf8)) }
            return (200, try! JSONSerialization.data(withJSONObject: ["text": "[\(samples.count)]"]))
        }

        let client = APIClient(endpoint: .openRouter(apiKey: "test"), session: MockOpenRouter.session())
        let pipeline = TranscriptionPipeline(
            client: client,
            options: .init(model: "m"),
            policy: RetryPolicy(maxAttempts: 3, baseDelay: 0.01)
        )
        let audio = speech(seconds: 130)
        let block = AudioFormat.sampleCount(forSeconds: 0.1)
        var i = 0
        while i < audio.count {
            pipeline.append(Array(audio[i..<min(i + block, audio.count)]))
            i += block
        }
        let text = try await pipeline.finish()

        // Every chunk's sample count shows up, in order, and they add up to the whole recording.
        let counts = text.split(separator: "]").map { Int($0.drop(while: { $0 == "[" || $0 == " " }))! }
        #expect(counts.count >= 5)
        #expect(counts.reduce(0, +) == audio.count)
        #expect(attemptsPerChunk.values.allSatisfy { $0 == 2 })
    }

    @Test func permanentFailureKeepsPartialText() async throws {
        let audio = speech(seconds: 40)
        let reference = Chunker()
        let firstChunkSize = reference.append(audio).first!.samples.count
        MockOpenRouter.handler = { _, body in
            let json = try! JSONSerialization.jsonObject(with: body) as! [String: Any]
            let data = (json["input_audio"] as! [String: String])["data"]!
            let samples = WAV.decodeSamples(Data(base64Encoded: data)!)!
            if samples.count == firstChunkSize { return (200, Data(#"{"text":"第一段"}"#.utf8)) }
            return (500, Data(#"{"error":{"message":"down"}}"#.utf8))
        }
        let client = APIClient(endpoint: .openRouter(apiKey: "test"), session: MockOpenRouter.session())
        let pipeline = TranscriptionPipeline(client: client, options: .init(model: "m"),
                                             policy: RetryPolicy(maxAttempts: 2, baseDelay: 0.01))
        pipeline.append(audio)
        do {
            _ = try await pipeline.finish()
            Issue.record("expected failure")
        } catch let failure as PipelineFailure {
            #expect(failure.partialText == "第一段")
            #expect(failure.failedChunks == [1])
            #expect(pipeline.completedTranscripts() == [0: "第一段"])
        }
    }

    @Test func authErrorIsNotRetried() async throws {
        var calls = 0
        MockOpenRouter.handler = { _, _ in
            MockOpenRouter.lock.lock(); calls += 1; MockOpenRouter.lock.unlock()
            return (401, Data(#"{"error":{"message":"No auth credentials found"}}"#.utf8))
        }
        let client = APIClient(endpoint: .openRouter(apiKey: "bad"), session: MockOpenRouter.session())
        let pipeline = TranscriptionPipeline(client: client, options: .init(model: "m"),
                                             policy: RetryPolicy(maxAttempts: 4, baseDelay: 0.01))
        pipeline.append(speech(seconds: 5))
        await #expect(throws: PipelineFailure.self) { try await pipeline.finish() }
        // Permanent errors are neither retried nor re-sent in the final round.
        #expect(calls == 1)
    }

    @Test func silenceIsSkippedWithoutRequests() async throws {
        var calls = 0
        MockOpenRouter.handler = { _, _ in calls += 1; return (200, Data(#"{"text":"Thanks for watching!"}"#.utf8)) }
        let client = APIClient(endpoint: .openRouter(apiKey: "k"), session: MockOpenRouter.session())
        let pipeline = TranscriptionPipeline(client: client, options: .init(model: "m"))
        pipeline.append([Int16](repeating: 3, count: AudioFormat.sampleCount(forSeconds: 4)))
        #expect(try await pipeline.finish() == "")
        #expect(calls == 0)
    }

    @Test func polishStreamsAndSanitizes() async throws {
        MockOpenRouter.handler = { request, body in
            #expect(request.url!.path.hasSuffix("/chat/completions"))
            let json = try! JSONSerialization.jsonObject(with: body) as! [String: Any]
            #expect(json["stream"] as? Bool == true)
            let sse = """
            : OPENROUTER PROCESSING

            data: {"choices":[{"delta":{"content":"我想做一个"}}]}

            data: {"choices":[{"delta":{"content":"语音输入软件。"},"finish_reason":"stop"}]}

            data: [DONE]

            """
            return (200, Data(sse.utf8))
        }
        let client = APIClient(endpoint: .openRouter(apiKey: "k"), session: MockOpenRouter.session())
        let result = try await client.polish(transcript: "嗯我想做一个呃语音输入软件", options: PolishOptions(model: "m"))
        #expect(result.text == "我想做一个语音输入软件。")
        #expect(result.truncated == false)
    }
}

// Shares MockOpenRouter.handler with the tests above, so it must run in the same serialized suite.
extension PipelineTests {
    private func endpoint(_ id: ProviderID, key: String = "k") -> ProviderEndpoint {
        ProviderEndpoint(id: id, baseURL: URL(string: id == .custom ? "http://localhost:9000/v1" : id.defaultBaseURL)!, apiKey: key)
    }

    @Test func multipartTranscriptionForOpenAICompatible() async throws {
        MockOpenRouter.handler = { request, body in
            #expect(request.url!.absoluteString == "https://api.groq.com/openai/v1/audio/transcriptions")
            #expect(request.value(forHTTPHeaderField: "Content-Type")!.hasPrefix("multipart/form-data; boundary="))
            #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer gsk_test")
            let text = String(decoding: body, as: UTF8.self)
            #expect(text.contains(#"name="file"; filename="audio.wav""#))
            #expect(text.contains("name=\"model\"\r\n\r\nwhisper-large-v3-turbo\r\n"))
            #expect(text.contains("name=\"language\"\r\n\r\nzh\r\n"))
            #expect(!text.contains("input_audio"))
            return (200, Data(#"{"text":"你好"}"#.utf8))
        }
        let client = APIClient(endpoint: endpoint(.groq, key: "gsk_test"), session: MockOpenRouter.session())
        let text = try await client.transcribe(wav: WAV.encode(samples: [1, 2, 3]),
                                               options: .init(model: "whisper-large-v3-turbo", language: "zh"), timeout: 5)
        #expect(text == "你好")
    }

    @Test func customEndpointWorksWithoutKey() async throws {
        MockOpenRouter.handler = { request, _ in
            #expect(request.value(forHTTPHeaderField: "Authorization") == nil)
            return (200, Data(#"{"text":"local"}"#.utf8))
        }
        let client = APIClient(endpoint: endpoint(.custom, key: ""), session: MockOpenRouter.session())
        #expect(try await client.transcribe(wav: WAV.encode(samples: [1]), options: .init(model: "w"), timeout: 5) == "local")
    }

    @Test func missingKeyIsReported() async {
        let client = APIClient(endpoint: endpoint(.openai, key: " "), session: MockOpenRouter.session())
        await #expect(throws: APIError.missingAPIKey("OpenAI")) {
            try await client.transcribe(wav: Data(), options: .init(model: "m"), timeout: 5)
        }
    }

    @Test func polishBodyAdaptsToProvider() {
        let options = PolishOptions(model: "m")
        let openRouter = APIClient(endpoint: endpoint(.openrouter)).polishBody(transcript: "x", options: options, includeOptional: true)
        #expect(openRouter["provider"] != nil && openRouter["temperature"] != nil && openRouter["max_tokens"] != nil)
        let openAI = APIClient(endpoint: endpoint(.openai)).polishBody(transcript: "x", options: options, includeOptional: true)
        #expect(openAI["temperature"] == nil && openAI["provider"] == nil && openAI["reasoning"] == nil)
        let deepSeek = APIClient(endpoint: endpoint(.deepseek)).polishBody(transcript: "x", options: options, includeOptional: true)
        #expect(deepSeek["temperature"] as? Double == 0.2 && deepSeek["provider"] == nil)
        let bare = APIClient(endpoint: endpoint(.openrouter)).polishBody(transcript: "x", options: options, includeOptional: false)
        #expect(Set(bare.keys) == ["model", "stream", "messages"])
    }

    @Test func polishRetriesWithoutRejectedParameter() async throws {
        var bodies: [[String: Any]] = []
        MockOpenRouter.handler = { _, body in
            let json = try! JSONSerialization.jsonObject(with: body) as! [String: Any]
            bodies.append(json)
            if json["temperature"] != nil {
                return (400, Data(#"{"error":{"message":"Unsupported value: 'temperature' does not support 0.2"}}"#.utf8))
            }
            return (200, Data("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n".utf8))
        }
        let client = APIClient(endpoint: endpoint(.deepseek), session: MockOpenRouter.session())
        let result = try await client.polish(transcript: "x", options: PolishOptions(model: "m"))
        #expect(result.text == "ok")
        #expect(bodies.count == 2)
    }

    @Test func localization() {
        UserDefaults.standard.set("en", forKey: AppLanguage.defaultsKey)
        #expect(L("中文", "English") == "English")
        UserDefaults.standard.set("zh", forKey: AppLanguage.defaultsKey)
        #expect(L("中文", "English") == "中文")
        UserDefaults.standard.removeObject(forKey: AppLanguage.defaultsKey)
    }
}
