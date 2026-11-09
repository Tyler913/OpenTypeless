import Foundation

/// What a recording is for: plain dictation, a translation (see `TranslationTarget`), or an edit of the text selected
/// in the app in front (see `SelectionCommand`), which the translation and command chords (see `ModeChords`) switch it
/// to while recording.
public enum RecordingMode: String, CaseIterable, Equatable, Sendable {
    case dictation
    case translate
    case command
}

/// What the app is to do next about a dictation, as `DictationSession` decides it. The app carries each out with the
/// microphone, the capsule, History and the paste; the session only says which, and in what order.
public enum SessionEffect: Equatable, Sendable {
    /// Open the microphone and start a dictation; the app reports `DictationSession.began` once it is recording
    /// (nothing, when it couldn't start).
    case startRecording
    /// Stop recording and process what was said, or throw it away as an accidental tap
    /// (`DictationSession.isAccidental`).
    case finishRecording
    /// The running recording becomes this mode's, while that mode is still switched on; the app reports
    /// `DictationSession.switched` when it did.
    case switchMode(RecordingMode)
    /// Stop without inserting anything: a recording is thrown away, a dictation being processed keeps what was
    /// transcribed, as cancelled. Silently: without "Cancelled" in the capsule.
    case cancel(silently: Bool)
    /// Esc on a long recording: stop and insert nothing, but keep it (see `CancelPolicy`).
    case keepCancelled
    /// The "Paste last dictation" that was waiting has been dropped, and does nothing more.
    case pasteLastDropped
    /// Carry out an action's shortcut.
    case perform(ShortcutAction)

    /// The effect by name, the same on both platforms (`testdata/session-cases.json` and the session log).
    public var name: String {
        switch self {
        case .startRecording: return "startRecording"
        case .finishRecording: return "finishRecording"
        case let .switchMode(mode): return "switchMode:\(mode.rawValue)"
        case let .cancel(silently): return silently ? "cancel:silently" : "cancel"
        case .keepCancelled: return "keepCancelled"
        case .pasteLastDropped: return "pasteLastDropped"
        case let .perform(action): return "perform:\(action.rawValue)"
        }
    }
}

/// Where one dictation stands, and what each key, click and system event does to it: the decisions of the app's
/// `SessionController`, without the microphone, the capsule, History or the paste, so both apps make them the same way
/// and `testdata/session-cases.json` can check them.
///
/// The app tells it what happened (`hotkeyDown`, `escape`, `locked`…) and carries out the effects it answers with, in
/// order. What the app alone can know it reports as it happens: that the microphone opened (`began`), that a recording
/// is being processed (`processing`), that the dictation is over (`ended`). Nothing here waits, reads a clock or
/// touches the system: every time and length comes in with the call.
public struct DictationSession: Sendable {
    public enum Phase: String, CaseIterable, Equatable, Sendable {
        case idle
        case recording
        case processing
    }

    public private(set) var phase: Phase = .idle
    /// Which dictation is current: bumped whenever one ends or is cancelled, so work still out for an earlier one (its
    /// paste, its Return, its reset) can tell it's no longer wanted and leaves the next dictation alone.
    public private(set) var generation = 0
    /// When the hotkey went down, by the key event's own time (see `HotkeyPress`).
    public private(set) var pressedAt: TimeInterval?
    public private(set) var handsFree = false
    /// What the running recording is for; plain dictation at every start.
    public private(set) var mode: RecordingMode = .dictation
    /// The send key that ended this recording (see `SendKey`): pressed in the app once the text is pasted there, for
    /// this dictation only. Off unless it was pressed.
    public private(set) var sendKey: SendKey = .off
    /// Between the screen locking (or sleep) and unlocking.
    public private(set) var isLocked = false
    /// False when retrying from History, once the screen locked, or when the key was let go of in an app with other
    /// settings: the result goes to the clipboard instead.
    public private(set) var deliversByPaste = true
    /// Why `deliversByPaste` is false, for whether the text is also kept in the Scratchpad (see `ScratchpadCapture`).
    public private(set) var noPasteReason: ScratchpadCapture.Outcome = .fromHistory
    /// The "Paste last dictation" waiting for its keys to be let go, or pasting, by its number. A new dictation, Esc or
    /// the screen locking drops it (nil), and what it still had to do is left undone.
    public private(set) var pendingPasteLast: Int?
    private var pasteLastCount = 0
    /// Second keys that switch the recording's mode while the hotkey is held (see `ModeChords`).
    public var chords = ModeChords<RecordingMode>()

    public init() {}

    // MARK: - What happened

    /// The dictation hotkey went down. Between dictations it starts one, and whoever pressed it is at the keyboard: the
    /// screen is unlocked, whatever notification was missed, and a "Paste last dictation" still waiting gives way. In a
    /// hands-free recording it ends it. Otherwise (held down already, or the last dictation still being processed)
    /// nothing.
    public mutating func hotkeyDown() -> [SessionEffect] {
        switch phase {
        case .idle:
            isLocked = false
            return dropPasteLast() + [.startRecording]
        case .recording where handsFree:
            return [.finishRecording]
        case .recording, .processing:
            return []
        }
    }

    /// The dictation hotkey was let go of at `time`: a tap makes the recording hands-free, a hold ends it.
    public mutating func hotkeyUp(at time: TimeInterval) -> [SessionEffect] {
        guard phase == .recording, !handsFree, let pressedAt else { return [] }
        if HotkeyPress.isTap(pressedAt: pressedAt, releasedAt: time) {
            handsFree = true
            return []
        }
        return [.finishRecording]
    }

    /// Another key went down at `time` while recording: a registered chord switches the recording's mode (not to the
    /// one it has); otherwise, soon after the press, it makes a key combination with the hotkey, which isn't a
    /// dictation.
    public func otherKey(_ keyCode: Int64, at time: TimeInterval) -> [SessionEffect] {
        guard phase == .recording, let pressedAt else { return [] }
        switch chords.otherKey(keyCode, pressedAt: pressedAt, at: time, handsFree: handsFree) {
        case let .switchMode(newMode): return newMode == mode ? [] : [.switchMode(newMode)]
        case .cancel: return [.cancel(silently: true)]
        case .ignore: return []
        }
    }

    /// The recording is to end as letting go of the key would end it: the capsule's ↑, the longest recording reached,
    /// or the microphone gone.
    public func finishRequested() -> [SessionEffect] {
        phase == .recording ? [.finishRecording] : []
    }

    /// The send key went down while recording (`key`: the one chosen in Settings): the recording ends, and that key is
    /// pressed in the app once the text is in.
    public mutating func sendKeyDown(_ key: SendKey) -> [SessionEffect] {
        guard phase == .recording, key != .off else { return [] }
        sendKey = key
        return [.finishRecording]
    }

    /// Esc, or the capsule's ✕. `recordedSeconds`: how long the recording is so far, the pre-roll aside.
    public mutating func escape(recordedSeconds: Double) -> [SessionEffect] {
        switch phase {
        case .idle:
            return dropPasteLast()
        case .recording where CancelPolicy.keeps(recordedSeconds: recordedSeconds):
            return [.keepCancelled]
        case .recording, .processing:
            return [.cancel(silently: false)]
        }
    }

    /// The screen locked, or the computer is going to sleep. A recording stops as with Esc (a long one is kept), so the
    /// microphone isn't left open on the lock screen; a dictation being processed goes to the clipboard instead of
    /// into whatever has focus afterwards; a "Paste last dictation" still waiting is dropped.
    public mutating func locked(recordedSeconds: Double) -> [SessionEffect] {
        isLocked = true
        let dropped = dropPasteLast()
        switch phase {
        case .idle:
            return dropped
        case .recording:
            return dropped + escape(recordedSeconds: recordedSeconds)
        case .processing:
            deliverToClipboard(because: .locked)
            return dropped
        }
    }

    /// The screen was unlocked. False when it wasn't locked, and nothing changes.
    public mutating func unlocked() -> Bool {
        guard isLocked else { return false }
        isLocked = false
        return true
    }

    /// An action's shortcut was pressed: only between dictations.
    public func actionPressed(_ action: ShortcutAction) -> [SessionEffect] {
        phase == .idle ? [.perform(action)] : []
    }

    /// Command mode found the selection can't be edited, for the dictation that was `generation`: while that one is
    /// still recording, in command mode, it stops as if it never started.
    public func selectionRefused(generation: Int) -> [SessionEffect] {
        isCurrent(generation) && phase == .recording && mode == .command ? [.cancel(silently: true)] : []
    }

    // MARK: - What the app reports

    /// The microphone is open and the dictation is recording, its hotkey pressed at `pressedAt`.
    public mutating func began(pressedAt: TimeInterval) {
        phase = .recording
        self.pressedAt = pressedAt
        handsFree = false
        sendKey = .off
        mode = .dictation
        deliversByPaste = true
    }

    /// The running recording is `mode`'s from now on.
    public mutating func switched(to mode: RecordingMode) {
        self.mode = mode
    }

    /// The recording has ended and is being transcribed.
    public mutating func processing() {
        phase = .processing
    }

    /// A saved dictation is being run again, as `mode`. `paste`: whether its text goes to the cursor, or (from History)
    /// to the clipboard.
    public mutating func retrying(paste: Bool, mode: RecordingMode) {
        deliversByPaste = paste
        noPasteReason = .fromHistory
        phase = .processing
        self.mode = mode
    }

    /// This dictation's text goes to the clipboard, not to the cursor.
    public mutating func deliverToClipboard(because reason: ScratchpadCapture.Outcome) {
        deliversByPaste = false
        noPasteReason = reason
    }

    /// The dictation is over: delivered, failed, cancelled or thrown away. Work still out for it isn't current any more.
    public mutating func ended() {
        generation += 1
        phase = .idle
        pressedAt = nil
        handsFree = false
        sendKey = .off
        mode = .dictation
    }

    /// Whether `generation` is still the dictation under way (or, between dictations, the one to come).
    public func isCurrent(_ generation: Int) -> Bool {
        generation == self.generation
    }

    // MARK: - An accidental tap

    /// A recording shorter than this was a slip of the finger: it's thrown away, with no entry in History.
    public static let accidentalSeconds: Double = 0.4

    /// `recordedSeconds`: how long the recording is, the pre-roll aside.
    public static func isAccidental(recordedSeconds: Double) -> Bool {
        recordedSeconds < accidentalSeconds
    }

    // MARK: - Paste last dictation

    /// A "Paste last dictation" was asked for: its number, or nil while a dictation is under way or another one is
    /// still waiting.
    public mutating func beginPasteLast() -> Int? {
        guard phase == .idle, pendingPasteLast == nil else { return nil }
        pasteLastCount += 1
        pendingPasteLast = pasteLastCount
        return pasteLastCount
    }

    /// The "Paste last dictation" `id` has done what it was going to.
    public mutating func endPasteLast(_ id: Int) {
        if pendingPasteLast == id { pendingPasteLast = nil }
    }

    /// Whether the "Paste last dictation" `id` is still wanted: not dropped, no dictation under way, the screen unlocked.
    public func pasteLastIsCurrent(_ id: Int) -> Bool {
        pendingPasteLast == id && phase == .idle && !isLocked
    }

    private mutating func dropPasteLast() -> [SessionEffect] {
        guard pendingPasteLast != nil else { return [] }
        pendingPasteLast = nil
        return [.pasteLastDropped]
    }
}
