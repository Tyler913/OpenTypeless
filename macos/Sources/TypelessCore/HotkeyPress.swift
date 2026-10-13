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
