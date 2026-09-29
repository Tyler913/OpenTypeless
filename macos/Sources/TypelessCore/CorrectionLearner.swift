import Foundation
import NaturalLanguage

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
/// Only small, sound-alike replacements are learned ("TypeList" → "Typeless", "逻辑鼠标" → "罗技鼠标", "克劳德" →
/// "Claude"); a fix of one Chinese character learns the word it belongs to ("罗级鼠标" → "罗技鼠标" learns 罗技).
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

    /// One replacement between the dictated text and the edited one, and why it isn't learned (nil when it is).
    public struct Change: Sendable, Equatable {
        public let correction: Correction
        public let rejected: String?
    }

    /// What the learner made of an edit: why it was skipped as a whole, or each replacement and its verdict.
    public struct Review: Sendable, Equatable {
        public var skipped: String?
        public var changes: [Change]

        public var learnable: [Correction] {
            var seen = Set<Correction>()
            return changes.filter { $0.rejected == nil }.map(\.correction).filter { seen.insert($0).inserted }
        }
    }

    /// Learnable corrections between the dictated text and the user's edited version of it.
    /// `isCommonWord` tells whether a single English word is an ordinary dictionary word; `words` splits Chinese
    /// text into words (see `review`).
    public static func corrections(original: String, edited: String,
                                   isCommonWord: (String) -> Bool = { _ in false },
                                   words: (String) -> [Range<String.Index>] = chineseWords) -> [Correction] {
        review(original: original, edited: edited, isCommonWord: isCommonWord, words: words).learnable
    }

    /// Every replacement between the two texts with its verdict. A fix inside Chinese text is widened to the whole
    /// word it falls in (per `words`, run on the edited text), so correcting one wrong character of 罗级鼠标 learns
    /// 罗技, heard as 罗级.
    public static func review(original: String, edited: String,
                              isCommonWord: (String) -> Bool = { _ in false },
                              words: (String) -> [Range<String.Index>] = chineseWords) -> Review {
        guard original != edited else { return Review(skipped: "unchanged", changes: []) }
        // A heavy rewrite says nothing about recognition errors.
        guard PolishMetrics.similarity(original, edited) >= 0.5 else { return Review(skipped: "rewrite", changes: []) }
        let hunks = changes(original: original, edited: edited, words: words(edited))
        guard hunks.count <= 5 else { return Review(skipped: "\(hunks.count) changes", changes: []) }
        return Review(skipped: nil, changes: hunks.map { Change(correction: $0, rejected: rejection($0, isCommonWord: isCommonWord)) })
    }

    /// Replacements between two texts, as the smallest runs of changed words (CJK: characters), each widened to
    /// whole Chinese words when the edited text's `words` are given.
    static func changes(original: String, edited: String, words wordRanges: [Range<String.Index>] = []) -> [Correction] {
        let a = tokens(original), b = tokens(edited)
        // Longest common subsequence over token text. The tokens both texts start and end with are left out of the
        // table: after a small fix to a long dictation they are nearly all of it, and this runs on the main thread.
        let n = a.count, m = b.count
        var start = 0
        while start < n, start < m, a[start].text == b[start].text { start += 1 }
        var end = 0
        while end < n - start, end < m - start, a[n - 1 - end].text == b[m - 1 - end].text { end += 1 }
        let endA = n - end, endB = m - end
        var lcs = [[Int]](repeating: [Int](repeating: 0, count: endB - start + 1), count: endA - start + 1)
        for i in stride(from: endA - start - 1, through: 0, by: -1) {
            for j in stride(from: endB - start - 1, through: 0, by: -1) {
                lcs[i][j] = a[start + i].text == b[start + j].text ? lcs[i + 1][j + 1] + 1 : max(lcs[i + 1][j], lcs[i][j + 1])
            }
        }
        // Whether the walk below skips an edited token rather than an original one, as a table over every token would
        // decide. Inside the table it is the usual comparison (the shared end adds the same to both sides); past its
        // edge only the shared end is left, and the full table skips on the side that is less far into it.
        func skipEdited(_ i: Int, _ j: Int) -> Bool {
            i >= endA || j >= endB
                ? i - endA > j - endB
                : lcs[i - start][j + 1 - start] >= lcs[i + 1 - start][j - start]
        }
        // Hunks as token index ranges, and which original token each unchanged edited token matches.
        var hunks: [(a: Range<Int>, b: Range<Int>)] = []
        var matchOf = [Int?](repeating: nil, count: m)
        var i = 0, j = 0, startA = 0, startB = 0
        func flush() {
            if i > startA || j > startB { hunks.append((startA..<i, startB..<j)) }
        }
        while i < n || j < m {
            if i < n, j < m, a[i].text == b[j].text {
                flush()
                matchOf[j] = i
                i += 1; j += 1
                startA = i; startB = j
            } else if j < m, skipEdited(i, j) {
                j += 1
            } else {
                i += 1
            }
        }
        flush()
        return hunks.map { hunk in
            var hunk = hunk
            if !wordRanges.isEmpty, !hunk.a.isEmpty, !hunk.b.isEmpty {
                let (left, right) = widening(b, hunk.b, in: edited, words: wordRanges)
                // Only across unchanged tokens that line up on both sides.
                var l = 0
                while l < left, hunk.a.lowerBound - l - 1 >= 0, matchOf[hunk.b.lowerBound - l - 1] == hunk.a.lowerBound - l - 1 { l += 1 }
                var r = 0
                while r < right, hunk.a.upperBound + r < n, matchOf[hunk.b.upperBound + r] == hunk.a.upperBound + r { r += 1 }
                hunk = ((hunk.a.lowerBound - l)..<(hunk.a.upperBound + r), (hunk.b.lowerBound - l)..<(hunk.b.upperBound + r))
            }
            return Correction(heard: span(Array(a[hunk.a]), in: original), corrected: span(Array(b[hunk.b]), in: edited))
        }
    }

    /// Particles and the like: a fix of one of these is grammar, never part of a word worth learning.
    private static let particles = Set("的地得了着过吗呢吧啊呀嘛么哦哈")
    /// Widening a fix never makes a Chinese word longer than this.
    private static let maxWidenedCharacters = 4

    /// How many tokens to add on the left and right of a hunk of the edited text so it covers whole words. A fix
    /// that is still a single character after that (the word segmenter splits names it doesn't know, like 千|问 or
    /// 飞|书) takes in the neighbouring single-character words too.
    private static func widening(_ tokens: [Token], _ hunk: Range<Int>, in text: String,
                                 words: [Range<String.Index>]) -> (Int, Int) {
        let changed = tokens[hunk].map(\.text).joined()
        guard changed.unicodeScalars.contains(where: TranscriptJoiner.isCJK),
              !(changed.count == 1 && particles.contains(Character(changed))) else { return (0, 0) }
        let lower = tokens[hunk.lowerBound].range.lowerBound, upper = tokens[hunk.upperBound - 1].range.upperBound
        guard var first = words.firstIndex(where: { $0.upperBound > lower }),
              var last = words.lastIndex(where: { $0.lowerBound < upper }), first <= last else { return (0, 0) }
        func isSingle(_ index: Int) -> Bool {
            let word = text[words[index]]
            return word.count == 1 && word.unicodeScalars.allSatisfy(TranscriptJoiner.isCJK) && !particles.contains(word.first!)
        }
        func length() -> Int { text[words[first].lowerBound..<words[last].upperBound].count }
        if length() == 1 {
            while first > 0, isSingle(first - 1), words[first - 1].upperBound == words[first].lowerBound,
                  length() < maxWidenedCharacters { first -= 1 }
            while last + 1 < words.count, isSingle(last + 1), words[last].upperBound == words[last + 1].lowerBound,
                  length() < maxWidenedCharacters { last += 1 }
        }
        guard length() <= maxWidenedCharacters else { return (0, 0) }
        let from = min(lower, words[first].lowerBound), to = max(upper, words[last].upperBound)
        // Only whole tokens: a word boundary inside a Latin token widens nothing on that side.
        let left = tokens[..<hunk.lowerBound].reversed().prefix { $0.range.lowerBound >= from }.count
        let right = tokens[hunk.upperBound...].prefix { $0.range.upperBound <= to }.count
        return (left, right)
    }

    /// Chinese words, per the system's word segmenter.
    public static func chineseWords(_ text: String) -> [Range<String.Index>] {
        let tokenizer = NLTokenizer(unit: .word)
        tokenizer.string = text
        tokenizer.setLanguage(.simplifiedChinese)
        return tokenizer.tokens(for: text.startIndex..<text.endIndex)
    }

    static func isLearnable(_ c: Correction, isCommonWord: (String) -> Bool) -> Bool {
        rejection(c, isCommonWord: isCommonWord) == nil
    }

    /// Why a replacement isn't learned, or nil when it is.
    static func rejection(_ c: Correction, isCommonWord: (String) -> Bool) -> String? {
        let heard = c.heard.trimmingCharacters(in: .whitespacesAndNewlines)
        let term = c.corrected.trimmingCharacters(in: .whitespacesAndNewlines)
        // Pure insertions and deletions aren't recognition errors.
        guard !heard.isEmpty, !term.isEmpty, heard != term else { return "insertion or deletion" }
        guard term.count <= 40, latinWords(term).count <= 4 else { return "too long" }
        let cjk = term.unicodeScalars.filter(TranscriptJoiner.isCJK).count
        let letters = term.unicodeScalars.filter { CharacterSet.letters.contains($0) }.count
        guard letters >= 2, cjk <= 8 else { return letters < 2 ? "too short" : "too long" }
        // One Chinese character is too little to be a word worth learning.
        if cjk == letters, cjk < 2 { return "single character" }
        // Numbers are facts the speaker changed, not something recognition got wrong.
        guard heard.filter(\.isNumber) == term.filter(\.isNumber) else { return "numbers changed" }
        if heard.lowercased() == term.lowercased() {
            // Only distinctive casing is worth learning (SwiftUI, iOS, GitHub), not "hello" → "Hello".
            return hasInnerCapital(term) ? nil : "only capitalisation"
        }
        // A different-sounding word is a change of mind, not a mishearing.
        guard soundsAlike(heard, term) else { return "sounds different" }
        // Swapping one ordinary word for another is an edit, not a name or term.
        let heardWords = latinWords(heard), termWords = latinWords(term)
        if heardWords.count == 1, termWords.count == 1, cjk == 0, !hasInnerCapital(term),
           isCommonWord(heardWords[0]), isCommonWord(termWords[0]) {
            return "ordinary words"
        }
        return nil
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
        return Double(editDistance(x, y)) / Double(max(x.count, y.count))
    }

    /// Close enough in spelling (`phoneticDistance`), or in sound: English words that are spelt differently but
    /// sound alike ("Versel" / "Vercel"), and English terms heard as Chinese (克劳德 / Claude, 杰森 / JSON).
    static func soundsAlike(_ a: String, _ b: String) -> Bool {
        if phoneticDistance(a, b) <= 0.5 { return true }
        let x = Array(soundKey(a)), y = Array(soundKey(b))
        guard x.count >= 2, y.count >= 2 else { return false }
        return Double(editDistance(x, y)) / Double(max(x.count, y.count)) <= 1.0 / 3
    }

    /// How a text sounds: each Latin word (Chinese as pinyin) reduced to its consonant sounds, Metaphone-style.
    static func soundKey(_ text: String) -> String {
        let latin = text.applyingTransform(.toLatin, reverse: false) ?? text
        let plain = (latin.applyingTransform(.stripDiacritics, reverse: false) ?? latin).lowercased()
        return plain.split { !($0.isASCII && ($0.isLetter || $0.isNumber)) }.map { metaphone(Array($0)) }.joined()
    }

    /// A simplified Metaphone: consonant sounds of one lowercase word (vowels only at the start, as "A").
    static func metaphone(_ word: [Character]) -> String {
        var w = word
        if w.count >= 2, ["kn", "gn", "pn", "wr", "ae"].contains(String(w[0...1])) { w.removeFirst() }
        if w.first == "x" { w[0] = "s" }
        if w.count >= 2, w[0] == "w", w[1] == "h" { w.remove(at: 1) }
        func at(_ i: Int) -> Character? { i >= 0 && i < w.count ? w[i] : nil }
        func isVowel(_ c: Character?) -> Bool { c.map { "aeiou".contains($0) } ?? false }
        var key = ""
        for (i, c) in w.enumerated() {
            let prev = at(i - 1), next = at(i + 1), after = at(i + 2)
            if c == prev, c != "c" { continue }
            switch c {
            case "a", "e", "i", "o", "u": if i == 0 { key += "A" }
            case "b": if !(prev == "m" && next == nil) { key += "B" }
            case "c":
                if next == "h" { key += "X" }
                else if next == "i" && after == "a" { key += "X" }
                else if let next, "iey".contains(next) { if prev != "s" { key += "S" } }
                else { key += "K" }
            case "d": key += next == "g" && after.map { "iey".contains($0) } == true ? "J" : "T"
            case "g":
                if next == "h" && !isVowel(after) { continue }
                if next == "n" && after == nil { continue }
                if prev == "d", let next, "iey".contains(next) { continue }
                key += next.map { "iey".contains($0) } == true ? "J" : "K"
            case "h": if isVowel(next), !(prev.map { "csptg".contains($0) } ?? false) { key += "H" }
            case "k": if prev != "c" { key += "K" }
            case "p": key += next == "h" ? "F" : "P"
            case "q": key += "K"
            case "s": key += next == "h" || (next == "i" && (after == "o" || after == "a")) ? "X" : "S"
            case "t":
                if next == "i" && (after == "o" || after == "a") { key += "X" }
                else if next == "h" { key += "0" }
                else if !(next == "c" && after == "h") { key += "T" }
            case "v": key += "F"
            case "w", "y": if isVowel(next) { key += c.uppercased() }
            case "x": key += "KS"
            case "z": key += "S"
            default: key += c.isLetter ? c.uppercased() : String(c)
            }
        }
        return key
    }

    private static func editDistance(_ x: [Character], _ y: [Character]) -> Int {
        guard !x.isEmpty, !y.isEmpty else { return max(x.count, y.count) }
        var previous = Array(0...y.count)
        for i in 1...x.count {
            var current = [i] + [Int](repeating: 0, count: y.count)
            for j in 1...y.count {
                current[j] = x[i - 1] == y[j - 1] ? previous[j - 1] : 1 + min(previous[j - 1], previous[j], current[j - 1])
            }
            previous = current
        }
        return previous[y.count]
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
