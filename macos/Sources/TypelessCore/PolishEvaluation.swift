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
            case let .error(message, _): error = message
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
    /// The request carries text around the cursor, so its output is cleaned of that as the app's is. Optional, so a plan
    /// saved before it existed still reads.
    public var sentContext: Bool?
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
                              upperBoundUSD: upper, inputTokenBound: inputBound, outputTokenBound: outputBound,
                              sentContext: Prompts.contextBlock(options.context) != nil)
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
            result.output = Prompts.sanitizePolishOutput(result.rawOutput, sentContext: plan.sentContext == true)
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
        return 1 - Double(distance(x, y)) / Double(max(x.count, y.count))
    }

    /// Edit distance between two normalised texts.
    static func distance(_ x: [Character], _ y: [Character]) -> Int {
        // A shared start and end never change the distance, and after a small fix to a long text they are nearly all
        // of it: only the part in between needs the quadratic table (the learner runs this on the main thread).
        var start = 0
        while start < x.count, start < y.count, x[start] == y[start] { start += 1 }
        var end = 0
        while end < x.count - start, end < y.count - start, x[x.count - 1 - end] == y[y.count - 1 - end] { end += 1 }
        let n = x.count - start - end, m = y.count - start - end
        var previous = Array(0...m)
        var current = [Int](repeating: 0, count: m + 1)
        for i in stride(from: 1, through: n, by: 1) {
            current[0] = i
            for j in stride(from: 1, through: m, by: 1) {
                current[j] = x[start + i - 1] == y[start + j - 1]
                    ? previous[j - 1]
                    : 1 + min(previous[j - 1], previous[j], current[j - 1])
            }
            swap(&previous, &current)
        }
        return previous[m]
    }

    /// Four letters in a row of a normalised text.
    struct Gram: Hashable {
        static let length = 4
        let a, b, c, d: UInt32
    }

    /// Every run of four letters, by where it starts.
    static func grams(_ text: [UInt32]) -> [Gram] {
        guard text.count >= Gram.length else { return [] }
        return (0...(text.count - Gram.length)).map { Gram(a: text[$0], b: text[$0 + 1], c: text[$0 + 2], d: text[$0 + 3]) }
    }

    /// One place where `y` differs from `x`: the letters of `y` there (positions), and how many of `x`'s it replaces.
    struct Edit: Equatable {
        let output: Range<Int>
        let replaced: Int
    }

    /// Where `y` differs from `x`, by an edit-distance alignment of the two: each run of changes between letters they
    /// share. Nil for texts too long to align here (over `maxCells` pairs of letters, a table that size in bytes).
    static func edits(_ x: [UInt32], _ y: [UInt32], maxCells: Int = 8_000_000) -> [Edit]? {
        var start = 0
        while start < x.count, start < y.count, x[start] == y[start] { start += 1 }
        var end = 0
        while end < x.count - start, end < y.count - start, x[x.count - 1 - end] == y[y.count - 1 - end] { end += 1 }
        let n = x.count - start - end, m = y.count - start - end
        guard n * m <= maxCells else { return nil }
        // How each cell was reached: 0 the same letter, 1 another letter in its place, 2 a letter of x left out,
        // 3 a letter of y added. Ties go in that order, as on Windows.
        var moves = [UInt8](repeating: 0, count: (n + 1) * (m + 1))
        var previous = Array(0...m)
        var current = [Int](repeating: 0, count: m + 1)
        for i in stride(from: 1, through: n, by: 1) {
            current[0] = i
            for j in stride(from: 1, through: m, by: 1) {
                let same = x[start + i - 1] == y[start + j - 1]
                let diagonal = previous[j - 1] + (same ? 0 : 1), leftOut = previous[j] + 1, added = current[j - 1] + 1
                if diagonal <= leftOut, diagonal <= added {
                    current[j] = diagonal
                    moves[i * (m + 1) + j] = same ? 0 : 1
                } else if leftOut <= added {
                    current[j] = leftOut
                    moves[i * (m + 1) + j] = 2
                } else {
                    current[j] = added
                    moves[i * (m + 1) + j] = 3
                }
            }
            swap(&previous, &current)
        }
        // Back from the end; a run of changes ends at the next shared letter.
        var runs: [Edit] = []
        var i = n, j = m
        var runEnd: Int?
        var replaced = 0
        while i > 0 || j > 0 {
            let move = i == 0 ? 3 : j == 0 ? 2 : moves[i * (m + 1) + j]
            if move == 0 {
                if let runEnd { runs.append(Edit(output: (start + j)..<(start + runEnd), replaced: replaced)) }
                runEnd = nil
                replaced = 0
                i -= 1
                j -= 1
                continue
            }
            if runEnd == nil { runEnd = j }
            switch move {
            case 1: i -= 1; j -= 1; replaced += 1
            case 2: i -= 1; replaced += 1
            default: j -= 1
            }
        }
        if let runEnd { runs.append(Edit(output: start..<(start + runEnd), replaced: replaced)) }
        return runs.reversed()
    }

    /// `normalized`, as Unicode scalar values, which compare far faster than characters.
    static func scalars(_ text: String) -> [UInt32] {
        normalized(text).unicodeScalars.map(\.value)
    }

    static func latinWords(_ text: String) -> [String] {
        text.lowercased().split { !($0.isASCII && ($0.isLetter || $0.isNumber)) }.map(String.init)
            .filter { $0.first?.isLetter == true }
    }

    /// Built once: fetching the three sets again for every character was most of the time spent comparing long texts.
    private static let ignored = CharacterSet.whitespacesAndNewlines.union(.punctuationCharacters).union(.symbols)

    static func normalized(_ text: String) -> String {
        String(text.lowercased().unicodeScalars.filter { !ignored.contains($0) }.map(Character.init))
    }
}
