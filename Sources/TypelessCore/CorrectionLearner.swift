import Foundation

/// A word the user fixed by hand after dictating: what speech recognition produced, and what they
/// changed it to.
public struct Correction: Sendable, Equatable, Hashable {
    public let heard: String
    public let corrected: String

    public init(heard: String, corrected: String) {
        self.heard = heard
        self.corrected = corrected
    }
}

/// Learns vocabulary from the edits people make to dictated text, the way Wispr Flow and Typeless do.
///
/// Only small, sound-alike replacements are learned ("TypeList" → "Typeless", "逻辑鼠标" → "罗技鼠标").
/// Everything else is ignored on purpose: rewrites, pure insertions or deletions, changed numbers (facts,
/// not recognition errors), capitalising the first letter, and swapping one ordinary word for another.
/// A wrong entry in the vocabulary costs more than a missed one.
public enum CorrectionLearner {
    // MARK: Locating the dictated text

    /// The text that now sits where the dictated text was inserted. `before` is the field's content
    /// right after the paste, `after` its content now. Returns nil when that can't be told reliably: the
    /// dictated text isn't found, or the field changed outside it (sent, cleared, edited elsewhere).
    public static func editedRegion(inserted: String, before: String, after: String) -> String? {
        let inserted = normalizeNewlines(inserted), before = normalizeNewlines(before), after = normalizeNewlines(after)
        guard !inserted.isEmpty, let range = before.range(of: inserted, options: .backwards) else { return nil }
        let prefix = before[..<range.lowerBound], suffix = before[range.upperBound...]
        guard after.count >= prefix.count + suffix.count, after.hasPrefix(prefix), after.hasSuffix(suffix) else { return nil }
        return String(after.dropFirst(prefix.count).dropLast(suffix.count))
    }

    // MARK: Finding corrections

    /// Learnable corrections between the dictated text and the user's edited version of it.
    /// `isCommonWord` tells whether a single English word is an ordinary dictionary word.
    public static func corrections(original: String, edited: String,
                                   isCommonWord: (String) -> Bool = { _ in false }) -> [Correction] {
        guard original != edited else { return [] }
        // A heavy rewrite says nothing about recognition errors.
        guard PolishMetrics.similarity(original, edited) >= 0.5 else { return [] }
        let hunks = changes(original: original, edited: edited)
        guard hunks.count <= 5 else { return [] }
        var seen = Set<Correction>()
        return hunks.filter { isLearnable($0, isCommonWord: isCommonWord) && seen.insert($0).inserted }
    }

    /// Replacements between two texts, as the smallest runs of changed words (CJK: characters).
    static func changes(original: String, edited: String) -> [Correction] {
        let a = tokens(original), b = tokens(edited)
        // Longest common subsequence over token text.
        let n = a.count, m = b.count
        var lcs = [[Int]](repeating: [Int](repeating: 0, count: m + 1), count: n + 1)
        if n > 0, m > 0 {
            for i in stride(from: n - 1, through: 0, by: -1) {
                for j in stride(from: m - 1, through: 0, by: -1) {
                    lcs[i][j] = a[i].text == b[j].text ? lcs[i + 1][j + 1] + 1 : max(lcs[i + 1][j], lcs[i][j + 1])
                }
            }
        }
        var result: [Correction] = []
        var i = 0, j = 0
        var hunkA: [Token] = [], hunkB: [Token] = []
        func flush() {
            if !hunkA.isEmpty || !hunkB.isEmpty {
                result.append(Correction(heard: span(hunkA, in: original), corrected: span(hunkB, in: edited)))
            }
            hunkA = []; hunkB = []
        }
        while i < n || j < m {
            if i < n, j < m, a[i].text == b[j].text {
                flush(); i += 1; j += 1
            } else if j < m, i == n || lcs[i][j + 1] >= lcs[i + 1][j] {
                hunkB.append(b[j]); j += 1
            } else {
                hunkA.append(a[i]); i += 1
            }
        }
        flush()
        return result
    }

    static func isLearnable(_ c: Correction, isCommonWord: (String) -> Bool) -> Bool {
        let heard = c.heard.trimmingCharacters(in: .whitespacesAndNewlines)
        let term = c.corrected.trimmingCharacters(in: .whitespacesAndNewlines)
        // Pure insertions and deletions aren't recognition errors.
        guard !heard.isEmpty, !term.isEmpty, heard != term else { return false }
        guard term.count <= 40, latinWords(term).count <= 4 else { return false }
        let cjk = term.unicodeScalars.filter(TranscriptJoiner.isCJK).count
        let letters = term.unicodeScalars.filter { CharacterSet.letters.contains($0) }.count
        guard letters >= 2, cjk <= 8 else { return false }
        // One changed Chinese character is too little to be a word worth learning.
        if cjk == letters, cjk < 2 { return false }
        // Numbers are facts the speaker changed, not something recognition got wrong.
        guard heard.filter(\.isNumber) == term.filter(\.isNumber) else { return false }
        if heard.lowercased() == term.lowercased() {
            // Only distinctive casing is worth learning (SwiftUI, iOS, GitHub), not "hello" → "Hello".
            return hasInnerCapital(term)
        }
        // A different-sounding word is a change of mind, not a mishearing.
        guard phoneticDistance(heard, term) <= 0.5 else { return false }
        // Swapping one ordinary word for another is an edit, not a name or term.
        let heardWords = latinWords(heard), termWords = latinWords(term)
        if heardWords.count == 1, termWords.count == 1, cjk == 0, !hasInnerCapital(term),
           isCommonWord(heardWords[0]), isCommonWord(termWords[0]) {
            return false
        }
        return true
    }

    // MARK: Helpers

    struct Token {
        let text: String
        let range: Range<String.Index>
    }

    /// Latin words (with joiners like "." "-" "+" "#"), single CJK characters, and single punctuation marks.
    /// Whitespace separates tokens but isn't one, so "Envious Sales" → "EnviousSales" is one replacement.
    static func tokens(_ text: String) -> [Token] {
        var result: [Token] = []
        var index = text.startIndex
        while index < text.endIndex {
            let character = text[index]
            let next = text.index(after: index)
            if character.isWhitespace {
                index = next
            } else if isWordCharacter(character) {
                var end = next
                while end < text.endIndex {
                    let c = text[end]
                    if isWordCharacter(c) { end = text.index(after: end); continue }
                    let after = text.index(after: end)
                    // Joiners inside a word ("Node.js", "gpt-5") and trailing "+"/"#" ("C++", "C#").
                    if "._-'".contains(c), after < text.endIndex, isWordCharacter(text[after]) { end = after; continue }
                    if "+#".contains(c) { end = after; continue }
                    break
                }
                result.append(Token(text: String(text[index..<end]), range: index..<end))
                index = end
            } else {
                result.append(Token(text: String(character), range: index..<next))
                index = next
            }
        }
        return result
    }

    private static func isWordCharacter(_ c: Character) -> Bool {
        guard let scalar = c.unicodeScalars.first, !TranscriptJoiner.isCJK(scalar) else { return false }
        return c.isLetter || c.isNumber
    }

    private static func span(_ tokens: [Token], in text: String) -> String {
        guard let first = tokens.first, let last = tokens.last else { return "" }
        return String(text[first.range.lowerBound..<last.range.upperBound])
    }

    static func latinWords(_ text: String) -> [String] {
        text.split { !($0.isASCII && ($0.isLetter || $0.isNumber)) }.map(String.init).filter { $0.first?.isLetter == true }
    }

    private static func hasInnerCapital(_ text: String) -> Bool {
        text.split(whereSeparator: { $0.isWhitespace }).contains { word in word.dropFirst().contains(where: \.isUppercase) }
    }

    /// How differently two spellings sound, 0 (same) … 1: Chinese is compared by pinyin, so homophones
    /// like 逻辑 / 罗技 count as identical, and "swift you eye" is close to "SwiftUI".
    static func phoneticDistance(_ a: String, _ b: String) -> Double {
        let x = Array(phoneticKey(a)), y = Array(phoneticKey(b))
        guard !x.isEmpty, !y.isEmpty else { return 1 }
        var previous = Array(0...y.count)
        for i in 1...x.count {
            var current = [i] + [Int](repeating: 0, count: y.count)
            for j in 1...y.count {
                current[j] = x[i - 1] == y[j - 1] ? previous[j - 1] : 1 + min(previous[j - 1], previous[j], current[j - 1])
            }
            previous = current
        }
        return Double(previous[y.count]) / Double(max(x.count, y.count))
    }

    static func phoneticKey(_ text: String) -> String {
        let latin = text.applyingTransform(.toLatin, reverse: false) ?? text
        let plain = latin.applyingTransform(.stripDiacritics, reverse: false) ?? latin
        return String(plain.lowercased().unicodeScalars.filter { $0.isASCII && CharacterSet.alphanumerics.contains($0) }
            .map(Character.init))
    }

    private static func normalizeNewlines(_ text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
    }
}
