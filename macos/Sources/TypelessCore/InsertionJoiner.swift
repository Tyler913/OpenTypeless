import Foundation

/// Fits dictated text to the text around the cursor it's pasted at, the way someone typing it there would: a space
/// where it would otherwise run into the word before or after it, a lowercase first word in the middle of a sentence,
/// and no full stop that would end the sentence early ("Hello there.Next sentence" → "Hello there. Next sentence").
///
/// Decided from the actual neighbours, unlike OpenWhispr (a trailing space unless the text is CJK) or whisper-writer and
/// Handy (a trailing space, a dropped final period, by setting). Whatever can't be told for sure is left as it is, which
/// is how the text would be pasted without this.
public enum InsertionJoiner {
    public struct Result: Equatable, Sendable {
        public let text: String
        /// What was changed, for the session log ("space before", "lowercase first word", "no final full stop",
        /// "space after"); empty when nothing was.
        public let changes: [String]
    }

    /// `before` and `after` are the text just before the cursor (or the selection the text replaces) and just after it,
    /// as read from the field: "" at the start or end of the field, nil when it couldn't be read. Without `before`, or at
    /// the start of the field (empty or not: an app that can't tell where its cursor is may say so), nothing is changed;
    /// without `after` only the start of the text is. A dozen characters each side are plenty. `isCommonWord` tells
    /// whether a lowercase word is an ordinary English dictionary word.
    public static func fit(_ text: String, before: String?, after: String?, vocabulary: [String] = [],
                           isCommonWord: (String) -> Bool) -> Result {
        let unchanged = Result(text: text, changes: [])
        // A block of lines (a list, paragraphs) stands on its own rather than inside a sentence.
        guard let before, let previous = before.unicodeScalars.last, let first = text.unicodeScalars.first,
              !text.unicodeScalars.contains(where: { kind($0, after: nil) == .newline }) else { return unchanged }
        let next = after?.unicodeScalars.first
        let previousKind = kind(previous, after: secondLast(before.unicodeScalars))
        // The cursor is inside a word: what the speaker meant there can't be told.
        if previousKind == .word, let next, kind(next, after: previous) == .word { return unchanged }
        // Japanese doesn't put spaces around Latin words the way Chinese text does here.
        let japanese = [before, text, after ?? ""].contains { $0.unicodeScalars.contains(where: isKana) }
        let mark = before.unicodeScalars.last { kind($0, after: nil) != .space }
        var body = text
        var changes: [String] = []

        if let mark, kind(mark, after: nil) == .word || mark == "," || mark == ";",
           let lowered = lowercasingFirstWord(body, vocabulary: vocabulary, isCommonWord: isCommonWord) {
            body = lowered
            changes.append("lowercase first word")
        }
        if let after, runsOn(after, midSentence: mark.map(isMidSentence) ?? false), let trimmed = droppingFinalStop(body) {
            body = trimmed
            changes.append("no final full stop")
        }
        if needsSpace(previous, previousKind, first, kind(first, after: previous), japanese: japanese) {
            body = " " + body
            changes.insert("space before", at: 0)
        }
        if let next, let last = body.unicodeScalars.last,
           needsSpace(last, kind(last, after: secondLast(body.unicodeScalars)), next, kind(next, after: last), japanese: japanese) {
            body += " "
            changes.append("space after")
        }
        return Result(text: body, changes: changes)
    }

    // MARK: Characters

    enum Kind { case space, newline, word, cjk, closing, other }

    /// What a character is for spacing: `word` is a letter or digit of a script that puts spaces between words (Latin,
    /// Cyrillic, Korean…), `cjk` one of a script that doesn't (Han, kana, full-width forms). `previous` tells a straight
    /// quote that closes (after a word) from one that opens.
    static func kind(_ s: Unicode.Scalar, after previous: Unicode.Scalar?) -> Kind {
        if newlines.contains(s) { return .newline }
        if s == "\t" || s.properties.generalCategory == .spaceSeparator { return .space }
        if isLetterOrNumber(s) { return TranscriptJoiner.joinsWithoutSpace(s) ? .cjk : .word }
        if closers.contains(s) { return .closing }
        if s == "\"" || s == "'", let previous, [.word, .cjk, .closing].contains(kind(previous, after: nil)) { return .closing }
        return .other
    }

    private static let newlines: Set<Unicode.Scalar> = ["\n", "\r", "\u{0B}", "\u{0C}", "\u{85}", "\u{2028}", "\u{2029}"]
    /// Punctuation a following word is separated from by a space.
    private static let closers: Set<Unicode.Scalar> = [".", ",", "!", "?", ";", ":", ")", "]", "}", "…", "\u{201D}", "\u{2019}", "»", "%"]

    private static func isLetterOrNumber(_ s: Unicode.Scalar) -> Bool {
        switch s.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter,
             .decimalNumber, .letterNumber, .otherNumber: true
        default: false
        }
    }

    private static func isHan(_ s: Unicode.Scalar) -> Bool {
        switch s.value {
        case 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF: true
        default: false
        }
    }

    private static func isKana(_ s: Unicode.Scalar) -> Bool {
        switch s.value {
        case 0x3040...0x30FF, 0xFF66...0xFF9F: true
        default: false
        }
    }

    private static func secondLast(_ scalars: String.UnicodeScalarView) -> Unicode.Scalar? {
        scalars.dropLast().last
    }

    // MARK: Rules

    /// Whether a space belongs between two neighbouring characters: between words, after closing punctuation, and
    /// between Chinese and Latin letters or digits, as the clean-up prompt writes them ("用 SwiftUI 写"). Never next to a
    /// space, a line break, an opening bracket or quote, or between CJK characters.
    private static func needsSpace(_ a: Unicode.Scalar, _ ka: Kind, _ b: Unicode.Scalar, _ kb: Kind, japanese: Bool) -> Bool {
        switch (ka, kb) {
        case (.word, .word), (.closing, .word): true
        case (.cjk, .word): !japanese && isHan(a)
        case (.word, .cjk): !japanese && isHan(b)
        default: false
        }
    }

    /// Before the cursor, the sentence is still going: the last thing there (spaces aside) is a word or a comma.
    private static func isMidSentence(_ mark: Unicode.Scalar) -> Bool {
        let kind = kind(mark, after: nil)
        return kind == .word || kind == .cjk || [",", ";", "，", "、", "；"].contains(mark)
    }

    /// The text after the cursor carries on the sentence the dictation goes into: it starts (spaces aside) with a
    /// lowercase letter, or with any word while the sentence before the cursor is still going.
    private static func runsOn(_ after: String, midSentence: Bool) -> Bool {
        guard let next = after.unicodeScalars.first(where: { kind($0, after: nil) != .space }) else { return false }
        if next.properties.generalCategory == .lowercaseLetter { return true }
        let kind = kind(next, after: nil)
        return midSentence && (kind == .word || kind == .cjk)
    }

    /// Abbreviations whose full stop stays when they end the text.
    private static let abbreviations: Set<String> = ["etc", "vs", "mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr", "inc", "ltd", "corp"]

    /// The text without its final "." or "。", when that ends a sentence after a word: not "…" or "...", nor the full stop
    /// of an abbreviation ("etc.", "e.g.", "U.S.") or an initial ("J."). nil when it stays.
    static func droppingFinalStop(_ text: String) -> String? {
        let scalars = Array(text.unicodeScalars)
        guard scalars.count >= 2, let stop = scalars.last, stop == "." || stop == "。",
              [.word, .cjk].contains(kind(scalars[scalars.count - 2], after: nil)) else { return nil }
        if stop == "." {
            let word = String(String.UnicodeScalarView(scalars.dropLast().reversed().prefix { kind($0, after: nil) != .space }.reversed()))
            let letters = word.unicodeScalars.filter(isLetterOrNumber)
            if word.contains(".") || letters.count == 1 || abbreviations.contains(word.lowercased()) { return nil }
        }
        return String(String.UnicodeScalarView(scalars.dropLast()))
    }

    /// The text with its first word lowercased, when that word is only capitalised for starting a sentence: an ordinary
    /// English word ("Then", "Maybe", "Don't"). Never "I" ("I'm"), a single letter, an acronym, a word with a capital
    /// inside (GitHub, OpenAI) or an abbreviation ("Mr."), a vocabulary term, or a word the dictionary doesn't know as
    /// an everyday one in lowercase (most names). nil when it stays.
    static func lowercasingFirstWord(_ text: String, vocabulary: [String], isCommonWord: (String) -> Bool) -> String? {
        let token = String(String.UnicodeScalarView(text.unicodeScalars.prefix { ![.space, .newline].contains(kind($0, after: nil)) }))
        var core = Substring(token)
        while let last = core.unicodeScalars.last, closers.contains(last) || last == "\"" || last == "'" { core = core.dropLast() }
        let scalars = Array(core.unicodeScalars)
        guard scalars.count >= 2, (0x41...0x5A).contains(scalars[0].value) else { return nil }
        let isLower = { (s: Unicode.Scalar) in (0x61...0x7A).contains(s.value) }
        for (i, s) in scalars.enumerated().dropFirst() where !isLower(s) {
            // An apostrophe inside a word ("Don't", "It's"), never right after its first letter ("I'm").
            guard s == "'" || s == "\u{2019}", i >= 2, i + 1 < scalars.count, isLower(scalars[i - 1]), isLower(scalars[i + 1])
            else { return nil }
        }
        let word = String(core)
        // "Mr.", "Dr.", "St.": short words with a full stop are abbreviations.
        if token.unicodeScalars.dropFirst(scalars.count).first == ".", scalars.count <= 3 { return nil }
        let lower = word.lowercased()
        let terms = vocabulary.map { $0.split(whereSeparator: \.isWhitespace).first.map { $0.lowercased() } ?? "" }
        guard !terms.contains(lower), isCommonWord(lower.replacingOccurrences(of: "\u{2019}", with: "'")) else { return nil }
        return String(lower.prefix(1)) + text.dropFirst()
    }
}
