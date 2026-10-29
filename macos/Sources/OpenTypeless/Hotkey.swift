import AppKit
import os
import CoreGraphics
import TypelessCore

/// The dictation trigger: either a single modifier key held on its own (Fn, right ⌘ …) or a
/// key combination (⌥Space, ⌃⇧D, F5 …).
struct Hotkey: Codable, Equatable {
    var keyCode: Int64
    /// Required modifiers for a combination (subset of `Hotkey.comboModifiers`, raw `CGEventFlags`).
    var modifiers: UInt64 = 0
    var isModifierOnly: Bool
    /// Key name captured when recording a combination ("Space", "D", "F5").
    var keyLabel: String = ""

    static let fn = Hotkey(keyCode: 63, isModifierOnly: true)
    static let rightCommand = Hotkey(keyCode: 54, isModifierOnly: true)
    static let rightOption = Hotkey(keyCode: 61, isModifierOnly: true)
    static let rightControl = Hotkey(keyCode: 62, isModifierOnly: true)
    static let presets: [Hotkey] = [.fn, .rightCommand, .rightOption, .rightControl]

    static let comboModifiers: CGEventFlags = [.maskCommand, .maskAlternate, .maskControl, .maskShift]

    /// keyCode → the flag it toggles, for keys usable on their own.
    static let modifierKeys: [Int64: CGEventFlags] = [
        54: .maskCommand, 55: .maskCommand,
        58: .maskAlternate, 61: .maskAlternate,
        59: .maskControl, 62: .maskControl,
        56: .maskShift, 60: .maskShift,
        63: .maskSecondaryFn,
    ]

    static let functionKeys: [Int64: String] = [
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8",
        101: "F9", 109: "F10", 103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15",
        106: "F16", 64: "F17", 79: "F18", 80: "F19", 90: "F20",
    ]

    static let namedKeys: [Int64: String] = [
        49: "Space", 36: "↩", 48: "⇥", 51: "⌫", 117: "⌦", 123: "←", 124: "→", 125: "↓", 126: "↑",
        115: "↖", 119: "↘", 116: "⇞", 121: "⇟",
    ]

    /// keyCode → the device-dependent flags (NX_DEVICELCMDKEYMASK…) of its own key and of the same modifier's other key.
    static let sideFlags: [Int64: (own: UInt64, other: UInt64)] = [
        55: (0x08, 0x10), 54: (0x10, 0x08), // left / right ⌘
        58: (0x20, 0x40), 61: (0x40, 0x20), // left / right ⌥
        59: (0x01, 0x2000), 62: (0x2000, 0x01), // left / right ⌃
        56: (0x02, 0x04), 60: (0x04, 0x02), // left / right ⇧
    ]

    var modifierFlag: CGEventFlags { Hotkey.modifierKeys[keyCode] ?? [] }

    /// As shortcuts are compared (see `ShortcutMap`).
    var chord: KeyChord {
        KeyChord(keyCode: keyCode, modifiers: CGEventFlags(rawValue: modifiers).intersection(Hotkey.comboModifiers).rawValue,
                 isModifierOnly: isModifierOnly)
    }

    /// Whether a modifier-only hotkey's own key is down in `flags`. The shared ⌘ flag stays on while the other ⌘ is
    /// held, so with Right ⌘ as the hotkey and Left ⌘ down, letting go of Right ⌘ would go unseen: the side's own bit
    /// tells. Keys posted by software sometimes carry only the shared flag; then that is all there is to go by.
    func isHeld(in flags: CGEventFlags) -> Bool {
        guard flags.contains(modifierFlag) else { return false }
        guard let side = Hotkey.sideFlags[keyCode] else { return true } // Fn has no sides
        let raw = flags.rawValue
        return raw & side.own != 0 || raw & (side.own | side.other) == 0
    }

    /// Pieces shown as separate key caps, e.g. ["⌥", "Space"] or ["Fn"].
    var keyCaps: [String] {
        if isModifierOnly { return [Hotkey.modifierName(keyCode)] }
        let flags = CGEventFlags(rawValue: modifiers)
        var caps: [String] = []
        if flags.contains(.maskControl) { caps.append("⌃") }
        if flags.contains(.maskAlternate) { caps.append("⌥") }
        if flags.contains(.maskShift) { caps.append("⇧") }
        if flags.contains(.maskCommand) { caps.append("⌘") }
        caps.append(keyLabel)
        return caps
    }

    var displayName: String { keyCaps.joined(separator: isModifierOnly ? "" : " ") }

    /// ⌘+letter combos shadow everyday shortcuts (copy, paste…) system-wide.
    var shadowsCommonShortcut: Bool {
        !isModifierOnly && CGEventFlags(rawValue: modifiers).intersection(Hotkey.comboModifiers) == .maskCommand
    }

    static func modifierName(_ keyCode: Int64) -> String {
        let left = L("左", "Left ")
        let right = L("右", "Right ")
        switch keyCode {
        case 63: return "Fn 🌐"
        case 54: return right + "⌘"
        case 55: return left + "⌘"
        case 61: return right + "⌥"
        case 58: return left + "⌥"
        case 62: return right + "⌃"
        case 59: return left + "⌃"
        case 60: return right + "⇧"
        case 56: return left + "⇧"
        default: return "?"
        }
    }

    static func label(forKeyCode keyCode: Int64, characters: String?) -> String {
        if let name = functionKeys[keyCode] ?? namedKeys[keyCode] { return name }
        let chars = (characters ?? "").trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        return chars.isEmpty ? "#\(keyCode)" : chars
    }
}

extension ShortcutAction {
    var title: String {
        switch self {
        case .pasteLastDictation: return L("粘贴上一次听写", "Paste last dictation")
        }
    }

    var subtitle: String {
        switch self {
        case .pasteLastDictation:
            return L("把最近一次听写的文字再插入到光标处；如果它转写失败了，会先重新转写",
                     "Inserts your latest dictation at the cursor again; if it failed, it's transcribed again first")
        }
    }

    var symbol: String {
        switch self {
        case .pasteLastDictation: return "doc.on.clipboard"
        }
    }
}

extension TranslationChord {
    /// Its key cap, as the shortcut page shows it next to the hotkey's.
    var keyCap: String {
        switch self {
        case .shift: return "⇧"
        case .control: return "⌃"
        case .alt: return "⌥"
        }
    }

    /// As Settings names it.
    var label: String {
        switch self {
        case .shift: return "⇧ Shift"
        case .control: return "⌃ Control"
        case .alt: return "⌥ Option"
        }
    }
}

extension ShortcutConflict {
    var message: String {
        switch self {
        case .modifierOnly:
            return L("单独的修饰键不能用作这个快捷键，请再加一个键。", "A modifier on its own can't be used here — add a key to it.")
        case .escape:
            return L("Esc 用来取消听写，请换一个键。", "Esc cancels a dictation — pick another key.")
        case .dictation:
            return L("这是听写快捷键，请换一个。", "That's the dictation shortcut — pick another.")
        case let .action(other):
            return L("已被「\(other.title)」使用，请换一个。", "Already used by “\(other.title)” — pick another.")
        }
    }
}

/// Listens globally for the configured hotkey via a CGEventTap.
///
/// Uses a default (active) tap because that only needs Accessibility permission, which the app needs
/// anyway for pasting; a listen-only tap would also require Input Monitoring. Everything is passed
/// through untouched except the keystrokes of a key-combination hotkey, which are swallowed so that
/// e.g. ⌥Space doesn't also type a character into the focused app, Esc while a dictation is under way, and the keys
/// of an action's shortcut or of a mode chord.
///
/// The press, release and other-key callbacks get the key event's own time (`ProcessInfo.systemUptime` clock),
/// see `HotkeyPress`.
final class HotkeyMonitor {
    static let shared = HotkeyMonitor()

    var onPress: ((TimeInterval) -> Void)?
    var onRelease: ((TimeInterval) -> Void)?
    /// Another key went down while the hotkey was held, by its key code: with a modifier-only hotkey any key (e.g.
    /// Fn+F1 for brightness), with a key combination only a mode chord (see `modeChordKeys`). A modifier only counts as
    /// a mode chord.
    var onOtherKey: ((Int64, TimeInterval) -> Void)?
    var onEscape: (() -> Void)?
    /// An action's shortcut went down (see `shortcuts`).
    var onAction: ((ShortcutAction) -> Void)?

    /// Set while a dictation is recording or processing: Esc then cancels it and is kept from the focused app, where
    /// it would also interrupt a command-line AI tool or close a dialog. Main thread only, like the tap.
    var swallowsEscape = false

    /// The shortcuts of actions besides dictating. Their keys are always kept from the focused app, whatever the
    /// session makes of them. Main thread only, like the tap.
    var shortcuts = ShortcutMap()

    /// Keys that switch the recording's mode while the hotkey is held (see `ModeChords`); kept from the focused app then.
    var modeChordKeys: Set<Int64> = []

    var hotkey: Hotkey = .fn {
        didSet { isDown = false }
    }

    /// Set while the settings window records a new hotkey.
    var isSuspended = false {
        didSet { isDown = false }
    }

    private(set) var isRunning = false
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var retryTimer: Timer?
    private var isDown = false
    /// An Esc key-down was swallowed: its auto-repeats and its key-up are swallowed too, even once the dictation has
    /// ended, so the focused app never sees half a key press.
    private var escapeSwallowed = false
    /// Keys whose key-down was swallowed as an action's shortcut or a mode chord: their auto-repeats and key-up go too.
    private var swallowedKeys: Set<Int64> = []
    /// Modifiers whose press was swallowed as a mode chord: their release goes too, and until then they're taken out of
    /// the flags of every event let through, mouse events included (see `SuppressedModifiers`).
    private var suppressedModifiers = SuppressedModifiers()
    /// Mouse events go through this tap only while a modifier is suppressed, to have their flags cleared of it: there's
    /// nothing to do with them otherwise, and a tap on every mouse move would be the main thread's to keep up with.
    private var mouseTap: CFMachPort?

    /// Starts the tap, retrying every 2 s until Accessibility permission is granted.
    func start() {
        guard !isRunning else { return }
        if installTap() { return }
        retryTimer?.invalidate()
        retryTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] timer in
            if self?.installTap() == true { timer.invalidate() }
        }
    }

    private func installTap() -> Bool {
        let types: [CGEventType] = [.flagsChanged, .keyDown, .keyUp]
        let mask = types.reduce(CGEventMask(0)) { $0 | (1 << $1.rawValue) }
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                guard let refcon else { return Unmanaged.passUnretained(event) }
                let monitor = Unmanaged<HotkeyMonitor>.fromOpaque(refcon).takeUnretainedValue()
                if monitor.handle(type: type, event: event) { return nil }
                monitor.stripSuppressedModifiers(from: event, type: type)
                return Unmanaged.passUnretained(event)
            },
            userInfo: refcon
        ) else { return false }

        self.tap = tap
        source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        installMouseTap()
        isRunning = true
        return true
    }

    /// The tap that clears suppressed modifiers from mouse events (a ⇧-click extends a selection, ⇧-scrolling goes
    /// sideways), off until a modifier is suppressed. Without it (not created), only the key events' flags are cleared.
    private func installMouseTap() {
        let types: [CGEventType] = [.leftMouseDown, .leftMouseUp, .leftMouseDragged, .rightMouseDown, .rightMouseUp,
                                    .rightMouseDragged, .otherMouseDown, .otherMouseUp, .otherMouseDragged, .mouseMoved,
                                    .scrollWheel]
        let mask = types.reduce(CGEventMask(0)) { $0 | (1 << $1.rawValue) }
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                if let refcon {
                    Unmanaged<HotkeyMonitor>.fromOpaque(refcon).takeUnretainedValue()
                        .stripSuppressedModifiers(from: event, type: type)
                }
                return Unmanaged.passUnretained(event)
            },
            userInfo: Unmanaged.passUnretained(self).toOpaque()
        ) else { return }
        mouseTap = tap
        CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0), .commonModes)
        CGEvent.tapEnable(tap: tap, enable: false)
    }

    /// Clears the suppressed modifiers from an event let through; turns the mouse tap back on when macOS turned it off
    /// while one is still suppressed.
    private func stripSuppressedModifiers(from event: CGEvent, type: CGEventType) {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            updateMouseTap()
            return
        }
        guard !suppressedModifiers.isEmpty else { return }
        event.flags = CGEventFlags(rawValue: suppressedModifiers.strip(event.flags.rawValue))
    }

    /// The mouse tap is on exactly while a modifier is suppressed.
    private func updateMouseTap() {
        guard let mouseTap else { return }
        let wanted = !suppressedModifiers.isEmpty
        if CGEvent.tapIsEnabled(tap: mouseTap) != wanted { CGEvent.tapEnable(tap: mouseTap, enable: wanted) }
    }

    /// Returns true when the event should be swallowed.
    private func handle(type: CGEventType, event: CGEvent) -> Bool {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            // The system disables slow taps; turn it straight back on.
            HotkeyLog.debug("⚠️ event tap was disabled by macOS (\(type == .tapDisabledByTimeout ? "timeout" : "user input")) — re-enabled")
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            resyncAfterOutage()
            return false
        }
        // Keys this app posts itself (a paste's ⌘V, ⌘C, Return) are never the hotkey or a shortcut, even when one is set
        // to the same keys, and leave the bookkeeping of the user's own keys alone.
        if event.getIntegerValueField(.eventSourceUserData) == TextInserter.eventSignature { return false }
        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        // First of all: whatever changed since a press was swallowed (a shortcut being recorded, the hotkey or the mode
        // chords changed in Settings), the rest of it is swallowed too, or the focused app gets half a key press.
        if let swallowed = restOfSwallowedPress(type: type, keyCode: keyCode, event: event) { return swallowed }
        if isSuspended {
            if type == .keyDown, keepsFromApp(keyCode, event: event) { return true }
            if type == .flagsChanged, keyCode == hotkey.keyCode {
                HotkeyLog.debug("ignored \(Hotkey.modifierName(keyCode)) — recording a new shortcut")
            }
            return false
        }

        switch type {
        case .flagsChanged:
            if keyCode != hotkey.keyCode, modeChordKeys.contains(keyCode) { return handleModifierChord(keyCode, event: event) }
            guard hotkey.isModifierOnly, keyCode == hotkey.keyCode else { return false }
            let down = hotkey.isHeld(in: event.flags)
            if down != isDown {
                isDown = down
                HotkeyLog.debug("\(hotkey.displayName) \(down ? "down" : "up") · \(HotkeyLog.describe(event))")
                fire(down ? onPress : onRelease, at: Self.time(of: event))
            } else {
                HotkeyLog.debug("\(hotkey.displayName) \(down ? "down" : "up") repeated, ignored · \(HotkeyLog.describe(event))")
            }
            return false

        case .keyDown:
            if !hotkey.isModifierOnly, keyCode == hotkey.keyCode {
                let flags = event.flags.intersection(Hotkey.comboModifiers)
                let wanted = CGEventFlags(rawValue: hotkey.modifiers).intersection(Hotkey.comboModifiers)
                if flags == wanted || isDown {
                    if !isDown, event.getIntegerValueField(.keyboardEventAutorepeat) == 0 {
                        isDown = true
                        // Also swallowed: its key-up, should the hotkey change or recording a new one start first.
                        swallowedKeys.insert(keyCode)
                        HotkeyLog.debug("\(hotkey.displayName) down · \(HotkeyLog.describe(event))")
                        fire(onPress, at: Self.time(of: event))
                    }
                    return true
                }
            }
            let repeated = event.getIntegerValueField(.keyboardEventAutorepeat) != 0
            // A new press: a key-up missed meanwhile (while recording a shortcut, say) mustn't swallow this one's.
            if !repeated { swallowedKeys.remove(keyCode) }
            if keyCode == 53 { // Escape
                if repeated { return false } // one swallowed went above
                fire(onEscape)
                escapeSwallowed = swallowsEscape
                return escapeSwallowed || keepsFromApp(keyCode, event: event)
            } else if isDown, hotkey.isModifierOnly {
                // A real combo (Fn+F1, right ⌘+Tab…) carries the hotkey's modifier flag on the key event.
                // Keys injected by other apps at the same moment — e.g. Logi Options+ sending its own
                // shortcut for the mouse button that MasterKey turns into Fn — don't, and must not cancel.
                if event.flags.contains(hotkey.modifierFlag) {
                    HotkeyLog.debug("combo key (code \(keyCode)) with \(hotkey.displayName) · \(HotkeyLog.describe(event))")
                    fire(onOtherKey, key: keyCode, at: Self.time(of: event))
                    if modeChordKeys.contains(keyCode) {
                        swallowedKeys.insert(keyCode)
                        return true
                    }
                } else {
                    HotkeyLog.debug("ignored key (code \(keyCode)) without \(hotkey.displayName) flag · \(HotkeyLog.describe(event))")
                }
            } else if isDown, modeChordKeys.contains(keyCode), !repeated {
                // A key-combination hotkey held: other keys are left alone, as they always were, but a chord counts.
                HotkeyLog.debug("chord key (code \(keyCode)) with \(hotkey.displayName)")
                fire(onOtherKey, key: keyCode, at: Self.time(of: event))
                swallowedKeys.insert(keyCode)
                return true
            }
            // After the combo above, which cancels a dictation the shortcut's modifier may have just started.
            if let action = shortcuts.action(keyCode: keyCode, modifiers: event.flags.intersection(Hotkey.comboModifiers).rawValue) {
                if !repeated {
                    HotkeyLog.debug("\(action.rawValue) shortcut · \(HotkeyLog.describe(event))")
                    fire(onAction, action)
                }
                swallowedKeys.insert(keyCode)
                return true
            }
            // Reported above when it makes a combination (which cancels the dictation), but not let through either.
            return keepsFromApp(keyCode, event: event)

        case .keyUp:
            if !hotkey.isModifierOnly, keyCode == hotkey.keyCode, isDown {
                isDown = false
                swallowedKeys.remove(keyCode)
                HotkeyLog.debug("\(hotkey.displayName) up")
                fire(onRelease, at: Self.time(of: event))
                return true
            }
            return false

        default:
            return false
        }
    }

    /// A key going down while a suppressed modifier is held: swallowed, its repeats and release with it, since it would
    /// reach the app without that modifier's flags, as another shortcut (see `HotkeyPress.keepsFromApp`).
    private func keepsFromApp(_ keyCode: Int64, event: CGEvent) -> Bool {
        guard HotkeyPress.keepsFromApp(keyCode, isRepeat: event.getIntegerValueField(.keyboardEventAutorepeat) != 0,
                                       isModifier: Hotkey.modifierKeys[keyCode] != nil, chordKeys: modeChordKeys,
                                       chordModifierSwallowed: !suppressedModifiers.isEmpty) else { return false }
        HotkeyLog.debug("key (code \(keyCode)) kept from the app while a chord modifier is held · \(HotkeyLog.describe(event))")
        swallowedKeys.insert(keyCode)
        return true
    }

    /// The auto-repeats and release of a key whose press was swallowed, and the release of a suppressed modifier: true
    /// (swallowed). nil for any other event, including a new press of a suppressed modifier, whose last release went by
    /// while the tap was off: it's no longer suppressed, and is read as any press.
    private func restOfSwallowedPress(type: CGEventType, keyCode: Int64, event: CGEvent) -> Bool? {
        switch type {
        case .flagsChanged:
            guard suppressedModifiers.contains(keyCode) else { return nil }
            suppressedModifiers.remove(keyCode)
            updateMouseTap()
            return Hotkey(keyCode: keyCode, isModifierOnly: true).isHeld(in: event.flags) ? nil : true
        case .keyDown:
            guard event.getIntegerValueField(.keyboardEventAutorepeat) != 0 else { return nil }
            if keyCode == 53, escapeSwallowed { return true }
            return swallowedKeys.contains(keyCode) ? true : nil
        case .keyUp:
            if keyCode == 53, escapeSwallowed {
                escapeSwallowed = false
                return true
            }
            // The key-combination hotkey's own release is read below.
            if !hotkey.isModifierOnly, keyCode == hotkey.keyCode, isDown { return nil }
            return swallowedKeys.remove(keyCode) != nil ? true : nil
        default:
            return nil
        }
    }

    /// A modifier registered as a mode chord (Shift, for translation) went down or up. Pressed while the hotkey is held,
    /// it switches the recording's mode, and its press and release are kept from the focused app, where Shift on its own
    /// would switch a Chinese or Japanese input method between its own letters and English; until it's let go, it's
    /// suppressed from the flags of everything else (see `suppressedModifiers`). Otherwise it's an ordinary modifier. Its
    /// own side's flag says which way it went (see `Hotkey.isHeld`); a suppressed one's release was read above.
    private func handleModifierChord(_ keyCode: Int64, event: CGEvent) -> Bool {
        guard isDown, Hotkey(keyCode: keyCode, isModifierOnly: true).isHeld(in: event.flags) else { return false }
        HotkeyLog.debug("chord key (code \(keyCode)) with \(hotkey.displayName) · \(HotkeyLog.describe(event))")
        fire(onOtherKey, key: keyCode, at: Self.time(of: event))
        suppressedModifiers.insert(keyCode)
        updateMouseTap()
        return true
    }

    /// While the tap was off its events were lost, the hotkey's release among them: a hotkey still marked down that no
    /// longer is gets its release now, or the recording would go on as if the key were held. Swallowed presses let go of
    /// meanwhile are forgotten, and a suppressed modifier no longer held is no longer cleared from what goes through.
    private func resyncAfterOutage() {
        suppressedModifiers.keepHeld(in: CGEventSource.flagsState(.hidSystemState).rawValue)
        updateMouseTap()
        swallowedKeys = swallowedKeys.filter { CGEventSource.keyState(.hidSystemState, key: CGKeyCode($0)) }
        if escapeSwallowed, !CGEventSource.keyState(.hidSystemState, key: 53) { escapeSwallowed = false }
        guard isDown, !isSuspended else { return }
        let held = hotkey.isModifierOnly
            ? hotkey.isHeld(in: CGEventSource.flagsState(.hidSystemState))
            : CGEventSource.keyState(.hidSystemState, key: CGKeyCode(hotkey.keyCode))
        guard !held else { return }
        isDown = false
        HotkeyLog.debug("\(hotkey.displayName) was released while the tap was off — released now")
        fire(onRelease, at: ProcessInfo.processInfo.systemUptime)
    }

    /// Waits until none of `shortcut`'s keys is physically held any more (its key, and any of ⌃⌥⇧⌘), so that what is
    /// sent next isn't read together with them: a posted ⌘V carries its own flags, but some apps go by the modifiers
    /// held at the time. Returns false when they're still held after `timeout`.
    static func waitForRelease(of shortcut: Hotkey, timeout: TimeInterval = 3) async -> Bool {
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while !Task.isCancelled {
            let modifiers = CGEventSource.flagsState(.hidSystemState).intersection(Hotkey.comboModifiers)
            let keyHeld = !shortcut.isModifierOnly && CGEventSource.keyState(.hidSystemState, key: CGKeyCode(shortcut.keyCode))
            if modifiers.isEmpty, !keyHeld { return true }
            if ProcessInfo.processInfo.systemUptime >= deadline { return false }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        return false
    }

    /// When the key event happened, on the `ProcessInfo.systemUptime` clock (NSEvent converts the event's stamp).
    private static func time(of event: CGEvent) -> TimeInterval {
        HotkeyPress.eventTime(NSEvent(cgEvent: event)?.timestamp ?? 0, now: ProcessInfo.processInfo.systemUptime)
    }

    private func fire(_ callback: (() -> Void)?) {
        DispatchQueue.main.async { callback?() }
    }

    private func fire(_ callback: ((TimeInterval) -> Void)?, at time: TimeInterval) {
        DispatchQueue.main.async { callback?(time) }
    }

    private func fire(_ callback: ((Int64, TimeInterval) -> Void)?, key: Int64, at time: TimeInterval) {
        DispatchQueue.main.async { callback?(key, time) }
    }

    private func fire(_ callback: ((ShortcutAction) -> Void)?, _ action: ShortcutAction) {
        DispatchQueue.main.async { callback?(action) }
    }
}

/// Debug-level log of hotkey events (not persisted; view with
/// `log stream --level debug --predicate 'subsystem == "local.opentypeless.app"'`).
enum HotkeyLog {
    private static let logger = Logger(subsystem: "local.opentypeless.app", category: "hotkey")

    static func debug(_ text: String) {
        logger.debug("\(text, privacy: .public)")
    }

    static func describe(_ event: CGEvent) -> String {
        let pid = event.getIntegerValueField(.eventSourceUnixProcessID)
        let name = pid > 0 ? (NSRunningApplication(processIdentifier: pid_t(pid))?.localizedName ?? "pid \(pid)") : "HID"
        return String(format: "flags=0x%llx from %@", event.flags.rawValue, name)
    }
}
