import Foundation
import Darwin

/// Evaluation-only accounting. The normal App request and retry path is deliberately untouched.
public struct EvaluationUsage: Codable, Sendable, Equatable {
    public var prompt_tokens: Int?
    public var completion_tokens: Int?
    public var total_tokens: Int?
    public var cost: Double?
}

public struct EvaluationAttempt: Codable, Sendable {
    public var requestID: String?
    public var responseModel: String?
    public var provider: String?
    public var usage: EvaluationUsage?
    public var costSource: String?
    public var finishReason: String?
    public var httpStatus: Int?
    public var error: String?
    public var billingErrors: [String] = []
    public var rawOutput = ""
    public var output = ""
    public var seconds = 0.0
    /// Time to the first content token: what a hedged request waits on.
    public var firstTokenSeconds: Double?
    public var retryCount = 0
    public var receivedDone = false
    public var truncated: Bool { finishReason == "length" }

    public init() {}

    public mutating func consume(_ line: String) {
        guard line.hasPrefix("data:") else { return }
        let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
        if payload == "[DONE]" { receivedDone = true; return }
        guard let data = payload.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            error = "malformed SSE data"; return
        }
        requestID = obj["id"] as? String ?? requestID
        responseModel = obj["model"] as? String ?? responseModel
        provider = obj["provider"] as? String ?? provider
        if let u = obj["usage"] as? [String: Any],
           let bytes = try? JSONSerialization.data(withJSONObject: u),
           let parsed = try? JSONDecoder().decode(EvaluationUsage.self, from: bytes) {
            usage = parsed
            if let cost = parsed.cost, cost.isFinite, cost >= 0 { costSource = "stream.usage.cost" }
            else { usage?.cost = nil }
        }
        for event in SSEParser().parse(line: line) {
            switch event {
            case let .content(piece): rawOutput += piece
            case let .finished(reason): finishReason = reason
            case let .error(message): error = message
            case .done: receivedDone = true
            case .usage: break // read from the raw chunk above, with the evaluation's own fields
            }
        }
    }
}

public struct EvaluationPlan: Codable, Sendable {
    public let model: String
    public let bodyJSON: String
    public let upperBoundUSD: Double
    public let inputTokenBound: Int
    public let outputTokenBound: Int
}

extension APIClient {
    /// Reuses production prompt/body, adding only evaluation routing price/supported-parameter guards.
    public func evaluationPlan(transcript: String, options: PolishOptions,
                               promptPrice: Double, completionPrice: Double, providerTag: String? = nil,
                               providerRouting: [String: Any]? = nil) throws -> EvaluationPlan {
        guard isOpenRouter, promptPrice.isFinite, completionPrice.isFinite,
              promptPrice >= 0, completionPrice >= 0,
              options.modelInfo?.supportedParameters.contains("max_tokens") == true else {
            throw APIError.badResponse("evaluation requires verified prices and max_tokens support")
        }
        // Same request and routing as the app, so latency and reliability match what users see.
        // (Price caps here once pinned models to overloaded cheap endpoints and skewed the results.)
        var body = polishBody(transcript: transcript, options: options, includeOptional: true)
        // Byte-level upper estimate plus ample chat-template overhead; 20% billing safety margin.
        let messages = body["messages"] as! [[String: String]]
        if let providerRouting {
            // Comparing routing preferences (e.g. sorting by latency vs throughput).
            body["provider"] = providerRouting
        }
        if let providerTag {
            // Explicitly benchmarking a single upstream provider.
            body["provider"] = ["only": [providerTag], "allow_fallbacks": false]
        }
        let inputBound = messages.reduce(2048) { $0 + ($1["content"] ?? "").utf8.count }
        let outputBound = body["max_tokens"] as! Int
        let upper = (Double(inputBound) * promptPrice + Double(outputBound) * completionPrice) * 1.2
        let data = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])
        return EvaluationPlan(model: options.model, bodyJSON: String(decoding: data, as: UTF8.self),
                              upperBoundUSD: upper, inputTokenBound: inputBound, outputTokenBound: outputBound)
    }

    /// One attempt only: failures and missing billing remain visible, with no hidden retries.
    public func evaluatePolish(plan: EvaluationPlan) async -> EvaluationAttempt {
        var result = EvaluationAttempt()
        let start = ProcessInfo.processInfo.systemUptime
        do {
            var req = try request(path: "chat/completions", idleTimeout: 25)
            req.setValue("text/event-stream", forHTTPHeaderField: "Accept")
            req.httpBody = Data(plan.bodyJSON.utf8)
            let (bytes, response) = try await session.bytes(for: req)
            let http = response as? HTTPURLResponse
            result.httpStatus = http?.statusCode
            result.requestID = http?.value(forHTTPHeaderField: "X-Generation-Id")
            guard let status = http?.statusCode, (200..<300).contains(status) else {
                var data = Data()
                for try await b in bytes { data.append(b); if data.count >= 4096 { break } }
                throw APIError.http(status: http?.statusCode ?? 0, message: Self.errorMessage(from: data))
            }
            for try await line in bytes.lines {
                result.consume(line)
                if result.firstTokenSeconds == nil, !result.rawOutput.isEmpty {
                    result.firstTokenSeconds = ProcessInfo.processInfo.systemUptime - start
                }
                if result.receivedDone { break }
            }
            if !result.receivedDone && result.error == nil { result.error = "stream ended without DONE" }
            if result.finishReason == nil && result.error == nil { result.error = "missing finish_reason" }
            result.output = Prompts.sanitizePolishOutput(result.rawOutput)
            if result.output.isEmpty && result.error == nil { result.error = "empty output" }
        } catch { result.error = error.localizedDescription }
        result.seconds = ProcessInfo.processInfo.systemUptime - start
        // Billing lookup is excluded from user-visible completion latency. Never infer zero cost.
        if result.usage?.cost == nil, let id = result.requestID {
            for delay in [0.5, 1.5, 3.0] {
                try? await Task.sleep(nanoseconds: UInt64(delay * 1e9))
                do {
                    var components = URLComponents(url: url("generation"), resolvingAgainstBaseURL: false)!
                    components.queryItems = [URLQueryItem(name: "id", value: id)]
                    var req = try request(path: "generation", idleTimeout: 15)
                    req.url = components.url; req.httpMethod = "GET"
                    let (data, response) = try await session.data(for: req)
                    guard let http = response as? HTTPURLResponse, http.statusCode == 200,
                          let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                          let bill = obj["data"] as? [String: Any],
                          let cost = bill["total_cost"] as? Double, cost.isFinite, cost >= 0 else {
                        throw APIError.badResponse("generation billing unavailable")
                    }
                    result.usage = EvaluationUsage(prompt_tokens: bill["native_tokens_prompt"] as? Int,
                                                   completion_tokens: bill["native_tokens_completion"] as? Int,
                                                   total_tokens: nil, cost: cost)
                    result.costSource = "generation.total_cost"
                    result.provider = bill["provider_name"] as? String ?? result.provider
                    break
                } catch { result.billingErrors.append(error.localizedDescription) }
            }
        }
        return result
    }
}

/// Durable reservations survive cancellation/crashes. Hold an exclusive process lock for the run.
/// Unknown costs keep their entire reservation, including HTTP errors with no generation ID.
public final class EvaluationBudget {
    public struct Entry: Codable {
        public let id: String
        public let upperBound: Double
        public var cost: Double?
        public var requestID: String?
    }
    public struct State: Codable {
        public let limit: Double
        public var entries: [Entry]
        public var spent: Double { entries.compactMap(\.cost).reduce(0, +) }
        public var reserved: Double { entries.filter { $0.cost == nil }.map(\.upperBound).reduce(0, +) }
    }
    public private(set) var state: State
    private let url: URL
    private let fd: Int32
    public init(url: URL, limit: Double) throws {
        guard limit > 0, limit <= 2, limit.isFinite else { throw APIError.badResponse("budget must be in (0, 2]") }
        self.url = url
        fd = Darwin.open(url.path + ".lock", O_CREAT | O_RDWR, S_IRUSR | S_IWUSR)
        guard fd >= 0 else { throw APIError.badResponse("cannot open budget lock") }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            close(fd); throw APIError.badResponse("another evaluation owns this budget")
        }
        do {
            if FileManager.default.fileExists(atPath: url.path) {
                state = try JSONDecoder().decode(State.self, from: Data(contentsOf: url))
                guard state.limit == limit else { throw APIError.badResponse("cannot change existing budget limit") }
            } else { state = State(limit: limit, entries: []) }
        } catch { close(fd); throw error }
    }
    deinit { flock(fd, LOCK_UN); close(fd) }
    private func save() throws {
        try JSONEncoder().encode(state).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
    public func check(next: Double, future: Double = 0) throws {
        guard next.isFinite, future.isFinite, next >= 0, future >= 0,
              state.spent + state.reserved + next + future <= state.limit else {
            throw APIError.badResponse("budget exhausted: paid + unresolved/in-flight + next + future exceeds cap")
        }
    }
    public func reserve(_ upper: Double, future: Double = 0) throws -> String {
        try check(next: upper, future: future)
        let id = UUID().uuidString
        state.entries.append(Entry(id: id, upperBound: upper, cost: nil, requestID: nil))
        try save() // Must be durable BEFORE any network request.
        return id
    }
    public func settle(_ id: String, cost: Double?, requestID: String?) throws {
        guard let i = state.entries.firstIndex(where: { $0.id == id }) else { throw APIError.badResponse("unknown reservation") }
        if let cost, (!cost.isFinite || cost < 0) { throw APIError.badResponse("invalid billed cost") }
        state.entries[i].cost = cost
        state.entries[i].requestID = requestID
        try save()
        if let cost, cost > state.entries[i].upperBound {
            throw APIError.badResponse("billing exceeded conservative bound; stop and audit")
        }
    }
}

/// Case-independent measures of how much a clean-up changed the speaker's text.
public enum PolishMetrics {
    /// Fillers and correction phrases the clean-up is supposed to remove.
    static let removableLatin: Set<String> = [
        "um", "uh", "umm", "uhh", "er", "ah", "oh", "like", "so", "ok", "okay", "yeah", "well",
        "actually", "basically", "mean", "you", "know", "wait", "no", "sorry", "scratch", "that",
    ]

    /// Share of the English words in a mostly-Chinese input that survive in the output. Translating
    /// "dark mode" to 深色模式 lowers it; recognition fixes like "swift UI" → SwiftUI don't.
    /// Returns nil when the input is mostly English or has no English words worth checking.
    public static func latinRetention(input: String, output: String, ignoring: [String] = []) -> Double? {
        // A Chinese character is roughly one word: compare counts to find the host language.
        let cjk = input.unicodeScalars.filter(TranscriptJoiner.isCJK).count
        guard cjk >= latinWords(input).count else { return nil }
        let ignored = Set(ignoring.flatMap(latinWords))
        let words = Set(latinWords(input)).subtracting(removableLatin).subtracting(ignored).filter { $0.count > 1 }
        guard !words.isEmpty else { return nil }
        let haystack = output.lowercased().filter { !$0.isWhitespace && $0 != "-" }
        return Double(words.filter { haystack.contains($0) }.count) / Double(words.count)
    }

    /// Character-level similarity (1 − normalised edit distance) after dropping whitespace and
    /// punctuation, so spacing and punctuation fixes don't count as edits.
    public static func similarity(_ a: String, _ b: String) -> Double {
        let x = Array(normalized(a)), y = Array(normalized(b))
        if x.isEmpty || y.isEmpty { return x.count == y.count ? 1 : 0 }
        var previous = Array(0...y.count)
        for i in 1...x.count {
            var current = [i] + [Int](repeating: 0, count: y.count)
            for j in 1...y.count {
                current[j] = x[i - 1] == y[j - 1]
                    ? previous[j - 1]
                    : 1 + min(previous[j - 1], previous[j], current[j - 1])
            }
            previous = current
        }
        return 1 - Double(previous[y.count]) / Double(max(x.count, y.count))
    }

    static func latinWords(_ text: String) -> [String] {
        text.lowercased().split { !($0.isASCII && ($0.isLetter || $0.isNumber)) }.map(String.init)
            .filter { $0.first?.isLetter == true }
    }

    static func normalized(_ text: String) -> String {
        String(text.lowercased().unicodeScalars.filter {
            !CharacterSet.whitespacesAndNewlines.contains($0) && !CharacterSet.punctuationCharacters.contains($0)
                && !CharacterSet.symbols.contains($0)
        }.map(Character.init))
    }
}
