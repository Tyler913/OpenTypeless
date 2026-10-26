import Foundation

/// The text around the cursor in the field being dictated into, which the clean-up gets as a spelling reference: the
/// names and terms a reply is about (a colleague's name in the thread, a product, a code identifier) are already on
/// screen, and they're what speech recognition gets wrong most. The app reads it once, as recording starts, through
/// Accessibility (UI Automation on Windows), only from a field that says it's editable and not a password field, and only
/// when the speaker switched it on. It's sent with the transcript and never kept. Like Wispr Flow's nearby text;
/// Superwhisper and VoiceInk read the screen instead (a screenshot and OCR), which needs screen recording permission and
/// reads far more than the field.
public enum CursorContext {
    /// At most this many characters go to the clean-up: the message being replied to, or the paragraph being written,
    /// with room on either side of the cursor, for a few hundred tokens.
    public static let limit = 1_500

    /// Which parts of a field were read only in part, at a length the app chose rather than at the start or end of the
    /// field. Such a read can end in the middle of a character (half an emoji, a family emoji without its last member).
    public struct Cut: OptionSet, Sendable {
        public let rawValue: Int
        public init(rawValue: Int) { self.rawValue = rawValue }
        /// The text before the selection doesn't reach back to the start of the field.
        public static let before = Cut(rawValue: 1)
        /// The selection was read only up to a length.
        public static let selection = Cut(rawValue: 2)
        /// The text after the selection doesn't reach the end of the field.
        public static let after = Cut(rawValue: 4)
    }

    /// The excerpt of a field's whole text that goes to the clean-up. `selection` is where the cursor or the selection
    /// is, in UTF-16 offsets (as Accessibility and UI Automation count them), clamped to the text and rounded down to
    /// whole characters; nil when the field doesn't say, and then the cursor is taken to be at the end, where it is when
    /// replying.
    public static func excerpt(text: String, selection: (location: Int, length: Int)?, limit: Int = limit) -> String? {
        let count = text.utf16.count
        let location = min(max(0, selection?.location ?? count), count)
        let length = min(max(0, selection?.length ?? 0), count - location)
        let start = index(in: text, utf16Offset: location)
        let end = max(start, index(in: text, utf16Offset: location + length))
        return excerpt(before: String(text[..<start]), selected: String(text[start..<end]), after: String(text[end...]),
                       limit: limit)
    }

    /// The selection, cut to `limit` characters, and as much of the text on either side of it as fits, split evenly
    /// (what one side doesn't need goes to the other), with every run of spaces or blank lines squeezed to one. A word
    /// cut in two at either edge is dropped, so a name is never shown half spelled (Chinese and Japanese, written without
    /// spaces, are cut anywhere), and so is a character a read cut through the middle of (U+FFFD). Characters are whole:
    /// an emoji or a CJK character is never split.
    ///
    /// Where a part was read only in part (`cut`), its cut end can be anywhere, even inside a character: the read's last
    /// character may be the first two of a family emoji's three people, which looks whole. So it's trimmed back past the
    /// first space or punctuation mark from that end (see `isBoundary`), beyond which the text reads as in the whole field,
    /// and all of it goes when there's none. A selection read in part isn't followed by the text after it, which isn't
    /// what comes after its first part. Nil when nothing but whitespace is left.
    public static func excerpt(before: String, selected: String, after: String, cut: Cut = [],
                               limit: Int = limit) -> String? {
        var before = before, selected = selected, after = after
        if cut.contains(.before) {
            before = before.firstIndex(where: isBoundary).map { String(before[before.index(after: $0)...]) } ?? ""
        }
        if cut.contains(.selection) {
            selected = selected.lastIndex(where: isBoundary).map { String(selected[..<$0]) } ?? ""
            after = ""
        } else if cut.contains(.after) {
            after = after.lastIndex(where: isBoundary).map { String(after[..<$0]) } ?? ""
        }
        selected = String(squeezed(selected).prefix(max(0, limit)))
        before = squeezed(before)
        after = squeezed(after)
        let room = max(0, limit - selected.count)
        let beforeCount = min(before.count, max(room / 2, room - after.count))
        let afterCount = min(after.count, room - beforeCount)
        var head = before.suffix(beforeCount)
        if beforeCount < before.count { head = dropCutWord(atStartOf: head, after: before.dropLast(beforeCount).last) }
        var tail = after.prefix(afterCount)
        if afterCount < after.count { tail = dropCutWord(atEndOf: tail, before: after.dropFirst(afterCount).first) }
        // Squeezed again where the parts meet: a space on both sides of the cursor is still one.
        let text = squeezed(String(head + selected + tail)).trimmingCharacters(in: trimmed)
        return text.isEmpty ? nil : text
    }

    private static let trimmed = CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn: "\u{FFFD}"))

    /// A character a part read only in part is trimmed back to: a single code point that is a space, a line break or a
    /// punctuation mark. Nothing joins onto either side of one to make a character of it, so the text on its far side from
    /// the cut is split into characters as it is in the whole field.
    static func isBoundary(_ character: Character) -> Bool {
        character.unicodeScalars.count == 1 && (character.isWhitespace || character.isPunctuation)
    }

    /// Each run of whitespace as one line break when it has one, otherwise as one space: indentation and blank lines
    /// would only use up the room.
    static func squeezed(_ text: String) -> String {
        var result = ""
        var run: Character?
        for character in text {
            if character.isWhitespace {
                if run == nil || character.isNewline { run = character.isNewline ? "\n" : " " }
                continue
            }
            if let space = run { result.append(space) }
            run = nil
            result.append(character)
        }
        if let space = run { result.append(space) }
        return result
    }

    /// Drops the word a cut went through at the start of `head`: up to its first space, or all of it when there's none.
    /// `previous` is the character before the cut.
    private static func dropCutWord(atStartOf head: Substring, after previous: Character?) -> Substring {
        guard let previous, let first = head.first, isSpacedWordCharacter(previous), isSpacedWordCharacter(first) else {
            return head
        }
        guard let space = head.firstIndex(where: \.isWhitespace) else { return head[head.endIndex...] }
        return head[head.index(after: space)...]
    }

    /// The same at the end of `tail`; `next` is the character after the cut.
    private static func dropCutWord(atEndOf tail: Substring, before next: Character?) -> Substring {
        guard let next, let last = tail.last, isSpacedWordCharacter(next), isSpacedWordCharacter(last) else { return tail }
        guard let space = tail.lastIndex(where: \.isWhitespace) else { return tail[..<tail.startIndex] }
        return tail[..<space]
    }

    /// A letter or digit of a script that puts spaces between words (Latin, Cyrillic, Korean…), so a cut next to it can
    /// be in the middle of a word.
    private static func isSpacedWordCharacter(_ character: Character) -> Bool {
        guard character.isLetter || character.isNumber, let scalar = character.unicodeScalars.first else { return false }
        return !TranscriptJoiner.joinsWithoutSpace(scalar)
    }

    /// The start of the character `offset` UTF-16 units into `text` falls in (the end for an offset past it).
    static func index(in text: String, utf16Offset offset: Int) -> String.Index {
        var position = 0
        var index = text.startIndex
        while index < text.endIndex {
            position += text[index].utf16.count
            if position > offset { return index }
            index = text.index(after: index)
        }
        return text.endIndex
    }
}
