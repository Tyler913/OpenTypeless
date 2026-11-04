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

    /// A client for a file being transcribed, on a session of its own with fewer connections per host (see
    /// `FileTranscription.maxConnectionsPerHost`): a dictation's requests never wait behind a file's.
    public static func forFiles(endpoint: ProviderEndpoint) -> APIClient {
        APIClient(endpoint: endpoint, session: fileSession)
    }

    static let fileSession: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForResource = 600
        config.waitsForConnectivity = false
        config.httpMaximumConnectionsPerHost = FileTranscription.maxConnectionsPerHost
        return URLSession(configuration: config)
    }()

    var isOpenRouter: Bool { endpoint.id == .openrouter }

    func url(_ path: String) -> URL {
        endpoint.baseURL.appendingPathComponent(path)
    }

    /// `query`: parameters for the URL, in order (a name may repeat).
    func request(path: String, idleTimeout: Double, contentType: String = "application/json",
                 query: [URLQueryItem] = []) throws -> URLRequest {
        let key = endpoint.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if key.isEmpty, endpoint.id.requiresKey { throw APIError.missingAPIKey(endpoint.id.displayName) }
        var target = url(path)
        if !query.isEmpty, var components = URLComponents(url: target, resolvingAgainstBaseURL: false) {
            components.queryItems = query
            // URLComponents leaves "+" alone, which a server reads as a space.
            components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
            target = components.url ?? target
        }
        var request = URLRequest(url: target)
        request.httpMethod = "POST"
        request.timeoutInterval = idleTimeout
        if !key.isEmpty {
            let auth = endpoint.id.authorization(key)
            request.setValue(auth.value, forHTTPHeaderField: auth.field)
        }
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
            // Deepgram.
            if let message = object["err_msg"] as? String { return message }
            if let detail = object["detail"] as? String { return detail }
            // ElevenLabs: {"detail": {"status": "invalid_api_key", "message": "…"}}.
            if let detail = object["detail"] as? [String: Any], let message = detail["message"] as? String { return message }
            // A validation error (ElevenLabs, Mistral): {"detail": [{"loc": ["body", "field"], "msg": "…"}]}, the field named
            // so a rejected optional parameter can be recognised.
            if let details = object["detail"] as? [[String: Any]], !details.isEmpty {
                return details.compactMap { item -> String? in
                    guard let message = item["msg"] as? String else { return nil }
                    let location = (item["loc"] as? [Any])?.map { "\($0)" }.joined(separator: ".") ?? ""
                    return location.isEmpty ? message : "\(location): \(message)"
                }.joined(separator: "; ")
            }
        }
        return String(data: data.prefix(300), encoding: .utf8) ?? "unknown error"
    }

    /// The status a failed response is classified by: its own, except where the body says the account is out of credit
    /// under another status (ElevenLabs answers 401 `quota_exceeded`), which is a 402 so it reads as billing.
    static func httpStatus(_ status: Int, body data: Data) -> Int {
        guard status == 401 || status == 403 || status == 429,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let detail = object["detail"] as? [String: Any],
              let code = detail["status"] as? String else { return status }
        return ["quota_exceeded", "payment_required", "insufficient_credits"].contains(code) ? 402 : status
    }

    /// The HTTP status an error body carries in `error.code` (OpenRouter puts 401, 402, 429… there, also inside a 200
    /// response or a stream), so the error is classified like that status and e.g. a 402 isn't retried. Nil for string
    /// codes and anything else.
    static func errorStatus(from data: Data) -> Int? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let error = object["error"] as? [String: Any],
              let code = error["code"] as? Int, (400...599).contains(code) else { return nil }
        return code
    }
}

// MARK: - Speech to text

extension APIClient {
    public struct TranscriptionOptions: Sendable {
        public var model: String
        /// ISO-639-1 code, or nil for auto-detect.
        public var language: String?
        /// The user's vocabulary, passed to models that take spelling hints (see VocabularyHints).
        public var vocabulary: [String]

        public init(model: String, language: String? = nil, vocabulary: [String] = []) {
            self.model = model
            self.language = language
            self.vocabulary = vocabulary
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
        do {
            return try await transcribeOnce(wav: wav, options: options, timeout: timeout)
        } catch let APIError.http(status, _) where (status == 400 || status == 422) && hasVocabularyHints(options) {
            // A provider that stopped taking the hints mustn't stop dictation: the same request without them. (A field
            // that fails validation is a 422 at ElevenLabs.)
            var plain = options
            plain.vocabulary = []
            return try await transcribeOnce(wav: wav, options: plain, timeout: timeout)
        }
    }

    func hasVocabularyHints(_ options: TranscriptionOptions) -> Bool {
        switch endpoint.id.sttFormat {
        case .openRouterJSON: return VocabularyHints.openRouterProvider(model: options.model, vocabulary: options.vocabulary) != nil
        case .multipart, .deepgram, .elevenLabs:
            return VocabularyHints.direct(provider: endpoint.id, model: options.model, vocabulary: options.vocabulary) != nil
        }
    }

    /// The transcription request for one chunk, with the vocabulary hints this model takes.
    func transcriptionRequest(wav: Data, options: TranscriptionOptions, timeout: Double) throws -> URLRequest {
        var request: URLRequest
        let format = endpoint.id.sttFormat
        let language = options.language.map { $0.trimmingCharacters(in: .whitespaces) }.flatMap { $0.isEmpty ? nil : $0 }
        switch format {
        case .openRouterJSON:
            var body: [String: Any] = [
                "model": options.model,
                "input_audio": ["data": wav.base64EncodedString(), "format": "wav"],
            ]
            if let language { body["language"] = language }
            if let provider = VocabularyHints.openRouterProvider(model: options.model, vocabulary: options.vocabulary) {
                body["provider"] = provider
            }
            request = try self.request(path: format.path, idleTimeout: timeout)
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        case .multipart:
            let hints = VocabularyHints.direct(provider: endpoint.id, model: options.model, vocabulary: options.vocabulary)
            let form = MultipartForm.transcription(wav: wav, options: options, hints: hints)
            request = try self.request(path: format.path, idleTimeout: timeout, contentType: form.contentType)
            request.httpBody = form.body
        case .deepgram:
            // The audio is the body; everything else goes in the query. Punctuation and formatting as typed text has
            // them; the language when one is set, else detected (Deepgram assumes English).
            var query = [URLQueryItem(name: "model", value: options.model),
                         URLQueryItem(name: "smart_format", value: "true"),
                         URLQueryItem(name: "punctuate", value: "true")]
            if let language {
                query.append(URLQueryItem(name: "language", value: language))
            } else {
                query.append(URLQueryItem(name: "detect_language", value: "true"))
            }
            if case let .keyterm(terms) = VocabularyHints.direct(provider: endpoint.id, model: options.model,
                                                                 vocabulary: options.vocabulary) {
                query += terms.map { URLQueryItem(name: "keyterm", value: $0) }
            }
            request = try self.request(path: format.path, idleTimeout: timeout, contentType: "audio/wav", query: query)
            request.httpBody = wav
        case .elevenLabs:
            let hints = VocabularyHints.direct(provider: endpoint.id, model: options.model, vocabulary: options.vocabulary)
            let form = MultipartForm.scribe(wav: wav, options: options, hints: hints)
            request = try self.request(path: format.path, idleTimeout: timeout, contentType: form.contentType)
            request.httpBody = form.body
        }
        return request
    }

    private func transcribeOnce(wav: Data, options: TranscriptionOptions, timeout: Double) async throws -> TranscriptionResult {
        let request = try transcriptionRequest(wav: wav, options: options, timeout: timeout)
        let session = self.session
        let (data, response) = try await withTimeout(timeout) { [request] in
            try await session.data(for: request)
        }
        guard let http = response as? HTTPURLResponse else { throw APIError.badResponse("no HTTP response") }
        guard (200..<300).contains(http.statusCode) else {
            throw APIError.http(status: Self.httpStatus(http.statusCode, body: data), message: Self.errorMessage(from: data))
        }
        return try Self.parseTranscription(data, format: endpoint.id.sttFormat)
    }

    /// A successful transcription response: `text` (and `usage`) for every format but Deepgram's, whose transcript is
    /// the first alternative of the first channel and whose length is `metadata.duration`.
    static func parseTranscription(_ data: Data, format: STTFormat) throws -> TranscriptionResult {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            // `response_format=text` servers reply with the bare transcript.
            if format == .multipart, let text = String(data: data, encoding: .utf8), !text.hasPrefix("<") {
                return TranscriptionResult(text: text, usage: nil)
            }
            throw APIError.badResponse(String(data: data.prefix(200), encoding: .utf8) ?? "")
        }
        if object["error"] != nil || object["err_code"] != nil {
            // Some upstream failures arrive as 200 + error body.
            throw APIError.http(status: errorStatus(from: data) ?? 502, message: errorMessage(from: data))
        }
        if format == .deepgram {
            guard let results = object["results"] as? [String: Any],
                  let channel = (results["channels"] as? [[String: Any]])?.first,
                  let alternative = (channel["alternatives"] as? [[String: Any]])?.first,
                  let text = alternative["transcript"] as? String else {
                throw APIError.badResponse("missing \"results.channels[0].alternatives[0].transcript\"")
            }
            let seconds = RequestUsage.number((object["metadata"] as? [String: Any])?["duration"])
            return TranscriptionResult(text: text, usage: seconds.map { RequestUsage(audioSeconds: $0) })
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

    /// Only `file`, `model`, and (optionally) `language` and the vocabulary hints, which go only where documented (see
    /// VocabularyHints): some OpenAI-compatible servers reject any parameter they don't know.
    static func transcription(wav: Data, options: APIClient.TranscriptionOptions, prompt: String? = nil) -> MultipartForm {
        transcription(wav: wav, options: options, hints: prompt.flatMap { $0.isEmpty ? nil : .prompt($0) })
    }

    static func transcription(wav: Data, options: APIClient.TranscriptionOptions, hints: VocabularyHints.Direct?) -> MultipartForm {
        var form = MultipartForm()
        form.addFile("file", filename: "audio.wav", mimeType: "audio/wav", data: wav)
        form.addField("model", options.model)
        if let language = options.language, !language.isEmpty { form.addField("language", language) }
        switch hints {
        case let .prompt(prompt)?: form.addField("prompt", prompt)
        // Mistral: the field once per term.
        case let .contextBias(terms)?: for term in terms { form.addField("context_bias", term) }
        default: break
        }
        form.finish()
        return form
    }

    /// ElevenLabs Scribe: `model_id` and `file`, the language when one is set (else it's detected), no sound-event
    /// tags such as "(laughter)" in the text, and the vocabulary as `keyterms`, a field per term.
    static func scribe(wav: Data, options: APIClient.TranscriptionOptions, hints: VocabularyHints.Direct?) -> MultipartForm {
        var form = MultipartForm()
        form.addField("model_id", options.model)
        form.addFile("file", filename: "audio.wav", mimeType: "audio/wav", data: wav)
        if let language = options.language, !language.isEmpty { form.addField("language_code", language) }
        form.addField("tag_audio_events", "false")
        if case let .keyterms(terms)? = hints {
            for term in terms { form.addField("keyterms", term) }
        }
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
            return ["whisper", "transcribe", "asr", "sensevoice", "stt", "speech", "paraformer", "telespeech", "voxtral"]
                .contains { id.contains($0) }
        }
    }

    /// GET /models. On OpenRouter, `outputModality: "transcription"` lists STT models.
    /// Deepgram and ElevenLabs list no speech-to-text models this way: nothing to choose from (the model is typed in).
    public func listModels(outputModality: String? = nil) async throws -> [ModelInfo] {
        guard endpoint.id.listsModels else { return [] }
        let data = try await get(modelListRequest(outputModality: outputModality))
        return try Self.parseModels(data)
    }

    /// The GET for the model list: OpenRouter's speech models by their output, every Claude model in one page.
    func modelListRequest(outputModality: String? = nil) -> URLRequest {
        var components = URLComponents(url: url("models"), resolvingAgainstBaseURL: false)!
        if let outputModality, isOpenRouter {
            components.queryItems = [URLQueryItem(name: "output_modalities", value: outputModality)]
        } else if endpoint.id == .anthropic {
            components.queryItems = [URLQueryItem(name: "limit", value: "1000")]
        }
        return getRequest(components.url!)
    }

    /// A GET with the key in the header this provider reads it from.
    func getRequest(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.timeoutInterval = 20
        let key = endpoint.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        for header in endpoint.id.listHeaders(key) { request.setValue(header.value, forHTTPHeaderField: header.field) }
        return request
    }

    /// The body of a successful GET; any other status is an `APIError.http`.
    func get(_ request: URLRequest) async throws -> Data {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIError.badResponse("no HTTP response") }
        guard (200..<300).contains(http.statusCode) else {
            throw APIError.http(status: Self.httpStatus(http.statusCode, body: data), message: Self.errorMessage(from: data))
        }
        return data
    }

    /// `{"data": [{"id": …}]}`; Gemini's IDs come as "models/gemini-…", which requests name without the prefix, and
    /// Anthropic's name is its `display_name`.
    static func parseModels(_ data: Data) throws -> [ModelInfo] {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { throw APIError.badResponse("models list") }
        return list.compactMap { m in
            guard var id = m["id"] as? String else { return nil }
            if id.hasPrefix("models/") { id.removeFirst("models/".count) }
            let reasoning = m["reasoning"] as? [String: Any]
            return ModelInfo(
                id: id,
                name: (m["name"] as? String) ?? (m["display_name"] as? String) ?? id,
                supportedParameters: (m["supported_parameters"] as? [String]) ?? [],
                reasoningMandatory: (reasoning?["mandatory"] as? Bool) ?? false,
                reasoningEfforts: (reasoning?["supported_efforts"] as? [String]) ?? []
            )
        }
    }

    /// One company serving a model on OpenRouter.
    public struct ModelHost: Sendable, Hashable, Identifiable {
        /// What a request names it by in `provider.order`, e.g. "groq" or "deepinfra/fp8".
        public let tag: String
        public let name: String
        /// Input price, USD per token (per second of audio for speech models).
        public let inputPrice: Double?
        /// Share of requests that succeeded over the last 30 minutes, 0–100.
        public let uptime: Double?

        public var id: String { tag }

        public init(tag: String, name: String, inputPrice: Double? = nil, uptime: Double? = nil) {
            self.tag = tag
            self.name = name
            self.inputPrice = inputPrice
            self.uptime = uptime
        }
    }

    /// The hosts serving an OpenRouter model, cheapest first, from GET /models/{id}/endpoints.
    public func listHosts(model: String) async throws -> [ModelHost] {
        // A variant suffix (":nitro", ":free") isn't part of the path.
        let id = model.trimmingCharacters(in: .whitespaces).split(separator: ":").first.map(String.init) ?? ""
        guard isOpenRouter, id.contains("/") else { return [] }
        var request = URLRequest(url: url("models/\(id)/endpoints"))
        request.timeoutInterval = 20
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw APIError.badResponse("no HTTP response") }
        // An ID OpenRouter doesn't know: nothing to choose from.
        if http.statusCode == 404 { return [] }
        guard (200..<300).contains(http.statusCode) else {
            throw APIError.http(status: http.statusCode, message: Self.errorMessage(from: data))
        }
        return Self.parseHosts(data)
    }

    static func parseHosts(_ data: Data) -> [ModelHost] {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let model = object["data"] as? [String: Any],
              let rows = model["endpoints"] as? [[String: Any]] else { return [] }
        var seen = Set<String>()
        let hosts = rows.compactMap { row -> ModelHost? in
            guard let tag = row["tag"] as? String, !tag.isEmpty, seen.insert(tag).inserted else { return nil }
            let price = ((row["pricing"] as? [String: Any])?["prompt"] as? String).flatMap(Double.init)
            return ModelHost(tag: tag, name: (row["provider_name"] as? String) ?? tag, inputPrice: price,
                             uptime: (row["uptime_last_30m"] as? NSNumber)?.doubleValue)
        }
        return hosts.sorted { ($0.inputPrice ?? .infinity, $0.name, $0.tag) < ($1.inputPrice ?? .infinity, $1.name, $1.tag) }
    }

    /// Verifies the key/URL work: OpenRouter's /models is public, so check its key endpoint instead; Deepgram and
    /// ElevenLabs have no model list to ask for, so their own account endpoints (see `ProviderID.keyCheckPath`).
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
        let key = endpoint.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if key.isEmpty, endpoint.id.requiresKey { throw APIError.missingAPIKey(endpoint.id.displayName) }
        guard endpoint.id.listsModels else {
            _ = try await get(getRequest(url(endpoint.id.keyCheckPath)))
            return L("Key 有效", "Key is valid")
        }
        do {
            let models = try Self.parseModels(try await get(modelListRequest()))
            return L("连接正常，\(models.count) 个模型可用", "Connected — \(models.count) models available")
        } catch APIError.http(status: 404, message: _) where endpoint.id.modelListMayBeMissing {
            // The server answered and took the key (a bad one is a 401); it just has no list.
            return L("连接正常", "Connected")
        }
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
