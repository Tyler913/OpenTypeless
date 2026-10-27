import Foundation

/// "Finish and send": the key that, pressed while a dictation is recording, ends it and, once the text is pasted, is
/// pressed in that app to send it. Return on its own, with ⇧ or with ⌘ (Enter, Shift + Enter, Ctrl + Enter on Windows),
/// since chat boxes differ in which one sends; or none, the default. Only for the dictation it ended, and only where its
/// text was pasted: whatever sends the text to the clipboard instead (another app in front, the screen locking, Esc)
/// sends nothing. "Press Return after inserting", which sends every dictation, is a separate setting.
///
/// There is deliberately no spoken "send it": a send can't be taken back, and words in a dictation are too easily meant
/// as part of the text.
public enum SendKey: String, CaseIterable, Sendable {
    case off
    case enter
    case shiftEnter
    /// ⌘Return on macOS, Ctrl + Enter on Windows: the modifier the platform's shortcuts are made with.
    case commandEnter

    /// A send key this soon after the dictation hotkey went down, while it's still held, was pressed together with it as
    /// a key combination (Fn+Return is the keypad's Enter on a Mac laptop), too soon for anything to have been said: it's
    /// left to the focused app, as it always was. The same as the shortest recording that isn't thrown away.
    public static let combinationWindow: TimeInterval = 0.4

    /// The saved choice; off for none, or for one this version can't read (saved by a newer one, or damaged).
    public init(saved: String?) {
        self = saved.flatMap(SendKey.init(rawValue:)) ?? .off
    }

    /// Whether a send key at `time` came with the hotkey pressed at `hotkeyPressedAt` and still held (see
    /// `combinationWindow`). Times as in `HotkeyPress`.
    public static func isCombination(hotkeyPressedAt: TimeInterval, at time: TimeInterval) -> Bool {
        time - hotkeyPressedAt < combinationWindow
    }

    /// The modifiers held with Return, in the platform's own bits; nil when off.
    public func modifiers(_ codes: SendKeyCodes) -> UInt64? {
        switch self {
        case .off: return nil
        case .enter: return 0
        case .shiftEnter: return codes.shift
        case .commandEnter: return codes.command
        }
    }

    /// Whether a dictation hotkey that is a key combination on `keyCode` takes this send key's presses: the hotkey's keys
    /// are read first, so that key (and, while the hotkey is held, any press of it) is the hotkey's, and the send key would
    /// never fire. Settings says so.
    public func isTaken(byHotkeyKey keyCode: Int64, isModifierOnly: Bool, codes: SendKeyCodes) -> Bool {
        self != .off && !isModifierOnly && codes.returnKeys.contains(keyCode)
    }

    /// Whether `keyCode` going down with `modifiers` held (only those shortcuts are made of) is this send key.
    /// `hotkeyModifiers`: those of the dictation hotkey while it's held (none hands-free), which may be down as well and
    /// then don't count: Return pressed while holding right ⌘ (or Right Alt) to talk, or the ⌥ of ⌥Space, is still Return.
    /// Any other modifier makes it another key: ⌥Return is left to the focused app.
    public func matches(keyCode: Int64, modifiers: UInt64, hotkeyModifiers: UInt64 = 0, codes: SendKeyCodes) -> Bool {
        guard let wanted = self.modifiers(codes), codes.returnKeys.contains(keyCode) else { return false }
        return modifiers & wanted == wanted && modifiers & ~wanted & ~hotkeyModifiers == 0
    }
}

/// The platform's codes for the keys a send key is made of.
public struct SendKeyCodes: Sendable {
    /// Return, and Enter on the keypad where it has a code of its own (on a Mac laptop, Fn+Return).
    public var returnKeys: Set<Int64>
    public var shift: UInt64
    /// ⌘ on macOS, Ctrl on Windows.
    public var command: UInt64

    public init(returnKeys: Set<Int64>, shift: UInt64, command: UInt64) {
        self.returnKeys = returnKeys
        self.shift = shift
        self.command = command
    }
}
