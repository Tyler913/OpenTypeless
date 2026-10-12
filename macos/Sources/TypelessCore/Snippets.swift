import Foundation

/// A spoken shortcut: say `trigger` in a dictation and `expansion` goes in in its place, word for word.
public struct Snippet: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var trigger: String
    public var expansion: String

    public init(id: String = UUID().uuidString, trigger: String, expansion: String) {
        self.id = id
        self.trigger = trigger
        self.expansion = expansion
    }

    private enum CodingKeys: String, CodingKey { case id, trigger, expansion }

    /// A missing field is tolerated, as on Windows, so one odd entry doesn't lose the whole list.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decodeIfPresent(String.self, forKey: .id) ?? UUID().uuidString
        trigger = try container.decodeIfPresent(String.self, forKey: .trigger) ?? ""
        expansion = try container.decodeIfPresent(String.self, forKey: .expansion) ?? ""
    }
}

/// Puts snippets' expansions in place of their triggers in a dictation's final text.
///
/// It's done in code once the text is final (cleaned up, or as transcribed when clean-up was off or fell back), never by
/// the clean-up model: a model paraphrases and translates a long expansion, and the clean-up's guards would take one for
/// an answer. The triggers reach speech-to-text and the clean-up as vocabulary instead (see `vocabulary`), so they come
/// through as written. Like OpenWhispr's snippets (longest trigger first) and Superwhisper's replacements (code, no AI),
/// with these differences:
/// - a trigger is found however the clean-up wrote it: in any case, in full-width or half-width forms, with spaces put in
///   between Chinese and English, or a line break for a space;
/// - word boundaries only count at a Latin edge. Chinese and Japanese have no spaces between words, so "我的邮箱" is found
///   in "发到我的邮箱吧", which a `\b` regex misses, while "email" is not found in "emails";
/// - the text is searched once, so an expansion that went in is never searched for other triggers.
public enum SnippetExpander {
    /// Wispr Flow's limits: a trigger is a short phrase, an expansion up to a long template.
    public static let maxTriggerLength = 60
    public static let maxExpansionLength = 4000

    /// `text` with every trigger in it replaced by its snippet's expansion. Where triggers overlap, the longest wins.
    /// A dictation that is nothing but one trigger, with the punctuation the clean-up gave it, becomes exactly the
    /// expansion. Snippets with no trigger or a blank expansion are left out.
    public static func expand(_ text: String, with snippets: [Snippet]) -> String {
        let patterns = snippets.enumerated().compactMap { index, snippet -> Pattern? in
            let units = units(of: snippet.trigger)
            guard !units.isEmpty, !snippet.expansion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            return Pattern(units: units, expansion: snippet.expansion, order: index)
        }
        guard !patterns.isEmpty, !text.isEmpty else { return text }
        let scalars = Array(text.unicodeScalars)
        let units = normalize(scalars)

        // Where each character is, so a trigger is only tried where its first character is.
        var positions: [UInt32: [Int]] = [:]
        for (index, unit) in units.enumerated() { positions[unit.scalar, default: []].append(index) }
        // Longest first: a longer trigger is the more specific one ("my address in English" over "my address").
        var claimed = [Bool](repeating: false, count: units.count)
        var matches: [Match] = []
        for pattern in patterns.sorted(by: { ($0.units.count, -$0.order) > ($1.units.count, -$1.order) }) {
            let n = pattern.units.count
            var next = 0
            for i in positions[pattern.units[0]] ?? [] where i >= next && i + n <= units.count {
                guard found(pattern, at: i, in: units), !claimed[i..<i + n].contains(true) else { continue }
                for k in i..<i + n { claimed[k] = true }
                matches.append(Match(start: units[i].start, end: units[i + n - 1].end, expansion: pattern.expansion))
                next = i + n
            }
        }
        guard !matches.isEmpty else { return text }
        matches.sort { $0.start < $1.start }

        // The dictation was the trigger alone: the full stop the clean-up gave it isn't part of the snippet.
        if matches.count == 1, (scalars[..<matches[0].start] + scalars[matches[0].end...]).allSatisfy(isSpaceOrClausePunctuation) {
            return matches[0].expansion
        }

        var output = String.UnicodeScalarView()
        var cursor = 0
        var expanded = false
        for (index, match) in matches.enumerated() {
            if cursor < match.start { append(scalars[cursor..<match.start], to: &output, spaced: expanded) }
            append(match.expansion.unicodeScalars, to: &output, spaced: true)
            cursor = match.end
            expanded = true
            // An expansion that ends a sentence or is a block of lines already ends there: the clean-up's punctuation after
            // the trigger would double it ("…回复你。。").
            let expansion = match.expansion.unicodeScalars
            if let last = expansion.last, isClausePunctuation(fold(last)) || expansion.contains(where: { $0 == "\n" || $0 == "\r" }) {
                let limit = index + 1 < matches.count ? matches[index + 1].start : scalars.count
                while cursor < limit, isClausePunctuation(fold(scalars[cursor])) { cursor += 1 }
            }
        }
        if cursor < scalars.count { append(scalars[cursor...], to: &output, spaced: expanded) }
        return String(output)
    }

    /// The snippets' triggers, then the vocabulary terms that aren't among them, for speech-to-text's spelling hints and the
    /// clean-up's vocabulary: a trigger has to come through both as written for its snippet to go in. Triggers go first,
    /// since hints stop after the first terms (see VocabularyHints). Without snippets, `terms` as they are.
    public static func vocabulary(_ snippets: [Snippet], before terms: [String]) -> [String] {
        guard !snippets.isEmpty else { return terms }
        var seen = Set<String>()
        var result: [String] = []
        for term in snippets.map(\.trigger) + terms {
            let trimmed = term.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty, seen.insert(trimmed.lowercased()).inserted else { continue }
            result.append(trimmed)
        }
        return result
    }

    /// A trigger as it's compared, for telling whether two triggers are the same one ("My email." and "my email" are).
    /// Empty when there's nothing to find.
    public static func key(_ trigger: String) -> String {
        String(String.UnicodeScalarView(units(of: trigger).compactMap(Unicode.Scalar.init)))
    }

    // MARK: Matching

    private struct Pattern {
        let units: [UInt32]
        let expansion: String
        let order: Int
    }

    /// Where a trigger was found, in the text's scalars.
    private struct Match {
        let start: Int
        let end: Int
        let expansion: String
    }

    /// One character of the text as compared, with the scalars of the text it stands for.
    private struct Unit {
        let scalar: UInt32
        let start: Int
        let end: Int
    }

    private static func found(_ pattern: Pattern, at i: Int, in units: [Unit]) -> Bool {
        let n = pattern.units.count
        for k in 0..<n where units[i + k].scalar != pattern.units[k] { return false }
        // A trigger that starts or ends with a Latin letter or digit is a whole word there. Next to Chinese, Japanese or
        // Korean (whose particles attach to the word before) anything goes.
        if isLatinWord(pattern.units[0]), i > 0, isLatinWord(units[i - 1].scalar) { return false }
        if isLatinWord(pattern.units[n - 1]), i + n < units.count, isLatinWord(units[i + n].scalar) { return false }
        return true
    }

    /// The text as compared: folded (see `fold`), each run of spaces or line breaks as one space, and none at all next to
    /// Chinese or Japanese, where the clean-up puts them around English words ("我的GitHub" is "我的 GitHub").
    private static func normalize(_ scalars: [Unicode.Scalar]) -> [Unit] {
        var units: [Unit] = []
        units.reserveCapacity(scalars.count)
        var i = 0
        while i < scalars.count {
            guard scalars[i].properties.isWhitespace else {
                units.append(Unit(scalar: fold(scalars[i]), start: i, end: i + 1))
                i += 1
                continue
            }
            var j = i + 1
            while j < scalars.count, scalars[j].properties.isWhitespace { j += 1 }
            let spaceless = (i > 0 && joinsWithoutSpace(fold(scalars[i - 1])))
                || (j < scalars.count && joinsWithoutSpace(fold(scalars[j])))
            if !spaceless { units.append(Unit(scalar: 0x20, start: i, end: j)) }
            i = j
        }
        return units
    }

    /// A trigger as compared: like the text, without the spaces around it or the punctuation typed after it.
    private static func units(of trigger: String) -> [UInt32] {
        var units = normalize(Array(trigger.unicodeScalars)).map(\.scalar)
        while let last = units.last, last == 0x20 || isClausePunctuation(last) { units.removeLast() }
        while units.first == 0x20 { units.removeFirst() }
        return units
    }

    /// Full-width ASCII as ASCII ("ＧＰＴ", "，"), then lowercase.
    private static func fold(_ scalar: Unicode.Scalar) -> UInt32 {
        var value = scalar.value
        if (0xFF01...0xFF5E).contains(value) { value -= 0xFEE0 }
        guard let folded = Unicode.Scalar(value) else { return value }
        let lower = folded.properties.lowercaseMapping.unicodeScalars
        return lower.count == 1 ? lower.first!.value : value
    }

    // MARK: Joining

    /// Appends `piece`, with a space first where it meets an expansion and the two would otherwise run together.
    private static func append<S: Sequence>(_ piece: S, to output: inout String.UnicodeScalarView, spaced: Bool)
        where S.Element == Unicode.Scalar {
        var iterator = piece.makeIterator()
        guard let first = iterator.next() else { return }
        if spaced, let last = output.last, needsSpace(last, first) { output.append(" ") }
        output.append(first)
        while let next = iterator.next() { output.append(next) }
    }

    /// Two Latin words, or Chinese or Japanese next to a Latin word, spaced the way the clean-up writes them ("发到
    /// name@example.com 吧"). Korean particles attach to the word before, so nothing goes in next to Hangul.
    private static func needsSpace(_ a: Unicode.Scalar, _ b: Unicode.Scalar) -> Bool {
        let latinA = isLatinWord(fold(a)), latinB = isLatinWord(fold(b))
        let hanA = WordCount.isCJKWordCharacter(a.value), hanB = WordCount.isCJKWordCharacter(b.value)
        return latinA && (latinB || hanB) || hanA && latinB
    }

    // MARK: Characters

    /// A letter, digit or mark outside Chinese, Japanese and Korean.
    private static func isLatinWord(_ value: UInt32) -> Bool {
        guard let scalar = Unicode.Scalar(value) else { return false }
        return WordCount.isWordCharacter(scalar) && !TranscriptJoiner.isCJK(scalar)
    }

    private static func joinsWithoutSpace(_ value: UInt32) -> Bool {
        Unicode.Scalar(value).map(TranscriptJoiner.joinsWithoutSpace) ?? false
    }

    /// What ends a clause or a sentence, as folded: full-width ，。！？；： are ASCII by then.
    private static func isClausePunctuation(_ value: UInt32) -> Bool {
        switch value {
        case 0x2E, 0x2C, 0x3B, 0x3A, 0x21, 0x3F, // . , ; : ! ?
             0x3002, 0x3001, 0x2026: // 。、…
            return true
        default:
            return false
        }
    }

    private static func isSpaceOrClausePunctuation(_ scalar: Unicode.Scalar) -> Bool {
        scalar.properties.isWhitespace || isClausePunctuation(fold(scalar))
    }
}
