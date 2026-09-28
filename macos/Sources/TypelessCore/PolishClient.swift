import Foundation

public struct PolishOptions: Sendable {
    public var model: String
    public var vocabulary: [String]
    /// Earlier recognition errors the user fixed by hand, shown to the model as hints.
    public var misheard: [Correction]
    public var extraInstructions: String
    /// OpenRouter model metadata from `/models`, used to switch reasoning off (or to its minimum).
    public var modelInfo: APIClient.ModelInfo?
    /// Replaces the built-in system prompt (used by the prompt evaluation tool).
    public var systemPromptOverride: String?

    public init(model: String, vocabulary: [String] = [], misheard: [Correction] = [], extraInstructions: String = "",
                modelInfo: APIClient.ModelInfo? = nil, systemPromptOverride: String? = nil) {
        self.model = model
        self.vocabulary = vocabulary
        self.misheard = misheard
        self.extraInstructions = extraInstructions
        self.modelInfo = modelInfo
        self.systemPromptOverride = systemPromptOverride
    }
}

public enum ReasoningConfig {
    static let effortOrder = ["none", "minimal", "low", "medium", "high", "xhigh"]

    /// Reasoning adds seconds of latency and nothing to text clean-up, so turn it off or to the minimum.
    public static func body(for info: APIClient.ModelInfo?) -> [String: Any]? {
        guard let info else { return ["exclude": true] }
        guard info.supportedParameters.contains("reasoning") else { return nil }
        if info.reasoningMandatory {
            let lowest = info.reasoningEfforts.min {
                (effortOrder.firstIndex(of: $0) ?? 99) < (effortOrder.firstIndex(of: $1) ?? 99)
            }
            if let lowest { return ["effort": lowest, "exclude": true] }
            return ["exclude": true]
        }
        if info.reasoningEfforts.contains("none") { return ["effort": "none", "exclude": true] }
        return ["enabled": false, "exclude": true]
    }
}

/// Parses an OpenAI-style SSE stream for chat completions.
public struct SSEParser {
    public enum Event: Equatable {
        case content(String)
        case finished(reason: String?)
        case done
        case error(String)
    }

    public init() {}

    public func parse(line rawLine: String) -> [Event] {
        let line = rawLine.trimmingCharacters(in: .whitespaces)
        // Empty lines separate events; ":" lines are keep-alive comments (": OPENROUTER PROCESSING").
        guard line.hasPrefix("data:") else { return [] }
        let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
        if payload == "[DONE]" { return [.done] }
        guard let data = payload.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [] }
        if object["error"] != nil {
            return [.error(APIClient.errorMessage(from: data))]
        }
        var events: [Event] = []
        if let choices = object["choices"] as? [[String: Any]], let choice = choices.first {
            if let delta = choice["delta"] as? [String: Any], let content = delta["content"] as? String,
               !content.isEmpty {
                events.append(.content(content))
            }
            if let reason = choice["finish_reason"] as? String {
                if reason == "error" { events.append(.error("provider returned finish_reason=error")) }
                else { events.append(.finished(reason: reason)) }
            }
        }
        return events
    }
}

extension APIClient {
    public struct PolishResult: Sendable {
        public let text: String
        public let truncated: Bool
    }

    /// Clean-up output is short, so the wait is mostly time to first token: prefer the lowest-latency
    /// provider, but push ones that stream slowly (under 50 tokens/s at the median) to the back.
    static let polishRouting: [String: Any] = [
        "sort": "latency",
        "preferred_min_throughput": ["p50": 50],
        "allow_fallbacks": true,
    ]

    /// Builds the chat request body. Optional tuning parameters are only sent where they're known to
    /// be accepted: OpenRouter normalises them for every model, while e.g. OpenAI's reasoning models
    /// reject `temperature`.
    func polishBody(transcript: String, options: PolishOptions, includeOptional: Bool) -> [String: Any] {
        var body: [String: Any] = [
            "model": options.model,
            "stream": true,
            "messages": [
                ["role": "system", "content": options.systemPromptOverride ?? Prompts.polishSystemPrompt(
                    vocabulary: options.vocabulary, misheard: options.misheard, extraInstructions: options.extraInstructions)],
                ["role": "user", "content": Prompts.polishUserMessage(transcript: transcript)],
            ],
        ]
        guard includeOptional else { return body }
        if isOpenRouter {
            body["max_tokens"] = max(1024, transcript.count * 3)
            body["provider"] = Self.polishRouting
            if let reasoning = ReasoningConfig.body(for: options.modelInfo) { body["reasoning"] = reasoning }
            if options.modelInfo?.supportedParameters.contains("temperature") ?? true { body["temperature"] = 0.2 }
        } else if endpoint.id != .openai {
            body["temperature"] = 0.2
        }
        return body
    }

    /// Streams a clean-up completion. Liveness is judged by an *idle* timeout (no bytes for
    /// `idleTimeout` seconds) rather than a short total timeout, so long dictations that take a while
    /// to stream out are never killed mid-way. `totalTimeout` is only a runaway guard.
    public func polish(
        transcript: String,
        options: PolishOptions,
        idleTimeout: Double = 25,
        totalTimeout: Double = 240,
        onPartial: (@Sendable (String) -> Void)? = nil
    ) async throws -> PolishResult {
        do {
            return try await streamPolish(transcript: transcript, options: options, includeOptional: true,
                                          idleTimeout: idleTimeout, totalTimeout: totalTimeout, onPartial: onPartial)
        } catch let APIError.http(status, message) where status == 400 || status == 422 {
            // A server rejecting an optional parameter: retry with the bare minimum request.
            let lower = message.lowercased()
            guard ["temperature", "max_tokens", "reasoning", "provider", "unsupported", "unrecognized", "unknown"]
                .contains(where: lower.contains) else { throw APIError.http(status: status, message: message) }
            return try await streamPolish(transcript: transcript, options: options, includeOptional: false,
                                          idleTimeout: idleTimeout, totalTimeout: totalTimeout, onPartial: onPartial)
        }
    }

    private func streamPolish(
        transcript: String, options: PolishOptions, includeOptional: Bool,
        idleTimeout: Double, totalTimeout: Double, onPartial: (@Sendable (String) -> Void)?
    ) async throws -> PolishResult {
        var request = try request(path: "chat/completions", idleTimeout: idleTimeout)
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        request.httpBody = try JSONSerialization.data(
            withJSONObject: polishBody(transcript: transcript, options: options, includeOptional: includeOptional))
        let session = self.session

        return try await withTimeout(totalTimeout) { [request] in
            let (bytes, response) = try await session.bytes(for: request)
            guard let http = response as? HTTPURLResponse else { throw APIError.badResponse("no HTTP response") }
            guard (200..<300).contains(http.statusCode) else {
                var data = Data()
                for try await byte in bytes { data.append(byte); if data.count > 4096 { break } }
                throw APIError.http(status: http.statusCode, message: APIClient.errorMessage(from: data))
            }
            let parser = SSEParser()
            var text = ""
            var finishReason: String?
            loop: for try await line in bytes.lines {
                for event in parser.parse(line: line) {
                    switch event {
                    case let .content(piece):
                        text += piece
                        onPartial?(text)
                    case let .finished(reason):
                        finishReason = reason
                    case .done:
                        break loop
                    case let .error(message):
                        throw APIError.http(status: 502, message: message)
                    }
                }
            }
            let cleaned = Prompts.sanitizePolishOutput(text)
            guard !cleaned.isEmpty else { throw APIError.badResponse(L("模型返回了空内容", "the model returned nothing")) }
            return PolishResult(text: cleaned, truncated: finishReason == "length")
        }
    }
}
