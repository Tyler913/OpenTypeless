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

    /// An entry needs its trigger and expansion; one without an ID is given one by `list(from:)`.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decodeIfPresent(String.self, forKey: .id) ?? ""
        trigger = try container.decode(String.self, forKey: .trigger)
        expansion = try container.decode(String.self, forKey: .expansion)
    }

    /// A saved list, read entry by entry: one that can't be read (null, or without its text) is left out rather than
    /// losing the others. Snippets are told apart by ID, so one with none, or with another's, gets its own; `repaired`
    /// says the list should be saved again so the new IDs last.
    public static func list(from data: Data) -> (snippets: [Snippet], repaired: Bool) {
        let entries = (try? JSONDecoder().decode([Entry].self, from: data))?.compactMap(\.snippet) ?? []
        var seen = Set<String>()
        var repaired = false
        let snippets = entries.map { entry -> Snippet in
            var snippet = entry
            if snippet.id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !seen.insert(snippet.id).inserted {
                snippet.id = UUID().uuidString
                seen.insert(snippet.id)
                repaired = true
            }
            return snippet
        }
        return (snippets, repaired)
    }

    /// One entry of a saved list, or nothing when it can't be read.
    private struct Entry: Decodable {
        let snippet: Snippet?

        init(from decoder: Decoder) throws {
            snippet = try? Snippet(from: decoder)
        }
    }
}

/// Puts snippets' expansions in place of their triggers in a dictation's final text.
///
/// It's done in code once the text is final (cleaned up, or as transcribed when clean-up was off or fell back), never by
/// the clean-up model: a model paraphrases and translates a long expansion, and the clean-up's guards would take one for
/// an answer. The triggers reach speech-to-text and the clean-up as vocabulary instead (see `vocabulary`), so they come
/// through as written. Like OpenWhispr's snippets (longest trigger first) and Superwhisper's replacements (code, no AI),
/// with these differences:
/// - a trigger is found however it was written: in any case, in full-width or half-width forms, composed or decomposed
///   ("é" or e + ◌́, a Hangul syllable or its jamo), with spaces put in between Chinese and English, or a line break for a
///   space. Text is compared a whole character (grapheme) at a time, so a trigger never takes part of one: "か" isn't
///   found in "が" typed as か + ◌゙;
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
            let keys = keys(of: snippet.trigger)
            guard !keys.isEmpty, !snippet.expansion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            return Pattern(keys: keys, expansion: Piece(snippet.expansion), order: index)
        }
        guard !patterns.isEmpty, !text.isEmpty else { return text }
        let characters = Array(text)
        let clusters = characters.map(Cluster.init)
        let units = normalize(clusters)

        // Where each character is, so a trigger is only tried where its first character is.
        var positions: [String: [Int]] = [:]
        for (index, unit) in units.enumerated() { positions[unit.key, default: []].append(index) }
        // Longest first: a longer trigger is the more specific one ("my address in English" over "my address").
        var claimed = [Bool](repeating: false, count: units.count)
        var matches: [Match] = []
        for pattern in patterns.sorted(by: { ($0.keys.count, -$0.order) > ($1.keys.count, -$1.order) }) {
            let n = pattern.keys.count
            var next = 0
            for i in positions[pattern.keys[0]] ?? [] where i >= next && i + n <= units.count {
                guard found(pattern, at: i, in: units), !claimed[i..<i + n].contains(true) else { continue }
                for k in i..<i + n { claimed[k] = true }
                matches.append(Match(start: units[i].start, end: units[i + n - 1].end, expansion: pattern.expansion))
                next = i + n
            }
        }
        guard !matches.isEmpty else { return text }
        matches.sort { $0.start < $1.start }

        // The dictation was the trigger alone: the full stop the clean-up gave it isn't part of the snippet.
        if matches.count == 1,
           (clusters[..<matches[0].start] + clusters[matches[0].end...]).allSatisfy({ $0.isSpace || isClausePunctuation($0.base) }) {
            return matches[0].expansion.text
        }

        var output = ""
        // The last character put out, as compared.
        var last: UInt32?
        // Appends a piece, with a space first where it meets an expansion and the two would otherwise run together.
        func append(_ piece: String, first: UInt32, final: UInt32, spaced: Bool) {
            if spaced, let last, needsSpace(last, first) { output += " " }
            output += piece
            last = final
        }
        func appendText(from start: Int, to end: Int, spaced: Bool) {
            guard start < end else { return }
            append(String(characters[start..<end]), first: clusters[start].base, final: clusters[end - 1].base, spaced: spaced)
        }

        var cursor = 0
        var expanded = false
        for (index, match) in matches.enumerated() {
            appendText(from: cursor, to: match.start, spaced: expanded)
            let expansion = match.expansion
            append(expansion.text, first: expansion.first, final: expansion.last, spaced: true)
            cursor = match.end
            expanded = true
            // An expansion that ends a sentence or is a block of lines already ends there: the clean-up's punctuation after
            // the trigger, with any spaces before it ("réponse !"), would double it ("…回复你。。"). Not across a line break.
            if expansion.endsBlock {
                let limit = index + 1 < matches.count ? matches[index + 1].start : clusters.count
                var end = cursor
                while end < limit, clusters[end].isSpace, !clusters[end].isLineBreak { end += 1 }
                let spaces = end
                while end < limit, !clusters[end].isSpace, isClausePunctuation(clusters[end].base) { end += 1 }
                if end > spaces { cursor = end }
            }
        }
        appendText(from: cursor, to: clusters.count, spaced: expanded)
        return output
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

    /// A trigger as it's compared, for telling whether two triggers are the same one ("My email." and "my email" are, and
    /// so are "café" composed and decomposed). Empty when there's nothing to find. It's in NFC, so comparing two keys as
    /// Swift strings (canonical equivalence) gives what Windows' ordinal comparison does.
    public static func key(_ trigger: String) -> String {
        keys(of: trigger).joined()
    }

    // MARK: Matching

    private struct Pattern {
        let keys: [String]
        let expansion: Piece
        let order: Int
    }

    /// Where a trigger was found, in the text's characters.
    private struct Match {
        let start: Int
        let end: Int
        let expansion: Piece
    }

    /// One character of the text as compared (a run of spaces counts as one), with the characters it stands for.
    private struct Unit {
        let key: String
        let base: UInt32
        let start: Int
        let end: Int
    }

    /// One character (grapheme cluster): its canonical composition, folded (see `fold`), and what kind it is.
    private struct Cluster {
        let key: String
        /// Its first scalar as compared, which says what script it's in.
        let base: UInt32
        let isSpace: Bool
        let isLineBreak: Bool

        init(_ character: Character) {
            let scalars = character.unicodeScalars
            isSpace = scalars.allSatisfy { $0.properties.isWhitespace }
            isLineBreak = scalars.contains { SnippetExpander.isLineBreak($0.value) }
            var folded = String.UnicodeScalarView()
            for scalar in SnippetExpander.composed(character) {
                folded.append(Unicode.Scalar(SnippetExpander.fold(scalar)) ?? scalar)
            }
            key = String(folded)
            base = folded.first?.value ?? 0
        }
    }

    /// An expansion, with what it starts and ends with, for joining it to the text around it.
    private struct Piece {
        let text: String
        let first: UInt32
        let last: UInt32
        /// It ends a sentence, or it's a block of lines.
        let endsBlock: Bool

        init(_ text: String) {
            let clusters = text.map(Cluster.init)
            self.text = text
            first = clusters.first?.base ?? 0x20
            last = clusters.last?.base ?? 0x20
            endsBlock = clusters.last.map { SnippetExpander.isClausePunctuation($0.base) } == true || clusters.contains(where: \.isLineBreak)
        }
    }

    private static func found(_ pattern: Pattern, at i: Int, in units: [Unit]) -> Bool {
        let n = pattern.keys.count
        for k in 0..<n where units[i + k].key != pattern.keys[k] { return false }
        // A trigger that starts or ends with a Latin letter or digit is a whole word there. Next to Chinese, Japanese or
        // Korean (whose particles attach to the word before) anything goes.
        if isLatinWord(units[i].base), i > 0, isLatinWord(units[i - 1].base) { return false }
        if isLatinWord(units[i + n - 1].base), i + n < units.count, isLatinWord(units[i + n].base) { return false }
        return true
    }

    /// The text as compared: each run of spaces or line breaks as one space, and none at all next to Chinese or Japanese,
    /// where the clean-up puts them around English words ("我的GitHub" is "我的 GitHub").
    private static func normalize(_ clusters: [Cluster]) -> [Unit] {
        var units: [Unit] = []
        units.reserveCapacity(clusters.count)
        var i = 0
        while i < clusters.count {
            guard clusters[i].isSpace else {
                units.append(Unit(key: clusters[i].key, base: clusters[i].base, start: i, end: i + 1))
                i += 1
                continue
            }
            var j = i + 1
            while j < clusters.count, clusters[j].isSpace { j += 1 }
            let spaceless = (i > 0 && isSpaceless(clusters[i - 1].base)) || (j < clusters.count && isSpaceless(clusters[j].base))
            if !spaceless { units.append(Unit(key: " ", base: 0x20, start: i, end: j)) }
            i = j
        }
        return units
    }

    /// A trigger as compared: like the text, without the spaces around it or the punctuation typed after it.
    private static func keys(of trigger: String) -> [String] {
        var units = normalize(trigger.map(Cluster.init))
        while let last = units.last, last.key == " " || isClausePunctuation(last.base) { units.removeLast() }
        while units.first?.key == " " { units.removeFirst() }
        return units.map(\.key)
    }

    /// The character's canonical composition (NFC), so "é" typed as e + ◌́ compares like é, and a Hangul syllable like its
    /// jamo. Most characters are one scalar NFC leaves alone, which skips the conversion.
    private static func composed(_ character: Character) -> [Unicode.Scalar] {
        let scalars = character.unicodeScalars
        if scalars.count == 1, let only = scalars.first {
            switch only.value {
            case 0..<0x300, 0x3040...0x30FF, 0x4E00...0x9FFF, 0xAC00...0xD7A3: return [only]
            default: break
            }
        }
        return Array(String(character).precomposedStringWithCanonicalMapping.unicodeScalars)
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

    /// Two Latin words, or Chinese or Japanese next to a Latin word, spaced the way the clean-up writes them ("发到
    /// name@example.com 吧"). Korean particles attach to the word before, so nothing goes in next to Hangul.
    private static func needsSpace(_ a: UInt32, _ b: UInt32) -> Bool {
        isLatinWord(a) && (isLatinWord(b) || isHanOrKana(b)) || isHanOrKana(a) && isLatinWord(b)
    }

    // MARK: Characters
    //
    // One classifier for word boundaries, spaces and joining, covering every block of each script (TranscriptJoiner's
    // ranges, which join transcripts, leave out the supplementary planes).

    /// Han in every block (the supplementary planes too, e.g. 𠮷) and kana, where words aren't separated by spaces.
    static func isHanOrKana(_ value: UInt32) -> Bool {
        switch value {
        case 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, 0x20000...0x3FFFF, // Han
             0x3040...0x30FF, 0x31F0...0x31FF, 0x1B000...0x1B16F, 0xFF66...0xFF9F: // kana
            return true
        default:
            return false
        }
    }

    /// Hangul syllables and jamo, half-width ones too.
    static func isHangul(_ value: UInt32) -> Bool {
        switch value {
        case 0x1100...0x11FF, 0x3130...0x318F, 0xA960...0xA97F, 0xAC00...0xD7AF, 0xD7B0...0xD7FF, 0xFFA0...0xFFDC:
            return true
        default:
            return false
        }
    }

    /// Chinese and Japanese characters and their punctuation and symbols: no space belongs next to them.
    static func isSpaceless(_ value: UInt32) -> Bool {
        if isHanOrKana(value) { return true }
        switch value {
        case 0x2E80...0x2FDF, 0x2FF0...0x2FFF, // radicals, ideographic description
             0x3000...0x303F, // CJK punctuation
             0x3100...0x312F, 0x3190...0x31EF, // bopomofo, kanbun, strokes
             0x3200...0x33FF, // enclosed and compatibility forms
             0xFE10...0xFE1F, 0xFE30...0xFE4F, // vertical and compatibility punctuation
             0xFF00...0xFF65, 0xFFE0...0xFFEF: // full-width and half-width punctuation and symbols
            return true
        default:
            return false
        }
    }

    /// A letter, digit or mark outside Chinese, Japanese and Korean.
    static func isLatinWord(_ value: UInt32) -> Bool {
        guard let scalar = Unicode.Scalar(value) else { return false }
        return WordCount.isWordCharacter(scalar) && !isSpaceless(value) && !isHangul(value)
    }

    private static func isLineBreak(_ value: UInt32) -> Bool {
        [0x0A, 0x0B, 0x0C, 0x0D, 0x85, 0x2028, 0x2029].contains(value)
    }

    /// What ends a clause or a sentence, as folded: full-width ，。！？；： are ASCII by then.
    private static func isClausePunctuation(_ value: UInt32) -> Bool {
        switch value {
        case 0x2E, 0x2C, 0x3B, 0x3A, 0x21, 0x3F, // . , ; : ! ?
             0x3002, 0x3001, 0x2026, 0xFF61, 0xFF64: // 。、… and half-width ｡､
            return true
        default:
            return false
        }
    }
}
