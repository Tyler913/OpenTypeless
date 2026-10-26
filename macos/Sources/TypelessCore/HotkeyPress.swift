import Foundation

/// How a press of the dictation hotkey is read: a hold is push-to-talk, a tap toggles hands-free mode, and another key
/// soon after the press makes it a key combination instead.
///
/// Times are the key events' own, in seconds on one clock that doesn't jump (macOS: `ProcessInfo.systemUptime`;
/// Windows: the tick count). The app may only get to the release late, e.g. while a Bluetooth microphone takes a second
/// to open, and that wait mustn't turn a hold into a tap (or a tap into a hold).
public enum HotkeyPress {
    /// A press shorter than this toggles hands-free mode instead of push-to-talk.
    public static let tapThreshold: TimeInterval = 0.35
    /// Another key this soon after the press makes it a combination (Fn+F1…), which cancels the dictation.
    public static let comboWindow: TimeInterval = 1.0
    /// A key event stamped longer ago than this (or in the future, or not at all) came from software whose clock
    /// can't be trusted.
    static let maxEventAge: TimeInterval = 10

    /// Whether a press released at `releasedAt` was a tap (hands-free) rather than a hold (push-to-talk).
    public static func isTap(pressedAt: TimeInterval, releasedAt: TimeInterval) -> Bool {
        releasedAt - pressedAt < tapThreshold
    }

    /// Whether another key at `otherKeyAt` came soon enough after the press to make it a combination.
    public static func isCombo(pressedAt: TimeInterval, otherKeyAt: TimeInterval) -> Bool {
        otherKeyAt - pressedAt < comboWindow
    }

    /// The event's own time, or `now` when its stamp isn't believable: keys posted by other apps may carry none.
    public static func eventTime(_ stamp: TimeInterval, now: TimeInterval) -> TimeInterval {
        stamp > 0 && stamp <= now && now - stamp < maxEventAge ? stamp : now
    }
}

/// What another key, pressed while the dictation hotkey is held, does to the recording.
public enum OtherKeyOutcome<Mode: Equatable & Sendable>: Equatable, Sendable {
    /// A registered chord: the recording goes on, in this mode.
    case switchMode(Mode)
    /// A key combination with the hotkey (Fn+F1…): the dictation is cancelled.
    case cancel
    /// Nothing happens.
    case ignore
}

/// Second keys that, pressed while the dictation hotkey is held, switch the recording's mode instead of making a key
/// combination that cancels it. Key codes are the platform's (see `KeyChord`); the app keeps the registered keys from
/// the focused app. With none registered, every other key is read as it always was.
public struct ModeChords<Mode: Equatable & Sendable>: Sendable {
    private var modes: [Int64: Mode] = [:]

    public init() {}

    public var keys: Set<Int64> { Set(modes.keys) }
    public var isEmpty: Bool { modes.isEmpty }

    public mutating func register(_ keyCode: Int64, mode: Mode) {
        modes[keyCode] = mode
    }

    public mutating func unregister(_ keyCode: Int64) {
        modes[keyCode] = nil
    }

    public func mode(for keyCode: Int64) -> Mode? { modes[keyCode] }

    /// What `keyCode`, pressed at `time` while the hotkey pressed at `pressedAt` is down, does. A chord works for as long
    /// as the hotkey is held; any other key only makes a combination soon after the press, and never once the
    /// recording is hands-free.
    public func otherKey(_ keyCode: Int64, pressedAt: TimeInterval, at time: TimeInterval,
                         handsFree: Bool) -> OtherKeyOutcome<Mode> {
        if let mode = modes[keyCode] { return .switchMode(mode) }
        return !handsFree && HotkeyPress.isCombo(pressedAt: pressedAt, otherKeyAt: time) ? .cancel : .ignore
    }
}

/// Modifier keys whose press was kept from the focused app (a mode chord: Shift pressed with the hotkey, for
/// translation), until their release. macOS tells apps which modifiers are down by the flags every key and mouse event
/// carries, not only by the modifiers' own events, so the flags of everything let through meanwhile are cleared of
/// them: otherwise the hotkey's own release would carry Shift to the app, which then never sees Shift go up, and takes
/// it for stuck. Flags are raw `CGEventFlags`, with the device-dependent bits of each side (NX_DEVICELSHIFTKEYMASK…).
/// macOS only: Windows keeps a swallowed key out of the key state itself.
public struct SuppressedModifiers: Equatable, Sendable {
    /// keyCode → its own side's bit, the other side's, and the flag both share.
    static let sides: [Int64: (own: UInt64, other: UInt64, shared: UInt64)] = [
        56: (0x02, 0x04, 0x20000), 60: (0x04, 0x02, 0x20000), // left / right ⇧
        59: (0x01, 0x2000, 0x40000), 62: (0x2000, 0x01, 0x40000), // left / right ⌃
        58: (0x20, 0x40, 0x80000), 61: (0x40, 0x20, 0x80000), // left / right ⌥
        55: (0x08, 0x10, 0x100000), 54: (0x10, 0x08, 0x100000), // left / right ⌘
    ]

    public private(set) var keys: Set<Int64> = []

    public init() {}

    public var isEmpty: Bool { keys.isEmpty }

    public func contains(_ keyCode: Int64) -> Bool { keys.contains(keyCode) }

    /// Only the four modifiers with sides can be kept (Fn, Caps Lock… are never chords).
    public mutating func insert(_ keyCode: Int64) {
        guard Self.sides[keyCode] != nil else { return }
        keys.insert(keyCode)
    }

    public mutating func remove(_ keyCode: Int64) {
        keys.remove(keyCode)
    }

    /// `flags` without the kept keys: each one's own side's bit, and the flag the two sides share unless the other side
    /// is down too (and was let through). Flags with neither side's bit came from software that set them itself, and
    /// keep the shared flag.
    public func strip(_ flags: UInt64) -> UInt64 {
        var stripped = flags
        for key in keys {
            guard let side = Self.sides[key] else { continue }
            stripped &= ~side.own
        }
        for key in keys {
            guard let side = Self.sides[key], flags & side.own != 0, stripped & side.other == 0 else { continue }
            stripped &= ~side.shared
        }
        return stripped
    }

    /// Lets go of the kept keys no longer down in `flags` (the keys' state now, after events were lost): their release
    /// went by unseen. A side is down by its own bit, or by the shared flag alone when neither side's bit is there.
    public mutating func keepHeld(in flags: UInt64) {
        keys = keys.filter { key in
            guard let side = Self.sides[key], flags & side.shared != 0 else { return false }
            return flags & side.own != 0 || flags & (side.own | side.other) == 0
        }
    }
}
