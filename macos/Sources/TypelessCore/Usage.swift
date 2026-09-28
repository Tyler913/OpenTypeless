import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// What one request used, as the server reported it. Every field is optional: OpenRouter reports tokens, audio
/// seconds and the billed cost; OpenAI-compatible servers report tokens at most. `estimated` marks usage filled in
/// from the text because the server reported nothing.
public struct RequestUsage: Sendable, Equatable, Codable {
    public var inputTokens: Int?
    public var outputTokens: Int?
    public var audioSeconds: Double?
    public var cost: Double?
    public var estimated: Bool

    public init(inputTokens: Int? = nil, outputTokens: Int? = nil, audioSeconds: Double? = nil, cost: Double? = nil,
                estimated: Bool = false) {
        self.inputTokens = inputTokens
        self.outputTokens = outputTokens
        self.audioSeconds = audioSeconds
        self.cost = cost
        self.estimated = estimated
    }

    /// Reads a `usage` object: chat completions (`prompt_tokens` / `completion_tokens`) and transcriptions
    /// (`input_tokens` / `output_tokens` / `seconds`), with OpenRouter's `cost` in USD.
    public static func parse(_ object: Any?) -> RequestUsage? {
        guard let usage = object as? [String: Any] else { return nil }
        func tokens(_ key: String, _ alternative: String) -> Int? {
            (number(usage[key]) ?? number(usage[alternative])).map { Int($0.rounded()) }
        }
        let parsed = RequestUsage(inputTokens: tokens("prompt_tokens", "input_tokens"),
                                  outputTokens: tokens("completion_tokens", "output_tokens"),
                                  audioSeconds: number(usage["seconds"]), cost: number(usage["cost"]))
        return parsed.inputTokens == nil && parsed.outputTokens == nil && parsed.audioSeconds == nil && parsed.cost == nil
            ? nil : parsed
    }

    /// A finite, non-negative number (JSON number or numeric string), or nil.
    static func number(_ value: Any?) -> Double? {
        let number: Double
        if let value = value as? NSNumber {
            number = value.doubleValue
        } else if let string = value as? String, let parsed = Double(string) {
            number = parsed
        } else {
            return nil
        }
        return number.isFinite && number >= 0 ? number : nil
    }

    /// Adds the audio length when the server didn't report it.
    public func withAudioSeconds(_ seconds: Double) -> RequestUsage {
        var copy = self
        if copy.audioSeconds == nil { copy.audioSeconds = seconds }
        return copy
    }
}

/// Rough token counts for servers that report no usage: one per CJK character, one per four other characters.
public enum TokenEstimate {
    public static func count(_ text: String) -> Int {
        var cjk = 0
        var other = 0
        for scalar in text.unicodeScalars {
            if WordCount.isCJKWordCharacter(scalar.value) { cjk += 1 } else { other += 1 }
        }
        return cjk + (other + 3) / 4
    }
}

/// A model's price in USD: per million input and output tokens, and per minute of audio for speech-to-text.
/// Entered by the user for providers other than OpenRouter.
public struct ModelPrice: Sendable, Equatable, Codable {
    public var inputPerMillion: Double?
    public var outputPerMillion: Double?
    public var perMinute: Double?

    enum CodingKeys: String, CodingKey {
        case inputPerMillion = "input", outputPerMillion = "output", perMinute = "minute"
    }

    public init(inputPerMillion: Double? = nil, outputPerMillion: Double? = nil, perMinute: Double? = nil) {
        self.inputPerMillion = inputPerMillion
        self.outputPerMillion = outputPerMillion
        self.perMinute = perMinute
    }

    public var hasTokenPrices: Bool { inputPerMillion != nil || outputPerMillion != nil }
    public var isEmpty: Bool { !hasTokenPrices && perMinute == nil }

    /// The key a user-entered price is stored under: the provider and the model ID.
    public static func key(_ provider: ProviderID, _ model: String) -> String {
        provider.rawValue + "|" + model.trimmingCharacters(in: .whitespaces)
    }

    /// From a JSON object (`input`, `output`, `minute`); nil when it holds no price.
    public init?(json: Any?) {
        guard let json = json as? [String: Any] else { return nil }
        self.init(inputPerMillion: RequestUsage.number(json["input"]), outputPerMillion: RequestUsage.number(json["output"]),
                  perMinute: RequestUsage.number(json["minute"]))
        if isEmpty { return nil }
    }
}

/// Works out what a request cost, from the cost the server reported or from a price.
public enum CostEstimator {
    /// A clean-up request. With `preferReported` (OpenRouter, which bills exactly what it reports) the reported cost
    /// wins; otherwise a user-entered price wins over whatever the server says. Nil when there's nothing to go on.
    public static func chat(_ usage: RequestUsage, price: ModelPrice?, preferReported: Bool) -> Double? {
        if preferReported, let reported = usage.cost { return reported }
        if let price, price.hasTokenPrices, usage.inputTokens != nil || usage.outputTokens != nil {
            return fromTokens(usage, price)
        }
        return usage.cost
    }

    /// A speech-to-text request: priced per minute of audio, or per token when the server counts tokens.
    public static func transcription(_ usage: RequestUsage, price: ModelPrice?, preferReported: Bool) -> Double? {
        if preferReported, let reported = usage.cost { return reported }
        if let perMinute = price?.perMinute, let seconds = usage.audioSeconds { return seconds / 60 * perMinute }
        if let price, price.hasTokenPrices, !usage.estimated, usage.inputTokens != nil || usage.outputTokens != nil {
            return fromTokens(usage, price)
        }
        return usage.cost
    }

    private static func fromTokens(_ usage: RequestUsage, _ price: ModelPrice) -> Double {
        let input = Double(usage.inputTokens ?? 0) * (price.inputPerMillion ?? 0)
        let output = Double(usage.outputTokens ?? 0) * (price.outputPerMillion ?? 0)
        return (input + output) / 1_000_000
    }
}

/// OpenRouter's current prices for every model, from its public `/models` list (no key needed). Prices change, so
/// the app refreshes the list regularly and caches the last one on disk. OpenRouter's reported cost per request is
/// what's counted; these prices are shown next to the models and fill in when a response carries no cost.
public struct PriceCatalog: Sendable, Equatable, Codable {
    public var fetchedAt: Date
    public var models: [String: ModelPrice]

    public init(fetchedAt: Date, models: [String: ModelPrice]) {
        self.fetchedAt = fetchedAt
        self.models = models
    }

    public static let empty = PriceCatalog(fetchedAt: .distantPast, models: [:])

    public var isEmpty: Bool { models.isEmpty }

    public func price(_ model: String) -> ModelPrice? { models[model.trimmingCharacters(in: .whitespaces)] }

    public func isOlder(than age: TimeInterval, now: Date = Date()) -> Bool { now.timeIntervalSince(fetchedAt) >= age }

    /// Reads a `/models` response. `pricing.prompt` and `pricing.completion` are USD per token, stored here per
    /// million; negative values mean "varies" (routers) and are skipped.
    public static func parseModels(_ data: Data) -> [String: ModelPrice] {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { return [:] }
        var models: [String: ModelPrice] = [:]
        for model in list {
            guard let id = model["id"] as? String, let pricing = model["pricing"] as? [String: Any] else { continue }
            let input = RequestUsage.number(pricing["prompt"])
            let output = RequestUsage.number(pricing["completion"])
            guard input != nil || output != nil else { continue }
            models[id] = ModelPrice(inputPerMillion: input.map { $0 * 1_000_000 }, outputPerMillion: output.map { $0 * 1_000_000 })
        }
        return models
    }

    /// Downloads the chat and speech-to-text model lists (OpenRouter lists them separately).
    public static func fetch(baseURL: URL? = nil, session: URLSession? = nil) async throws -> PriceCatalog {
        let session = session ?? APIClient.sharedSession
        let root = (baseURL ?? URL(string: ProviderID.openrouter.defaultBaseURL)!).absoluteString
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        var models: [String: ModelPrice] = [:]
        for path in ["/models", "/models?output_modalities=transcription"] {
            var request = URLRequest(url: URL(string: root + path)!)
            request.timeoutInterval = 30
            let (data, response) = try await session.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200..<300).contains(status) else {
                throw APIError.http(status: status, message: APIClient.errorMessage(from: data))
            }
            models.merge(parseModels(data)) { _, new in new }
        }
        guard !models.isEmpty else { throw APIError.badResponse("models list") }
        return PriceCatalog(fetchedAt: Date(), models: models)
    }

    public func jsonData() -> Data? {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        return try? encoder.encode(self)
    }

    public init?(jsonData: Data) {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let decoded = try? decoder.decode(PriceCatalog.self, from: jsonData) else { return nil }
        self = decoded
    }
}
