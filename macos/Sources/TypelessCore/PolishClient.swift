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
    /// The OpenRouter host (an endpoint tag such as "groq" or "deepinfra/fp8") the user wants this model served by;
    /// nil lets OpenRouter pick the fastest.
    public var host: String?
    /// The text around the speaker's cursor, sent after the transcript to spell names by (see CursorContext); nil for none.
    public var context: String?

    public init(model: String, vocabulary: [String] = [], misheard: [Correction] = [], extraInstructions: String = "",
                modelInfo: APIClient.ModelInfo? = nil, systemPromptOverride: String? = nil, host: String? = nil,
                context: String? = nil) {
        self.model = model
        self.vocabulary = vocabulary
        self.misheard = misheard
        self.extraInstructions = extraInstructions
        self.modelInfo = modelInfo
        self.systemPromptOverride = systemPromptOverride
        self.host = host
        self.context = context
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
        /// An error object in the stream, with the HTTP status its `code` gives, if any.
        case error(String, status: Int? = nil)
        /// The token counts (and on OpenRouter the cost), sent in the last chunk.
        case usage(RequestUsage)
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
            return [.error(APIClient.errorMessage(from: data), status: APIClient.errorStatus(from: data))]
        }
        var events: [Event] = []
        if let usage = RequestUsage.parse(object["usage"]) { events.append(.usage(usage)) }
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
        /// What the request used: as reported, or estimated from the text when the server said nothing.
        public var usage: RequestUsage? = nil
    }

    /// Clean-up output is short, so the wait is mostly time to first token: prefer the lowest-latency
    /// provider, but push ones that stream slowly (under 50 tokens/s at the median) to the back.
    static let polishRouting: [String: Any] = [
        "sort": "latency",
        "preferred_min_throughput": ["p50": 50],
        "allow_fallbacks": true,
    ]

    /// `polishRouting`, with the user's chosen host tried first. Should that host be down or busy, OpenRouter still
    /// falls back to the others, fastest first, so a dictation never fails over the choice.
    static func polishRouting(host: String?) -> [String: Any] {
        var routing = polishRouting
        if let host = host?.trimmingCharacters(in: .whitespaces), !host.isEmpty { routing["order"] = [host] }
        return routing
    }

    /// The system prompt and the user message a clean-up of `transcript` sends.
    static func polishMessages(transcript: String, options: PolishOptions) -> [String] {
        [options.systemPromptOverride ?? Prompts.polishSystemPrompt(
            vocabulary: options.vocabulary, misheard: options.misheard, extraInstructions: options.extraInstructions),
         Prompts.polishUserMessage(transcript: transcript, context: options.context)]
    }

    /// Builds the chat request body. Optional tuning parameters are only sent where they're known to
    /// be accepted: OpenRouter normalises them for every model, while e.g. OpenAI's reasoning models
    /// reject `temperature`.
    func polishBody(transcript: String, options: PolishOptions, includeOptional: Bool) -> [String: Any] {
        let messages = Self.polishMessages(transcript: transcript, options: options)
        var body: [String: Any] = [
            "model": options.model,
            "stream": true,
            "messages": [
                ["role": "system", "content": messages[0]],
                ["role": "user", "content": messages[1]],
            ],
        ]
        guard includeOptional else { return body }
        if isOpenRouter {
            body["max_tokens"] = max(1024, transcript.count * 3)
            body["provider"] = Self.polishRouting(host: options.host)
            if let reasoning = ReasoningConfig.body(for: options.modelInfo) { body["reasoning"] = reasoning }
            if options.modelInfo?.supportedParameters.contains("temperature") ?? true { body["temperature"] = 0.2 }
        } else if endpoint.id != .openai {
            body["temperature"] = 0.2
        }
        if !isOpenRouter {
            // OpenAI-compatible servers only report token counts on a stream when asked (OpenRouter always does).
            body["stream_options"] = ["include_usage": true]
        }
        return body
    }

    /// How long a clean-up may take in all: long enough for a slow provider to stream the whole answer out, and short
    /// enough that a stuck one gives the dictation back (as spoken) within a minute or two rather than four.
    public static func polishTimeout(for transcript: String) -> Double {
        min(120, 20 + Double(transcript.count) * 0.05)
    }

    /// Clean-up models start answering within a second or two. OpenRouter sends keep-alive comments while a model is
    /// queued or slow to start, which keep the idle timeout from ever firing, so the first word has a deadline of its own.
    public static let polishFirstTokenTimeout: Double = 10

    /// Streams a clean-up completion. Liveness is judged by an *idle* timeout (no bytes for
    /// `idleTimeout` seconds) rather than a short total timeout, so long dictations that take a while
    /// to stream out are never killed mid-way. `totalTimeout` (by default `polishTimeout(for:)`) is only a runaway
    /// guard; the first content must arrive within `firstTokenTimeout`. All three end in `APIError.timeout`.
    public func polish(
        transcript: String,
        options: PolishOptions,
        idleTimeout: Double = 25,
        totalTimeout: Double? = nil,
        firstTokenTimeout: Double = APIClient.polishFirstTokenTimeout,
        onPartial: (@Sendable (String) -> Void)? = nil
    ) async throws -> PolishResult {
        let limits = StreamLimits(idle: idleTimeout, total: totalTimeout ?? Self.polishTimeout(for: transcript),
                                  firstToken: firstTokenTimeout)
        do {
            return try await streamPolish(transcript: transcript, options: options, includeOptional: true,
                                          limits: limits, onPartial: onPartial)
        } catch let APIError.http(status, message) where status == 400 || status == 422 {
            // A server rejecting an optional parameter: retry with the bare minimum request.
            let lower = message.lowercased()
            guard ["temperature", "max_tokens", "reasoning", "provider", "stream_options", "include_usage",
                   "unsupported", "unrecognized", "unknown"]
                .contains(where: lower.contains) else { throw APIError.http(status: status, message: message) }
            return try await streamPolish(transcript: transcript, options: options, includeOptional: false,
                                          limits: limits, onPartial: onPartial)
        }
    }

    private struct StreamLimits: Sendable {
        let idle: Double
        let total: Double
        let firstToken: Double
    }

    private func streamPolish(
        transcript: String, options: PolishOptions, includeOptional: Bool,
        limits: StreamLimits, onPartial: (@Sendable (String) -> Void)?
    ) async throws -> PolishResult {
        var request = try request(path: "chat/completions", idleTimeout: limits.idle)
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        let body = polishBody(transcript: transcript, options: options, includeOptional: includeOptional)
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let messages = (body["messages"] as? [[String: String]] ?? []).compactMap { $0["content"] }
        let session = self.session
        let answering = OnceFlag()

        return try await withTimeout(limits.total) { [request] in
            try await withThrowingTaskGroup(of: PolishResult?.self) { group in
                group.addTask {
                    do {
                        return try await Self.readPolishStream(session: session, request: request, messages: messages,
                                                               answering: answering, onPartial: onPartial)
                    } catch let error as URLError where error.code == .timedOut {
                        // No byte for the idle timeout.
                        throw APIError.timeout(seconds: limits.idle)
                    }
                }
                group.addTask {
                    try await Task.sleep(nanoseconds: UInt64(limits.firstToken * 1_000_000_000))
                    if answering.isSet { return nil }
                    throw APIError.timeout(seconds: limits.firstToken)
                }
                defer { group.cancelAll() }
                while let finished = try await group.next() {
                    if let result = finished { return result }
                }
                throw APIError.cancelled
            }
        }
    }

    private static func readPolishStream(
        session: URLSession, request: URLRequest, messages: [String], answering: OnceFlag,
        onPartial: (@Sendable (String) -> Void)?
    ) async throws -> PolishResult {
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
        var receivedDone = false
        var usage: RequestUsage?
        loop: for try await line in bytes.lines {
            for event in parser.parse(line: line) {
                switch event {
                case let .content(piece):
                    text += piece
                    answering.set()
                    onPartial?(text)
                case let .finished(reason):
                    finishReason = reason
                case .done:
                    receivedDone = true
                    break loop
                case let .usage(reported):
                    usage = reported
                case let .error(message, status):
                    // The error's own code (401, 402, 429…) decides whether asking again can help; without one it's
                    // the upstream provider failing.
                    throw APIError.http(status: status ?? 502, message: message)
                }
            }
        }
        let cleaned = Prompts.sanitizePolishOutput(text)
        guard !cleaned.isEmpty else { throw APIError.badResponse(L("模型返回了空内容", "the model returned nothing")) }
        if usage == nil || (usage?.inputTokens == nil && usage?.outputTokens == nil && usage?.cost == nil) {
            usage = APIClient.estimatedUsage(messages: messages, output: text)
        }
        return PolishResult(text: cleaned, truncated: isTruncated(finishReason: finishReason, receivedDone: receivedDone),
                            usage: usage)
    }

    /// An answer stopped by the output limit or a content filter is incomplete, and so is a stream that ended without
    /// saying it had finished (neither a finish reason nor `[DONE]`): the connection broke off. Inserting it as the
    /// clean-up would silently lose the rest of the dictation.
    static func isTruncated(finishReason: String?, receivedDone: Bool) -> Bool {
        switch finishReason {
        case "length", "content_filter": return true
        case nil: return !receivedDone
        default: return false
        }
    }
}

extension APIClient {
    /// Token counts guessed from the messages sent and the text received, for servers that report none.
    static func estimatedUsage(messages: [String], output: String) -> RequestUsage {
        let input = messages.reduce(0) { $0 + TokenEstimate.count($1) + 4 }
        return RequestUsage(inputTokens: input, outputTokens: TokenEstimate.count(output), estimated: true)
    }
}

/// Set once, by the stream when the answer starts; read by the first-token deadline.
private final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false

    func set() { lock.withLock { value = true } }
    var isSet: Bool { lock.withLock { value } }
}
