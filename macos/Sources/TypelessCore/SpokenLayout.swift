import Foundation

/// Spoken layout commands: "new line" and "new paragraph" (换行 / 新段落, 改行, 줄 바꿈, neue Zeile…) said in a dictation
/// become a line break and a blank line, before the clean-up and whether or not there is one.
///
/// Only a command said as a phrase of its own counts: with a pause before and after it, which speech recognition writes as
/// punctuation around it ("Hi Anna. New line. Thanks…", "周报，换行，登录模块…"), or at the very start or end. Said
/// within a sentence ("add a new line to the config", "这里要不要换行", "每条都换行写") the words are part of the text
/// and stay. This is decided here, by rule, rather than by the clean-up model, which can't be relied on to tell the two
/// apart, isn't there when clean-up is off or fails, and would make ordinary dictations depend on how it reads them. The
/// price is that a command run into the words around it without a pause is missed and stays as words: never a line break
/// the speaker didn't ask for. A transcript without a command comes back exactly as it was.
public enum SpokenLayout {
    /// The commands, lowercased and without spaces (so "new line", "newline" and "New Line" are one; 줄 바꿈 and 줄바꿈 too),
    /// with the line breaks each stands for. Those of every language the app speaks are recognised in any dictation.
    static let commands: [String: String] = {
        let line = [
            "newline", // English: "new line"
            "换行", "換行", // Chinese
            "改行", // Japanese
            "줄바꿈", "새줄", "새로운줄", // Korean
            "nuevalínea", "nuevalinea", // Spanish
            "novalinha", // Portuguese
            "àlaligne", "alaligne", "nouvelleligne", // French
            "neuezeile", // German
            "новаястрока", // Russian
        ]
        let paragraph = [
            "newparagraph",
            "新段落", "另起一段", "换一段", "換一段",
            "新しい段落", "改段落",
            "새단락", "새문단", "새로운단락", "새로운문단",
            "nuevopárrafo", "nuevoparrafo",
            "novoparágrafo", "novoparagrafo",
            "nouveauparagraphe",
            "neuerabsatz",
            "новыйабзац",
        ]
        var commands: [String: String] = [:]
        for key in line { commands[key] = "\n" }
        for key in paragraph { commands[key] = "\n\n" }
        return commands
    }()

    /// A transcript laid out for the clean-up and the paste (see `layout`).
    public struct LaidOut: Equatable, Sendable {
        /// The line breaks of a command at the very start, kept out of the clean-up (whose answer is trimmed).
        public let leading: String
        /// What the clean-up is given, or what goes in without one.
        public let body: String
        /// The line breaks of a command at the very end.
        public let trailing: String
        /// Whether a command was said: every line break in `body` is then one the speaker asked for, and the clean-up is
        /// told to keep them. Otherwise `body` is the transcript exactly as it came, line breaks of its own included.
        public let dictatedBreaks: Bool

        /// The whole laid-out text, as it goes in when it isn't cleaned up.
        public var text: String { leading + body + trailing }
        /// `cleaned` (the clean-up of `body`) with the breaks at the start and end put back.
        public func around(_ cleaned: String) -> String { leading + cleaned + trailing }
    }

    /// The transcript as the dictation uses it: with `enabled` and a command said, laid out (see `apply`) and its edges
    /// split off (see `edges`); otherwise the transcript as it is.
    public static func layout(_ transcript: String, enabled: Bool) -> LaidOut {
        let applied = enabled ? apply(transcript) : transcript
        guard applied != transcript else {
            return LaidOut(leading: "", body: transcript, trailing: "", dictatedBreaks: false)
        }
        let parts = edges(applied)
        return LaidOut(leading: parts.leading, body: parts.body, trailing: parts.trailing, dictatedBreaks: true)
    }

    /// `transcript` with each command said on its own turned into its line breaks. The punctuation before a command stays
    /// ("Hi Anna," keeps its comma, a line can end in "。"); the command, the punctuation after it and the spaces around
    /// the break go. A command at the very start or end gives a break there (see `edges`). Line breaks speech recognition
    /// put in itself (some services break the text at their own segments, mid-sentence) become a space then, or nothing
    /// between Chinese or Japanese characters, so the only breaks left are the ones the speaker asked for. Without a
    /// command, `transcript` comes back as it is.
    public static func apply(_ transcript: String) -> String {
        let pieces = clauses(of: Array(transcript))
        guard pieces.contains(where: { lineBreaks(for: $0.clause) != nil }) else { return transcript }
        var result = ""
        var afterBreak = false
        for (index, piece) in pieces.enumerated() {
            if let breaks = lineBreaks(for: piece.clause) {
                while let last = result.last, last.isWhitespace, !last.isNewline { result.removeLast() }
                result += breaks
                afterBreak = true
                continue
            }
            let clause = afterBreak ? String(piece.clause.drop { $0.isWhitespace && !$0.isNewline }) : piece.clause
            result += clause
            let next = pieces[(index + 1)...].lazy.compactMap { $0.clause.first(where: { !$0.isWhitespace }) }.first
            result += withoutLineBreaks(piece.separator, after: result.last, before: next)
            afterBreak = false
        }
        return result
    }

    /// `separator` with the line breaks speech recognition put in (and the spaces around them) as one space, or none where
    /// the text on either side is Chinese or Japanese, or at the end.
    static func withoutLineBreaks(_ separator: String, after previous: Character?, before next: Character?) -> String {
        guard separator.contains(where: \.isNewline) else { return separator }
        let kept = String(separator.filter { !$0.isWhitespace })
        let joins = { (character: Character?) in
            character?.unicodeScalars.first.map(TranscriptJoiner.joinsWithoutSpace) ?? true
        }
        let lastKept = kept.last ?? previous
        return joins(lastKept) || joins(next) ? kept : kept + " "
    }

    /// Splits off the line breaks a command at the very start or end gave, which the clean-up would otherwise lose (its
    /// answer is trimmed): they're put back around its text. Without any there, `body` is `text` as it is.
    public static func edges(_ text: String) -> (leading: String, body: String, trailing: String) {
        let characters = Array(text)
        var start = 0
        while start < characters.count, characters[start].isWhitespace { start += 1 }
        guard start < characters.count else {
            return (String(repeating: "\n", count: characters.filter(\.isNewline).count), "", "")
        }
        var end = characters.count
        while end > start, characters[end - 1].isWhitespace { end -= 1 }
        let leading = characters[..<start].filter(\.isNewline).count
        let trailing = characters[end...].filter(\.isNewline).count
        let body = String(characters[(leading > 0 ? start : 0)..<(trailing > 0 ? end : characters.count)])
        return (String(repeating: "\n", count: leading), body, String(repeating: "\n", count: trailing))
    }

    /// The line breaks `clause` (one piece between punctuation, see `clauses`) stands for, or nil when it isn't a command.
    static func lineBreaks(for clause: String) -> String? {
        let key = String(clause.lowercased().filter { !$0.isWhitespace })
        guard !key.isEmpty else { return nil }
        return commands[key]
    }

    /// The text cut into clauses, each with the run of punctuation and spaces after it.
    static func clauses(of characters: [Character]) -> [(clause: String, separator: String)] {
        var pieces: [(clause: String, separator: String)] = []
        var start = 0
        var index = 0
        while index < characters.count {
            guard isBoundary(characters, index) else {
                index += 1
                continue
            }
            var end = index
            while end < characters.count, characters[end].isWhitespace || isBoundary(characters, end) { end += 1 }
            pieces.append((String(characters[start..<index]), String(characters[index..<end])))
            start = end
            index = end
        }
        if start < characters.count { pieces.append((String(characters[start...]), "")) }
        return pieces
    }

    /// Punctuation that ends a clause, a line break included. ".", "," and ":" only before a space, more punctuation or
    /// the end, so "3.5", "1,000" and "10:30" stay whole (as in `SegmentedPolish`).
    static func isBoundary(_ characters: [Character], _ index: Int) -> Bool {
        let character = characters[index]
        if character.isNewline || alwaysEnds.contains(character) { return true }
        guard endsBeforeSpace.contains(character) else { return false }
        guard index + 1 < characters.count else { return true }
        let next = characters[index + 1]
        return next.isWhitespace || next.isNewline || alwaysEnds.contains(next) || endsBeforeSpace.contains(next)
    }

    private static let alwaysEnds: Set<Character> = ["。", "，", "、", "！", "？", "；", "：", "．", "｡", "､", "…", "!", "?", ";"]
    private static let endsBeforeSpace: Set<Character> = [".", ",", ":"]
}
