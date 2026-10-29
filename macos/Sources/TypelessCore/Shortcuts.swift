import Foundation

/// What a shortcut of its own can do besides dictating; the dictation hotkey stays separate (see `HotkeyPress`). An
/// action fires as its keys go down, is ignored while a dictation is under way, and its keys are kept from the focused
/// app. None has a shortcut until the user records one. A new action is a new case here.
public enum ShortcutAction: String, CaseIterable, Sendable {
    /// Puts the newest finished dictation at the cursor again (see `LastDictation`).
    case pasteLastDictation
}

/// A key and the modifiers held with it, in the platform's own codes (macOS: a virtual key code and `CGEventFlags` bits;
/// Windows: a virtual-key code and `HotkeyModifiers`), or a modifier key held on its own.
public struct KeyChord: Hashable, Sendable {
    public var keyCode: Int64
    /// Only the modifiers shortcuts are made of (⌃⌥⇧⌘, or Ctrl Alt Shift Win); none for a modifier on its own.
    public var modifiers: UInt64
    public var isModifierOnly: Bool

    public init(keyCode: Int64, modifiers: UInt64 = 0, isModifierOnly: Bool = false) {
        self.keyCode = keyCode
        self.modifiers = isModifierOnly ? 0 : modifiers
        self.isModifierOnly = isModifierOnly
    }
}

/// Why a key combination can't be an action's shortcut.
public enum ShortcutConflict: Equatable, Sendable {
    /// A modifier on its own would fire with every other shortcut that uses it.
    case modifierOnly
    /// Esc cancels a dictation (and is kept from the focused app meanwhile).
    case escape
    /// It's the dictation hotkey.
    case dictation
    /// It's already this action's shortcut.
    case action(ShortcutAction)
}

/// The shortcuts the user gave to actions, and which action a key going down fires.
public struct ShortcutMap: Equatable, Sendable {
    public private(set) var chords: [ShortcutAction: KeyChord]

    /// A modifier on its own never makes a shortcut here (see `ShortcutConflict.modifierOnly`), whatever was saved.
    public init(_ chords: [ShortcutAction: KeyChord] = [:]) {
        self.chords = chords.filter { !$0.value.isModifierOnly }
    }

    public var isEmpty: Bool { chords.isEmpty }

    /// The action whose shortcut is this key with exactly these modifiers held (counting only those shortcuts are made
    /// of), if any: ⌃⌘V doesn't also fire on ⌃⌥⌘V.
    public func action(keyCode: Int64, modifiers: UInt64) -> ShortcutAction? {
        let pressed = KeyChord(keyCode: keyCode, modifiers: modifiers)
        return ShortcutAction.allCases.first { chords[$0] == pressed }
    }

    /// Why `chord` can't be the shortcut of `action`, or nil when it can. `dictation` is the dictation hotkey.
    public func conflict(_ chord: KeyChord, for action: ShortcutAction, dictation: KeyChord,
                         escapeKeyCode: Int64) -> ShortcutConflict? {
        if chord.isModifierOnly { return .modifierOnly }
        if chord.keyCode == escapeKeyCode { return .escape }
        if chord == dictation { return .dictation }
        if let other = ShortcutAction.allCases.first(where: { $0 != action && chords[$0] == chord }) {
            return .action(other)
        }
        return nil
    }
}

/// A JSON object of entries by name saved as one value (the actions' shortcuts, by action name), read and changed one
/// entry at a time: an entry this version can't read (saved by a newer one, or damaged) neither hides the others nor is
/// lost when another one changes. The Windows app's settings file does the same with its JSON nodes.
public enum StoredEntries {
    /// Each entry of `data` that decodes as `T`, by name.
    public static func decode<T: Decodable>(_ data: Data?, as type: T.Type) -> [String: T] {
        var decoded: [String: T] = [:]
        for (name, value) in object(data) {
            // Only an object or an array can be serialised on its own (anything else would raise, not throw).
            guard JSONSerialization.isValidJSONObject(value),
                  let entry = try? JSONSerialization.data(withJSONObject: value),
                  let item = try? JSONDecoder().decode(T.self, from: entry) else { continue }
            decoded[name] = item
        }
        return decoded
    }

    /// `data` with the entry `name` replaced by `value` (removed when nil) and every other one as it was; `data` itself
    /// when `value` can't be encoded.
    public static func replacing<T: Encodable>(_ name: String, with value: T?, in data: Data?) -> Data? {
        var entries = object(data)
        if let value {
            guard let encoded = try? JSONEncoder().encode(value),
                  let entry = try? JSONSerialization.jsonObject(with: encoded) else { return data }
            entries[name] = entry
        } else {
            entries[name] = nil
        }
        guard JSONSerialization.isValidJSONObject(entries) else { return data }
        return try? JSONSerialization.data(withJSONObject: entries, options: [.sortedKeys])
    }

    private static func object(_ data: Data?) -> [String: Any] {
        data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] } ?? [:]
    }
}

/// Which dictation "Paste last dictation" puts at the cursor again: the newest that finished with text. When the newest
/// one failed and its recording is saved, that one is re-sent instead and pasted once it's done, as the menu bar's
/// Retry does: what the user is after is what they said last, not the dictation before it.
public enum LastDictation {
    public struct Entry: Equatable, Sendable {
        public var kind: HistoryPruning.Kind
        /// Its text (cleaned up, or as transcribed) isn't empty.
        public var hasText: Bool
        public var hasAudio: Bool
        /// An edit of a selection by voice (see `SelectionCommand`), not a dictation: its text replaced a selection, and
        /// without that selection (which isn't kept) it can't be run again.
        public var isCommand: Bool

        public init(kind: HistoryPruning.Kind, hasText: Bool, hasAudio: Bool, isCommand: Bool = false) {
            self.kind = kind
            self.hasText = hasText
            self.hasAudio = hasAudio
            self.isCommand = isCommand
        }
    }

    public enum Choice: Equatable, Sendable {
        /// Paste the text of the entry at this index.
        case paste(Int)
        /// Re-send the recording of the entry at this index, and paste what comes back.
        case retry(Int)
        /// There's nothing to paste.
        case nothing
    }

    /// `entries` are newest first, as History keeps them. Passed over: dictations still under way, cancelled ones (the
    /// user threw them away with Esc), ones that heard no speech, failed ones whose recording is gone, and edits of a
    /// selection by voice.
    public static func choose(_ entries: [Entry]) -> Choice {
        for (index, entry) in entries.enumerated() where !entry.isCommand {
            switch entry.kind {
            case .finished where entry.hasText: return .paste(index)
            case .failed where entry.hasAudio: return .retry(index)
            default: continue
            }
        }
        return .nothing
    }
}
