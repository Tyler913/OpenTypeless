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
