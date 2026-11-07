import Foundation

/// Which engine does the speech-to-text step: the provider picked on the Models page, or the speech recognizer built
/// into the system, which needs no key and sends nothing anywhere. Stored under `defaultsKey` with these raw values on
/// both platforms, so a setting means the same thing on each.
public enum SpeechEngine: String, CaseIterable, Sendable {
    case provider
    case onDevice

    public static let defaultsKey = "speechEngine"

    /// The stored value; missing or unknown is the provider, as before there was a choice.
    public init(stored: String?) {
        self = stored.flatMap(SpeechEngine.init(rawValue:)) ?? .provider
    }
}

/// What a dictation needs set up before it can run, and what the status calls "Setup needed".
public enum SpeechSetup {
    /// Whether the speech-to-text step can run: on this Mac always; with a provider once it is configured (its key, or
    /// its address for a local server).
    public static func ready(engine: SpeechEngine, providerConfigured: Bool) -> Bool {
        engine == .onDevice || providerConfigured
    }

    /// Whether a dictation is cleaned up: when that's switched on (globally or by the app's profile) and the clean-up's
    /// provider is configured. With speech-to-text on this Mac, a clean-up with no key is skipped without an error:
    /// someone who started with no key at all gets the text as recognised. With a provider for speech-to-text the
    /// clean-up is tried, and its missing key is reported, as before.
    public static func cleansUp(enabled: Bool, engine: SpeechEngine, providerConfigured: Bool) -> Bool {
        enabled && (providerConfigured || engine == .provider)
    }
}

/// The locale the on-device recognizer listens in.
public enum OnDeviceLocale {
    /// One of `supported` (BCP-47 identifiers such as "en-US") for the spoken-language hint `language` (an ISO code such
    /// as "zh", or "" to auto-detect), or nil when there's no model for it. A language picks the variant in the region
    /// of the user's own languages (`preferred`, most preferred first, as `Locale.preferredLanguages` lists them), else
    /// its most likely one ("en" → en-US, "zh" → zh-CN, "pt" → pt-BR), else the first. Auto-detect takes the first of
    /// `preferred` there is a model for, else English.
    public static func resolve(language: String, preferred: [String], supported: [String]) -> String? {
        let language = language.trimmingCharacters(in: .whitespaces)
        if !language.isEmpty { return match(language, preferred: preferred, supported: supported) }
        for wanted in preferred {
            if let match = match(wanted, preferred: preferred, supported: supported) { return match }
        }
        return match("en", preferred: preferred, supported: supported)
    }

    private static func match(_ wanted: String, preferred: [String], supported: [String]) -> String? {
        guard let code = languageCode(wanted) else { return nil }
        let candidates = supported.filter { languageCode($0) == code }
        guard !candidates.isEmpty else { return nil }
        // The region asked for, then the one of the user's own languages in this language, then the likeliest.
        var regions: [String] = []
        if let region = region(wanted) { regions.append(region) }
        regions += preferred.filter { languageCode($0) == code }.compactMap(region)
        if let likely = region(Locale.Language(identifier: wanted).maximalIdentifier) { regions.append(likely) }
        for region in regions {
            if let match = candidates.first(where: { self.region($0) == region }) { return match }
        }
        return candidates.sorted().first
    }

    private static func languageCode(_ identifier: String) -> String? {
        Locale(identifier: identifier).language.languageCode?.identifier.lowercased()
    }

    private static func region(_ identifier: String) -> String? {
        Locale(identifier: identifier).region?.identifier.uppercased()
    }
}

/// Recognises speech on this Mac (Apple's SpeechAnalyzer in the app; a fake in tests).
public protocol OnDeviceRecognizer: Sendable {
    /// The locales there's a model for, installed or not, as BCP-47 identifiers ("en-US").
    func supportedLocales() async -> [String]
    /// What is said in `samples` (16 kHz mono), recognised in `locale` (one of `supportedLocales`), with `vocabulary` as
    /// words to expect. The first time a language is used its model is downloaded first.
    func transcribe(samples: [Int16], locale: String, vocabulary: [String]) async throws -> String
}

/// Where a chunk's speech-to-text comes from: a provider's API (`APIClient`) or the recognizer on this Mac
/// (`OnDeviceRoute`). `TranscriptionPipeline` chunks, retries and joins the same way for both.
public protocol SpeechToTextRoute: Sendable {
    /// One chunk's transcript, with what it used (`usage.audioSeconds` filled in), retried by `policy`.
    func transcribeDetailedWithRetry(samples: [Int16], options: APIClient.TranscriptionOptions, policy: RetryPolicy,
                                     onRetry: ((Int, APIError) -> Void)?) async throws -> APIClient.TranscriptionResult
}

extension APIClient: SpeechToTextRoute {}

/// Speech-to-text by the recognizer on this Mac: no key, no network once the language's model is there, and free.
public struct OnDeviceRoute: SpeechToTextRoute {
    public let recognizer: any OnDeviceRecognizer
    private let preferredLanguages: [String]

    /// One attempt more for a recognizer that failed: it either works or it doesn't, so there's little to wait for.
    public static let policy = RetryPolicy(maxAttempts: 2, baseDelay: 0.2)

    public init(recognizer: any OnDeviceRecognizer, preferredLanguages: [String] = Locale.preferredLanguages) {
        self.recognizer = recognizer
        self.preferredLanguages = preferredLanguages
    }

    public static var unsupportedLanguageMessage: String {
        L("设备端识别不支持这种语言。", "On-device recognition doesn't support this language.")
    }

    /// The locale `language` (see `OnDeviceLocale`) is recognised in, or nil when it can't be.
    public func locale(for language: String?) async -> String? {
        OnDeviceLocale.resolve(language: language ?? "", preferred: preferredLanguages,
                               supported: await recognizer.supportedLocales())
    }

    public func transcribeDetailedWithRetry(samples: [Int16], options: APIClient.TranscriptionOptions, policy: RetryPolicy,
                                            onRetry: ((Int, APIError) -> Void)?) async throws -> APIClient.TranscriptionResult {
        guard let locale = await locale(for: options.language) else {
            throw APIError.unavailable(Self.unsupportedLanguageMessage)
        }
        let text = try await policy.run(onRetry: onRetry) { _ in
            try await recognizer.transcribe(samples: samples, locale: locale, vocabulary: options.vocabulary)
        }
        // Nothing is billed: counted as costing nothing, so Home's spend stays as it is and nothing is "unpriced".
        return APIClient.TranscriptionResult(
            text: Self.tidy(text),
            usage: RequestUsage(audioSeconds: AudioFormat.seconds(forSampleCount: samples.count), cost: 0))
    }
}

extension OnDeviceRoute {
    /// The recognizer's text without the spaces it leaves in Chinese and Japanese: before full-width punctuation
    /// ("你好 ，这是" → "你好，这是"), and between two characters written without spaces. Latin text keeps its spaces.
    public static func tidy(_ text: String) -> String {
        let scalars = Array(text.unicodeScalars)
        var result = String.UnicodeScalarView()
        var index = 0
        while index < scalars.count {
            let scalar = scalars[index]
            guard CharacterSet.whitespaces.contains(scalar) else {
                result.append(scalar)
                index += 1
                continue
            }
            var end = index
            while end < scalars.count, CharacterSet.whitespaces.contains(scalars[end]) { end += 1 }
            let before = result.last
            let after = end < scalars.count ? scalars[end] : nil
            let drop: Bool
            if let after, isWidePunctuation(after) {
                drop = true
            } else if let before, isWidePunctuation(before) {
                drop = true
            } else if let before, let after {
                drop = TranscriptJoiner.joinsWithoutSpace(before) && TranscriptJoiner.joinsWithoutSpace(after)
            } else {
                drop = false
            }
            if !drop { result.append(contentsOf: scalars[index..<end]) }
            index = end
        }
        return String(result).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// CJK punctuation and full-width forms ("，", "。", "！"), which take no space on either side.
    private static func isWidePunctuation(_ scalar: Unicode.Scalar) -> Bool {
        (0x3000...0x303F).contains(scalar.value) || (0xFF00...0xFF65).contains(scalar.value)
    }
}
