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

/// How much of a translation is written in its target language's script, counting words the way `WordCount` does: a Han
/// character or a kana is one word, a run of letters or digits another ("e-mail", "foo_bar" and "3.5" once each, so
/// "SwiftUI" counts as much as "写"). Numbers don't count: they're kept as they are in every language. Nor do names, code
/// and terms kept from the transcript as they were, since a translation keeps them by design ("Contact 欧阳娜娜.",
/// "用 React、TypeScript 和 Vite。"); what's left of the other scripts is text that wasn't translated.
public enum ScriptShare {
    /// Under this many words there's too little to tell ("OK", "好的", "Contact 欧阳娜娜.").
    public static let minimumWords = 4
    /// The share of the words in the target's script among those not kept as names, under which a translation is taken
    /// to have come back in another language: a translation that didn't happen is at 0–0.3, a real one near 1.
    public static let minimumShare = 0.5
    /// And among all the words, names included: a text that is almost all names and terms kept as they were, with no
    /// target-language text around them, wasn't translated either. A real one full of terms keeps a third
    /// ("用 React、TypeScript、Vite 和 Tailwind。": 2 of 6).
    public static let minimumTargetShare = 0.2
    /// A run of Han characters or kana up to this long, found as it is in the transcript, is a name kept as it was
    /// (a Chinese name is two to four characters: "张伟", "欧阳娜娜"); a longer one is a phrase left untranslated, and
    /// once a translation has one, its short runs are taken for more of the same ("Please fix the 验证码不显示 bug on
    /// the 登录页面").
    static let nameLength = 4

    enum Kind: Equatable { case latin, cyrillic, hangul, other }

    /// A word as `WordCount` counts it: a run of letters or digits, or a run of Han characters and kana (each a word).
    struct Token: Equatable {
        let text: String
        /// A run of Han characters and kana.
        let isCJK: Bool
        /// A run of letters or digits: the script of its first letter; nil for a number.
        let kind: Kind?

        /// How many words `WordCount` counts in it.
        var words: Int { isCJK ? text.unicodeScalars.count : 1 }
    }

    /// `text` cut into words with `WordCount`'s rules, joiners and all.
    static func tokens(_ text: String) -> [Token] {
        let scalars = Array(text.unicodeScalars)
        var tokens: [Token] = []
        var word = String.UnicodeScalarView()
        var run = String.UnicodeScalarView()
        func endWord() {
            guard !word.isEmpty else { return }
            tokens.append(Token(text: String(word), isCJK: false, kind: kind(of: word)))
            word = String.UnicodeScalarView()
        }
        func endRun() {
            guard !run.isEmpty else { return }
            tokens.append(Token(text: String(run), isCJK: true, kind: nil))
            run = String.UnicodeScalarView()
        }
        for (i, scalar) in scalars.enumerated() {
            if WordCount.isCJKWordCharacter(scalar.value) {
                endWord()
                run.append(scalar)
            } else if WordCount.isWordCharacter(scalar) {
                endRun()
                word.append(scalar)
            } else if !word.isEmpty, WordCount.isJoiner(scalar), i + 1 < scalars.count,
                      WordCount.isWordCharacter(scalars[i + 1]), !WordCount.isCJKWordCharacter(scalars[i + 1].value) {
                word.append(scalar)
            } else {
                endWord()
                endRun()
            }
        }
        endWord()
        endRun()
        return tokens
    }

    /// The script of a word's first letter; nil when it has none (a number).
    private static func kind(of word: String.UnicodeScalarView) -> Kind? {
        for scalar in word {
            switch scalar.value {
            case 0x41...0x5A, 0x61...0x7A, 0xC0...0xD6, 0xD8...0xF6, 0xF8...0x24F, 0x1E00...0x1EFF: return .latin
            case 0x400...0x52F: return .cyrillic
            case 0xAC00...0xD7AF, 0x1100...0x11FF, 0x3130...0x318F: return .hangul
            default:
                switch scalar.properties.generalCategory {
                case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter: return .other
                default: continue
                }
            }
        }
        return nil
    }

    private static func isKana(_ scalar: Unicode.Scalar) -> Bool { (0x3040...0x30FF).contains(scalar.value) }

    /// A word kept from the transcript that reads as a name, code or a term rather than ordinary text left untranslated:
    /// it has a capital letter, a digit or a joiner ("SwiftUI", "GPT6", "e-mail", "foo_bar"), or its script has no
    /// capitals (Korean). "the" and "report" don't.
    static func looksLikeName(_ word: String) -> Bool {
        var cased = false
        for scalar in word.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .uppercaseLetter, .titlecaseLetter, .decimalNumber, .letterNumber, .otherNumber: return true
            case .lowercaseLetter: cased = true
            default: if WordCount.isJoiner(scalar) { return true }
            }
        }
        return !cased
    }

    /// The share of `text`'s words written in `script` (1 when it has none), names included.
    public static func share(of script: WritingScript, in text: String) -> Double {
        var inScript = 0, total = 0
        for token in tokens(text) {
            if token.isCJK {
                for scalar in token.text.unicodeScalars {
                    total += 1
                    if script == .japanese || (script == .han && !isKana(scalar)) { inScript += 1 }
                }
            } else if let kind = token.kind {
                total += 1
                if matches(kind, script) { inScript += 1 }
            }
        }
        return total == 0 ? 1 : Double(inScript) / Double(total)
    }

    private static func matches(_ kind: Kind, _ script: WritingScript) -> Bool {
        switch (kind, script) {
        case (.latin, .latin), (.cyrillic, .cyrillic), (.hangul, .hangul): return true
        default: return false
        }
    }

    /// Whether `text`, a translation of `transcript`, is mostly written in `script`, or too short to tell. Words of other
    /// scripts found in the transcript or the speaker's vocabulary are names kept as they were (see `looksLikeName`,
    /// `nameLength`), and only count against the translation when they're nearly all of it (`minimumTargetShare`).
    ///
    /// Han characters are shared by Chinese and Japanese, so once a text has ten Han characters or kana, kana tell them
    /// apart: every Japanese sentence has some (at least one character in ten), Chinese none.
    public static func isMostly(_ text: String, in script: WritingScript, transcript: String = "",
                                vocabulary: [String] = []) -> Bool {
        let spoken = Set(tokens(transcript).filter { !$0.isCJK }.map { $0.text.lowercased() })
        let terms = vocabulary.flatMap { tokens($0) }
        let termWords = Set(terms.filter { !$0.isCJK }.map { $0.text.lowercased() })
        let termRuns = Set(terms.filter(\.isCJK).map(\.text))
        var target = 0, untranslated = 0, kept = 0, han = 0, kana = 0
        // Short runs of another script's Han characters or kana found in the transcript: names, unless the translation
        // also left a phrase of that script untranslated.
        var names = 0, nameCharacters = 0, leftAPhrase = false
        for token in tokens(text) {
            if token.isCJK {
                var foreign = 0
                for scalar in token.text.unicodeScalars {
                    if isKana(scalar) { kana += 1 } else { han += 1 }
                    if script == .japanese || (script == .han && !isKana(scalar)) { target += 1 } else { foreign += 1 }
                }
                guard foreign > 0 else { continue }
                if termRuns.contains(token.text) {
                    kept += 1
                } else if token.text.unicodeScalars.count <= nameLength, transcript.contains(token.text) {
                    names += 1
                    nameCharacters += foreign
                } else {
                    untranslated += foreign
                    leftAPhrase = true
                }
            } else if let kind = token.kind {
                let word = token.text.lowercased()
                if matches(kind, script) {
                    target += 1
                } else if termWords.contains(word) || (spoken.contains(word) && looksLikeName(token.text)) {
                    kept += 1
                } else {
                    untranslated += 1
                }
            }
        }
        if leftAPhrase { untranslated += nameCharacters } else { kept += names }
        let total = target + untranslated + kept
        guard total >= minimumWords else { return true }
        guard Double(target) >= Double(target + untranslated) * minimumShare,
              Double(target) >= Double(total) * minimumTargetShare else { return false }
        let cjk = han + kana
        guard cjk >= 10 else { return true }
        switch script {
        case .japanese: return kana * 10 >= cjk
        case .han: return kana * 10 < cjk
        default: return true
        }
    }

    /// A text's length in words that compare across writing systems: a Han character or a hiragana counts half a word
    /// (Chinese and Japanese take about two for an English word), a run of katakana one (it's a loanword, however long:
    /// "ローカリゼーション" for "localization"), any other word one.
    public static func length(_ text: String) -> Double {
        tokens(text).reduce(0) { total, token in
            guard token.isCJK else { return total + 1 }
            var length = 0.0, inKatakana = false
            for scalar in token.text.unicodeScalars {
                let katakana = (0x30A0...0x30FF).contains(scalar.value)
                if katakana, !inKatakana { length += 1 } else if !katakana { length += 0.5 }
                inKatakana = katakana
            }
            return total + length
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
    /// Every answer that comes back goes into `ledger` as soon as it's in, so a translation set aside for the clean-up
    /// is still counted when the dictation is cancelled while the clean-up runs.
    public static func run(
        translate: @Sendable () async throws -> SegmentedPolishResult,
        cleanUp: (@Sendable () async throws -> SegmentedPolishResult)?,
        ledger: PolishLedger? = nil
    ) async throws -> TranslatedPolishResult {
        var translation: SegmentedPolishResult?
        var translationError: APIError?
        do {
            let result = try await translate()
            ledger?.add(result)
            translation = result
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
            ledger?.add(cleaned)
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

/// The clean-up and translation answers one dictation got, kept as they come in. What they cost is counted from here,
/// also when the dictation is cancelled before its text goes in: a translation set aside for the clean-up was billed
/// all the same. Owned by one run of the processing, so a later dictation never counts it.
public final class PolishLedger: @unchecked Sendable {
    private let lock = NSLock()
    private var results: [SegmentedPolishResult] = []

    public init() {}

    public func add(_ result: SegmentedPolishResult) {
        lock.withLock { results.append(result) }
    }

    public var all: [SegmentedPolishResult] { lock.withLock { results } }
}
