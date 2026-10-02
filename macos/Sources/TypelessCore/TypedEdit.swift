import Foundation

/// Follows the keys pressed after a dictation is pasted, to know what the dictated text became without reading the
/// text field: for apps whose fields can't be read (many Electron apps, Firefox, WeChat), this is how a fix is seen.
///
/// It starts as the pasted text with the cursor at its end, and applies typing, Backspace / Delete and the arrow keys,
/// with or without a selection. A click puts the cursor somewhere it can't see (`cursorLost`); a word selected by
/// double-clicking or dragging finds it again (`select`), when that text occurs once in the dictated text. Anything it
/// can't follow exactly stops it (`apply` returns false), and then only the state before that key counts: a key while
/// the cursor is lost, the cursor leaving the dictated text (it doesn't know what's around it), a word jump across
/// Chinese or Japanese text (macOS moves by dictionary words there), a jump to the start or end of a line, and up / down.
public struct TypedEdit: Sendable, Equatable {
    public enum Unit: Sendable { case character, word }
    public enum Key: Sendable, Equatable {
        /// Text typed, or pasted with ⌘V.
        case insert(String)
        case deleteBackward(Unit)
        case deleteForward(Unit)
        case left(Unit, extend: Bool)
        case right(Unit, extend: Bool)
    }

    public private(set) var characters: [Character]
    /// Where the cursor is; with `anchor` it spans the selection.
    public private(set) var caret: Int
    public private(set) var anchor: Int
    /// False after a click, until a selection shows where the cursor is.
    public private(set) var knowsCursor = true

    public init(pasted: String) {
        characters = Array(pasted)
        caret = characters.count
        anchor = caret
    }

    public var text: String { String(characters) }
    private var selection: Range<Int> { min(caret, anchor)..<max(caret, anchor) }

    /// A click: the cursor is somewhere unknown until `select` finds it.
    public mutating func cursorLost() {
        knowsCursor = false
    }

    /// The user selected `text` (double-click, drag): selects it here, if it occurs exactly once. False otherwise, and
    /// then the cursor stays unknown.
    @discardableResult
    public mutating func select(_ text: String) -> Bool {
        let needle = Array(text)
        guard !needle.isEmpty, needle.count <= characters.count else { return false }
        var found: Int?
        for start in 0...(characters.count - needle.count) where characters[start..<(start + needle.count)].elementsEqual(needle) {
            guard found == nil else { return false } // more than once: can't tell which
            found = start
        }
        guard let found else { return false }
        anchor = found
        caret = found + needle.count
        knowsCursor = true
        return true
    }

    /// Applies one key; false when it can't be followed (the state stays as before it).
    public mutating func apply(_ key: Key) -> Bool {
        guard knowsCursor else { return false }
        switch key {
        case .insert(let string):
            guard !string.isEmpty else { return false }
            replaceSelection(with: Array(string))
            return true
        case .deleteBackward(let unit), .deleteForward(let unit):
            if !selection.isEmpty {
                replaceSelection(with: [])
                return true
            }
            let backward = if case .deleteBackward = key { true } else { false }
            guard let target = position(from: caret, backward: backward, unit: unit) else { return false }
            let range = min(caret, target)..<max(caret, target)
            guard !range.isEmpty else { return false }
            characters.removeSubrange(range)
            caret = range.lowerBound
            anchor = caret
            return true
        case .left(let unit, let extend), .right(let unit, let extend):
            let backward = if case .left = key { true } else { false }
            let target: Int
            if !extend, !selection.isEmpty, unit == .character {
                // An arrow collapses a selection to its side.
                target = backward ? selection.lowerBound : selection.upperBound
            } else {
                guard let moved = position(from: caret, backward: backward, unit: unit) else { return false }
                target = moved
            }
            caret = target
            if !extend { anchor = target }
            return true
        }
    }

    private mutating func replaceSelection(with new: [Character]) {
        let range = selection
        characters.replaceSubrange(range, with: new)
        caret = range.lowerBound + new.count
        anchor = caret
    }

    /// The cursor position one unit away, or nil when that leaves the text or can't be told.
    private func position(from index: Int, backward: Bool, unit: Unit) -> Int? {
        switch unit {
        case .character:
            let target = backward ? index - 1 : index + 1
            return (0...characters.count).contains(target) ? target : nil
        case .word:
            // Option-arrow: over the spaces and punctuation next to the cursor, then over the word.
            var i = index
            func at(_ i: Int) -> Character { characters[backward ? i - 1 : i] }
            func inside(_ i: Int) -> Bool { backward ? i > 0 : i < characters.count }
            while inside(i), !Self.isWordCharacter(at(i)) { i += backward ? -1 : 1 }
            guard inside(i) else { return nil } // would run past the dictated text
            let start = i
            while inside(i), Self.isWordCharacter(at(i)) {
                if Self.isIdeographic(at(i)) { return nil }
                i += backward ? -1 : 1
            }
            // A word that runs to the edge may go on outside the dictated text: only where it ends at a separator.
            if !inside(i), i != start, backward ? i == 0 : i == characters.count { return nil }
            return i
        }
    }

    static func isWordCharacter(_ c: Character) -> Bool { c.isLetter || c.isNumber || c == "_" || c == "'" || c == "’" }

    /// Han, kana and hangul, where macOS finds word boundaries with a dictionary.
    static func isIdeographic(_ c: Character) -> Bool {
        c.unicodeScalars.contains { scalar in
            switch scalar.value {
            case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xAC00...0xD7AF, 0xF900...0xFAFF, 0x20000...0x2FA1F: true
            default: false
            }
        }
    }
}
