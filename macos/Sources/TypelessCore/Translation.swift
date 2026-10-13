import Foundation

/// The languages Settings offers to translate dictations into. Any other can be typed in (see `TranslationTarget`).
public enum TranslationLanguage: String, CaseIterable, Sendable {
    case english = "en"
    case chinese = "zh-Hans"
    case japanese = "ja"
    case korean = "ko"
    case spanish = "es"
    case french = "fr"
    case german = "de"
    case portuguese = "pt"
    case russian = "ru"

    /// Its own name, as the picker shows it in every UI language.
    public var nativeName: String {
        switch self {
        case .english: return "English"
        case .chinese: return "简体中文"
        case .japanese: return "日本語"
        case .korean: return "한국어"
        case .spanish: return "Español"
        case .french: return "Français"
        case .german: return "Deutsch"
        case .portuguese: return "Português"
        case .russian: return "Русский"
        }
    }

    /// Its English name, as the translation prompt names it.
    public var promptName: String {
        switch self {
        case .english: return "English"
        case .chinese: return "Simplified Chinese"
        case .japanese: return "Japanese"
        case .korean: return "Korean"
        case .spanish: return "Spanish"
        case .french: return "French"
        case .german: return "German"
        case .portuguese: return "Portuguese"
        case .russian: return "Russian"
        }
    }

    /// The short code the recording capsule shows ("→ EN").
    public var badge: String {
        switch self {
        case .chinese: return "ZH"
        default: return rawValue.uppercased()
        }
    }

    public var script: WritingScript {
        switch self {
        case .english, .spanish, .french, .german, .portuguese: return .latin
        case .chinese: return .han
        case .japanese: return .japanese
        case .korean: return .hangul
        case .russian: return .cyrillic
        }
    }
}

/// The language a translation dictation's text is written in: one of the listed languages, by its code, or any other the
/// user named. Saved as `id`, in Settings and with the dictation in History (see `init(mode:targetLanguage:)`).
public struct TranslationTarget: Hashable, Sendable {
    /// A listed language's code ("en", "zh-Hans"…), or the name the user typed for another.
    public let id: String

    /// nil for an empty name.
    public init?(_ id: String) {
        let trimmed = id.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        self.id = trimmed
    }

    public init(_ language: TranslationLanguage) {
        id = language.rawValue
    }

    /// The listed language, also when its name was typed in as another ("english", "日本語").
    public var language: TranslationLanguage? {
        if let language = TranslationLanguage(rawValue: id) { return language }
        let name = id.lowercased()
        return TranslationLanguage.allCases.first { $0.promptName.lowercased() == name || $0.nativeName.lowercased() == name }
    }

    /// How the prompt names it.
    public var promptName: String { language?.promptName ?? id }

    /// How Settings and History name it.
    public var displayName: String { language?.nativeName ?? id }

    /// What the recording capsule shows while it records or processes a translation.
    public var badge: String { "→ " + (language?.badge ?? String(id.prefix(10))) }

    /// What a translation must mostly be written in; nil for a language typed in, which isn't checked.
    public var script: WritingScript? { language?.script }

    // MARK: History

    /// What a translation dictation saves as its `mode` in History; plain dictation saves none, so records from before
    /// translation, and older versions reading newer records, see a dictation.
    public static let recordMode = "translate"

    /// The target a History record names: its `mode` and `targetLanguage`. nil for a plain dictation, or a mode this
    /// version doesn't know.
    public init?(mode: String?, targetLanguage: String?) {
        guard mode == Self.recordMode, let targetLanguage else { return nil }
        self.init(targetLanguage)
    }
}

/// The key that, pressed while the dictation hotkey is held, turns the recording into a translation (see `ModeChords`).
/// Either side's key counts. A modifier, so it types nothing and works for as long as the hotkey is held.
public enum TranslationChord: String, CaseIterable, Sendable {
    case shift, control, alt

    /// Both of its keys (macOS virtual key codes), less the hotkey's own when that is one of them: a modifier-only
    /// hotkey such as Right ⇧ can't be its own chord, but Left ⇧ still is.
    public func keyCodes(excludingHotkey hotkey: Int64?) -> Set<Int64> {
        let both: Set<Int64>
        switch self {
        case .shift: both = [56, 60]
        case .control: both = [59, 62]
        case .alt: both = [58, 61]
        }
        return both.subtracting(hotkey.map { [$0] } ?? [])
    }
}

/// The writing systems translations are checked against (see `ScriptShare`).
public enum WritingScript: Sendable, Equatable {
    case latin, cyrillic, hangul
    /// Han ideographs: Chinese.
    case han
    /// Kana, with or without Han ideographs: Japanese.
    case japanese
}

/// How much of a text is written in a script, counting words the way `WordCount` does: a Han character or a kana is one,
/// a run of letters of one script another (so "SwiftUI" counts as much as "写"). Digits, punctuation and symbols don't
/// count: numbers are kept as they are in every language.
public enum ScriptShare {
    /// Under this many words there's too little to tell ("OK", "好的").
    public static let minimumWords = 4
    /// The share of words in the target's script under which a translation is taken to have come back in another
    /// language. Names, code and technical terms are kept as they are, so a Chinese translation of a message full of
    /// them can be half Latin ("用 SwiftUI 写一个 macOS app，调用 OpenRouter 的 API…": 0.56), while a translation that
    /// didn't happen is at 0–0.3.
    public static let minimumShare = 0.5

    struct Counts {
        var latin = 0, cyrillic = 0, hangul = 0, han = 0, kana = 0, other = 0
        var total: Int { latin + cyrillic + hangul + han + kana + other }

        func of(_ script: WritingScript) -> Int {
            switch script {
            case .latin: return latin
            case .cyrillic: return cyrillic
            case .hangul: return hangul
            case .han: return han
            case .japanese: return han + kana
            }
        }
    }

    private enum Kind { case latin, cyrillic, hangul, han, kana, other }

    static func counts(_ text: String) -> Counts {
        var counts = Counts()
        var run: Kind?
        for scalar in text.unicodeScalars {
            let value = scalar.value
            let kind: Kind
            switch value {
            case 0x4E00...0x9FFF, 0x3400...0x4DBF, 0x20000...0x3134F, 0xF900...0xFAFF:
                counts.han += 1
                run = nil
                continue
            case 0x3040...0x309F, 0x30A0...0x30FF, 0x31F0...0x31FF, 0xFF66...0xFF9F:
                counts.kana += 1
                run = nil
                continue
            case 0x41...0x5A, 0x61...0x7A, 0xC0...0xD6, 0xD8...0xF6, 0xF8...0x24F, 0x1E00...0x1EFF:
                kind = .latin
            case 0x400...0x52F:
                kind = .cyrillic
            case 0xAC00...0xD7AF, 0x1100...0x11FF, 0x3130...0x318F:
                kind = .hangul
            default:
                switch scalar.properties.generalCategory {
                case .nonspacingMark, .spacingMark, .enclosingMark:
                    // An accent written as its own character stays with its letter.
                    continue
                case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter:
                    kind = .other
                default:
                    run = nil
                    continue
                }
            }
            guard kind != run else { continue }
            run = kind
            switch kind {
            case .latin: counts.latin += 1
            case .cyrillic: counts.cyrillic += 1
            case .hangul: counts.hangul += 1
            case .han, .kana: break
            case .other: counts.other += 1
            }
        }
        return counts
    }

    /// The share of `text`'s words written in `script` (1 when it has none).
    public static func share(of script: WritingScript, in text: String) -> Double {
        let counts = counts(text)
        return counts.total == 0 ? 1 : Double(counts.of(script)) / Double(counts.total)
    }

    /// Whether `text` is mostly written in `script`, or too short to tell. Han characters are shared by Chinese and
    /// Japanese, so once a text has ten Han characters or kana, kana tell them apart: every Japanese sentence has some (at
    /// least one character in ten), Chinese none.
    public static func isMostly(_ text: String, in script: WritingScript) -> Bool {
        let counts = counts(text)
        guard counts.total >= minimumWords else { return true }
        guard Double(counts.of(script)) >= Double(counts.total) * minimumShare else { return false }
        let cjk = counts.han + counts.kana
        guard cjk >= 10 else { return true }
        switch script {
        case .japanese: return counts.kana * 10 >= cjk
        case .han: return counts.kana * 10 < cjk
        default: return true
        }
    }
}

/// A translation dictation's text: its translation when that can be used as it is, otherwise its clean-up (one request
/// more), and the transcript as spoken when that fails too. One request when it works, so a translation is as quick as
/// a clean-up; OpenWhispr's translation chain always sends a second request after the clean-up instead.
public struct TranslatedPolishResult: Sendable {
    /// What the translation request gave, when anything came back. Billed even when it isn't used.
    public let translation: SegmentedPolishResult?
    /// What went wrong when nothing came back.
    public let translationError: APIError?
    /// The clean-up asked for instead, when the translation couldn't be used and clean-up is on.
    public let cleanUp: SegmentedPolishResult?
    /// What went wrong with that clean-up, when nothing came back.
    public let cleanUpError: APIError?

    /// Whether the translation goes in: every part of it came back usable. A translation with a part that didn't would
    /// go in half translated, so it's set aside as a whole.
    public var translated: Bool { translation.map { $0.fallbacks.isEmpty } ?? false }

    /// Why the translation wasn't used: the first unusable part's reason, or `.failed` when nothing came back.
    public var translationFallback: PolishFallback? {
        guard !translated else { return nil }
        return translation?.fallbacks.first?.fallback ?? .failed
    }
}

public enum TranslatedPolish {
    /// Runs `translate`; when its result can't be used as it is (see `TranslatedPolishResult.translated`) or it fails,
    /// runs `cleanUp` instead (nil: clean-up is off, and the transcript goes in as spoken). Throws only when cancelled.
    public static func run(
        translate: @Sendable () async throws -> SegmentedPolishResult,
        cleanUp: (@Sendable () async throws -> SegmentedPolishResult)?
    ) async throws -> TranslatedPolishResult {
        var translation: SegmentedPolishResult?
        var translationError: APIError?
        do {
            translation = try await translate()
        } catch {
            let apiError = APIError.from(error)
            if apiError == .cancelled { throw apiError }
            translationError = apiError
        }
        if let translation, translation.fallbacks.isEmpty {
            return TranslatedPolishResult(translation: translation, translationError: nil, cleanUp: nil, cleanUpError: nil)
        }
        if Task.isCancelled { throw APIError.cancelled }
        guard let cleanUp else {
            return TranslatedPolishResult(translation: translation, translationError: translationError, cleanUp: nil,
                                          cleanUpError: nil)
        }
        do {
            let cleaned = try await cleanUp()
            return TranslatedPolishResult(translation: translation, translationError: translationError, cleanUp: cleaned,
                                          cleanUpError: nil)
        } catch {
            let apiError = APIError.from(error)
            if apiError == .cancelled { throw apiError }
            return TranslatedPolishResult(translation: translation, translationError: translationError, cleanUp: nil,
                                          cleanUpError: apiError)
        }
    }
}
