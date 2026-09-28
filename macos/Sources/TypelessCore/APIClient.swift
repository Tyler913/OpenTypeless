import Foundation

/// Talks to OpenRouter or any OpenAI-compatible server for speech-to-text, chat and model lists.
public struct APIClient: Sendable {
    public var endpoint: ProviderEndpoint
    let session: URLSession

    public init(endpoint: ProviderEndpoint, session: URLSession? = nil) {
        self.endpoint = endpoint
        self.session = session ?? APIClient.sharedSession
    }

    /// One session for the whole app so connections are reused across chunks.
    static let sharedSession: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        // Per-request idle timeouts are set on each URLRequest; hard caps are enforced by `withTimeout`.
        config.timeoutIntervalForResource = 600
        config.waitsForConnectivity = false
        config.httpMaximumConnectionsPerHost = 6
        return URLSession(configuration: config)
    }()

    var isOpenRouter: Bool { endpoint.id == .openrouter }

    func url(_ path: String) -> URL {
        endpoint.baseURL.appendingPathComponent(path)
    }

    func request(path: String, idleTimeout: Double, contentType: String = "application/json") throws -> URLRequest {
        let key = endpoint.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if key.isEmpty, endpoint.id.requiresKey { throw APIError.missingAPIKey(endpoint.id.displayName) }
        var request = URLRequest(url: url(path))
        request.httpMethod = "POST"
        request.timeoutInterval = idleTimeout
        if !key.isEmpty { request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        request.setValue(contentType, forHTTPHeaderField: "Content-Type")
        if isOpenRouter {
            // OpenRouter app attribution headers (optional).
            request.setValue("https://github.com/Tyler913/OpenTypeless", forHTTPHeaderField: "HTTP-Referer")
            request.setValue("OpenTypeless", forHTTPHeaderField: "X-Title")
        }
        return request
    }

    static func errorMessage(from data: Data) -> String {
        if let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            if let error = object["error"] as? [String: Any] {
                var message = (error["message"] as? String) ?? "unknown error"
                // OpenRouter nests the upstream provider's message in metadata.raw.
                if let metadata = error["metadata"] as? [String: Any], let raw = metadata["raw"] as? String {
                    message += " — \(raw.prefix(300))"
                }
                return message
            }
            if let error = object["error"] as? String { return error }
            if let message = object["message"] as? String { return message }
            if let detail = object["detail"] as? String { return detail }
        }
        return String(data: data.prefix(300), encoding: .utf8) ?? "unknown error"
    }
}

// MARK: - Speech to text

extension APIClient {
    public struct TranscriptionOptions: Sendable {
        public var model: String
        /// ISO-639-1 code, or nil for auto-detect.
        public var language: String?

        public init(model: String, language: String? = nil) {
            self.model = model
            self.language = language
        }
    }

    /// A transcript and what producing it used, when the server said.
    public struct TranscriptionResult: Sendable {
        public let text: String
        public let usage: RequestUsage?
    }

    /// One STT call for a single chunk (no retries — see `transcribeWithRetry`).
    public func transcribe(wav: Data, options: TranscriptionOptions, timeout: Double) async throws -> String {
        try await transcribeDetailed(wav: wav, options: options, timeout: timeout).text
    }

    /// `transcribe`, plus the `usage` the server reported (OpenRouter includes the cost).
    public func transcribeDetailed(wav: Data, options: TranscriptionOptions, timeout: Double) async throws -> TranscriptionResult {
        var request: URLRequest
        switch endpoint.id.sttFormat {
        case .openRouterJSON:
            var body: [String: Any] = [
                "model": options.model,
                "input_audio": ["data": wav.base64EncodedString(), "format": "wav"],
            ]
            if let language = options.language, !language.isEmpty { body["language"] = language }
            request = try self.request(path: "audio/transcriptions", idleTimeout: timeout)
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        case .multipart:
            let form = MultipartForm.transcription(wav: wav, options: options)
            request = try self.request(path: "audio/transcriptions", idleTimeout: timeout, contentType: form.contentType)
            request.httpBody = form.body
        }

        let session = self.session
        let (data, response) = try await withTimeout(timeout) { [request] in
            try await session.data(for: request)
        }
        guard let http = response as? HTTPURLResponse else { throw APIError.badResponse("no HTTP response") }
        guard (200..<300).contains(http.statusCode) else {
            throw APIError.http(status: http.statusCode, message: Self.errorMessage(from: data))
        }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            // `response_format=text` servers reply with the bare transcript.
            if let text = String(data: data, encoding: .utf8), !text.hasPrefix("<") { return TranscriptionResult(text: text, usage: nil) }
            throw APIError.badResponse(String(data: data.prefix(200), encoding: .utf8) ?? "")
        }
        if object["error"] != nil {
            // Some upstream failures arrive as 200 + error body.
            throw APIError.http(status: 502, message: Self.errorMessage(from: data))
        }
        guard let text = object["text"] as? String else {
            throw APIError.badResponse("missing \"text\" field")
        }
        return TranscriptionResult(text: text, usage: RequestUsage.parse(object["usage"]))
    }

    /// Per-attempt timeout scales with chunk length but stays under the ~60 s upstream limit for typical
    /// chunks; retries cover transient provider/network failures.
    public func transcribeWithRetry(
        samples: [Int16],
        options: TranscriptionOptions,
        policy: RetryPolicy = RetryPolicy(),
        onRetry: ((Int, APIError) -> Void)? = nil
    ) async throws -> String {
        try await transcribeDetailedWithRetry(samples: samples, options: options, policy: policy, onRetry: onRetry).text
    }

    /// `transcribeWithRetry`, plus the usage of the attempt that succeeded (audio length filled in).
    public func transcribeDetailedWithRetry(
        samples: [Int16],
        options: TranscriptionOptions,
        policy: RetryPolicy = RetryPolicy(),
        onRetry: ((Int, APIError) -> Void)? = nil
    ) async throws -> TranscriptionResult {
        let wav = WAV.encode(samples: samples)
        let seconds = AudioFormat.seconds(forSampleCount: samples.count)
        let timeout = max(30, min(90, seconds * 2 + 15))
        let result = try await policy.run(onRetry: onRetry) { _ in
            try await transcribeDetailed(wav: wav, options: options, timeout: timeout)
        }
        return TranscriptionResult(text: result.text,
                                   usage: (result.usage ?? RequestUsage(estimated: true)).withAudioSeconds(seconds))
    }
}

public struct MultipartForm {
    public let boundary: String
    public private(set) var body = Data()

    public init(boundary: String = "OpenTypeless-\(UUID().uuidString)") {
        self.boundary = boundary
    }

    public var contentType: String { "multipart/form-data; boundary=\(boundary)" }

    public mutating func addField(_ name: String, _ value: String) {
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
    }

    public mutating func addFile(_ name: String, filename: String, mimeType: String, data: Data) {
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"; filename=\"\(filename)\"\r\nContent-Type: \(mimeType)\r\n\r\n".utf8))
        body.append(data)
        body.append(Data("\r\n".utf8))
    }

    public mutating func finish() {
        body.append(Data("--\(boundary)--\r\n".utf8))
    }

    /// Only `file`, `model` and (optionally) `language`: some OpenAI-compatible servers reject any
    /// parameter they don't know, so nothing else is sent.
    static func transcription(wav: Data, options: APIClient.TranscriptionOptions) -> MultipartForm {
        var form = MultipartForm()
        form.addFile("file", filename: "audio.wav", mimeType: "audio/wav", data: wav)
        form.addField("model", options.model)
        if let language = options.language, !language.isEmpty { form.addField("language", language) }
        form.finish()
        return form
    }
}

// MARK: - Models

extension APIClient {
    public struct ModelInfo: Sendable, Hashable, Identifiable {
        public let id: String
        public let name: String
        public let supportedParameters: [String]
        public let reasoningMandatory: Bool
        public let reasoningEfforts: [String]

        public init(id: String, name: String, supportedParameters: [String] = [],
                    reasoningMandatory: Bool = false, reasoningEfforts: [String] = []) {
            self.id = id
            self.name = name
            self.supportedParameters = supportedParameters
            self.reasoningMandatory = reasoningMandatory
            self.reasoningEfforts = reasoningEfforts
        }

        /// Best-effort guess for OpenAI-compatible servers that don't label model types.
        public var looksLikeSpeechModel: Bool {
            let id = id.lowercased()
            return ["whisper", "transcribe", "asr", "sensevoice", "stt", "speech", "paraformer", "telespeech"]
                .contains { id.contains($0) }
        }
    }

    /// GET /models. On OpenRouter, `outputModality: "transcription"` lists STT models.
    public func listModels(outputModality: String? = nil) async throws -> [ModelInfo] {
        var components = URLComponents(url: url("models"), resolvingAgainstBaseURL: false)!
        if let outputModality, isOpenRouter {
            components.queryItems = [URLQueryItem(name: "output_modalities", value: outputModality)]
        }
        var request = URLRequest(url: components.url!)
        request.timeoutInterval = 20
        let key = endpoint.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !key.isEmpty { request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIError.badResponse("no HTTP response") }
        guard (200..<300).contains(http.statusCode) else {
            throw APIError.http(status: http.statusCode, message: Self.errorMessage(from: data))
        }
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { throw APIError.badResponse("models list") }
        return list.compactMap { m in
            guard let id = m["id"] as? String else { return nil }
            let reasoning = m["reasoning"] as? [String: Any]
            return ModelInfo(
                id: id,
                name: (m["name"] as? String) ?? id,
                supportedParameters: (m["supported_parameters"] as? [String]) ?? [],
                reasoningMandatory: (reasoning?["mandatory"] as? Bool) ?? false,
                reasoningEfforts: (reasoning?["supported_efforts"] as? [String]) ?? []
            )
        }
    }

    /// Verifies the key/URL work: OpenRouter's /models is public, so check its key endpoint instead.
    public func verifyCredentials() async throws -> String {
        if isOpenRouter {
            var request = URLRequest(url: url("key"))
            request.timeoutInterval = 15
            request.setValue("Bearer \(endpoint.apiKey.trimmingCharacters(in: .whitespacesAndNewlines))",
                             forHTTPHeaderField: "Authorization")
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
                throw APIError.http(status: (response as? HTTPURLResponse)?.statusCode ?? 0,
                                    message: Self.errorMessage(from: data))
            }
            return L("Key 有效", "Key is valid")
        }
        let models = try await listModels()
        return L("连接正常，\(models.count) 个模型可用", "Connected — \(models.count) models available")
    }

    /// `verifyCredentials` a few times in a row, timing each round trip. The first one also pays for setting up
    /// the connection, so the median is what a request costs once the app is running.
    public func checkConnection(attempts: Int = 3) async throws -> ConnectionCheck {
        var message = ""
        var times: [Double] = []
        for _ in 0..<max(1, attempts) {
            let start = ProcessInfo.processInfo.systemUptime
            message = try await verifyCredentials()
            times.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
        }
        return ConnectionCheck(message: message, milliseconds: ConnectionCheck.median(times))
    }
}

/// A working connection and how long a round trip to the provider takes.
public struct ConnectionCheck: Sendable, Equatable {
    public enum Speed: Sendable { case fast, fine, slow }

    public let message: String
    public let milliseconds: Double

    public init(message: String, milliseconds: Double) {
        self.message = message
        self.milliseconds = milliseconds
    }

    /// Under 300 ms is fast, under a second fine, anything longer slow.
    public var speed: Speed { milliseconds < 300 ? .fast : milliseconds < 1000 ? .fine : .slow }

    /// "Key is valid · 182 ms"
    public var summary: String { message + " · \(Int(milliseconds.rounded())) ms" }

    public static func median(_ values: [Double]) -> Double {
        let sorted = values.sorted()
        guard !sorted.isEmpty else { return 0 }
        let mid = sorted.count / 2
        return sorted.count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2
    }
}
