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

/// Listens globally for the configured hotkey via a CGEventTap.
///
/// Uses a default (active) tap because that only needs Accessibility permission, which the app needs
/// anyway for pasting; a listen-only tap would also require Input Monitoring. Everything is passed
/// through untouched except the keystrokes of a key-combination hotkey, which are swallowed so that
/// e.g. ⌥Space doesn't also type a character into the focused app, and Esc while a dictation is under way.
///
/// The press, release and other-key callbacks get the key event's own time (`ProcessInfo.systemUptime` clock),
/// see `HotkeyPress`.
final class HotkeyMonitor {
    static let shared = HotkeyMonitor()

    var onPress: ((TimeInterval) -> Void)?
    var onRelease: ((TimeInterval) -> Void)?
    /// Another key went down while a modifier-only hotkey was held (e.g. Fn+F1 for brightness).
    var onOtherKey: ((TimeInterval) -> Void)?
    var onEscape: (() -> Void)?

    /// Set while a dictation is recording or processing: Esc then cancels it and is kept from the focused app, where
    /// it would also interrupt a command-line AI tool or close a dialog. Main thread only, like the tap.
    var swallowsEscape = false

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
                if let refcon,
                   Unmanaged<HotkeyMonitor>.fromOpaque(refcon).takeUnretainedValue().handle(type: type, event: event) {
                    return nil
                }
                return Unmanaged.passUnretained(event)
            },
            userInfo: refcon
        ) else { return false }

        self.tap = tap
        source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        isRunning = true
        return true
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
        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        if isSuspended {
            if type == .flagsChanged, keyCode == hotkey.keyCode {
                HotkeyLog.debug("ignored \(Hotkey.modifierName(keyCode)) — recording a new shortcut")
            }
            return false
        }

        switch type {
        case .flagsChanged:
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
                        HotkeyLog.debug("\(hotkey.displayName) down · \(HotkeyLog.describe(event))")
                        fire(onPress, at: Self.time(of: event))
                    }
                    return true
                }
            }
            if keyCode == 53 { // Escape
                if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 { return escapeSwallowed }
                fire(onEscape)
                escapeSwallowed = swallowsEscape
                return escapeSwallowed
            } else if isDown, hotkey.isModifierOnly {
                // A real combo (Fn+F1, right ⌘+Tab…) carries the hotkey's modifier flag on the key event.
                // Keys injected by other apps at the same moment — e.g. Logi Options+ sending its own
                // shortcut for the mouse button that MasterKey turns into Fn — don't, and must not cancel.
                if event.flags.contains(hotkey.modifierFlag) {
                    HotkeyLog.debug("combo key (code \(keyCode)) with \(hotkey.displayName) · \(HotkeyLog.describe(event))")
                    fire(onOtherKey, at: Self.time(of: event))
                } else {
                    HotkeyLog.debug("ignored key (code \(keyCode)) without \(hotkey.displayName) flag · \(HotkeyLog.describe(event))")
                }
            }
            return false

        case .keyUp:
            if !hotkey.isModifierOnly, keyCode == hotkey.keyCode, isDown {
                isDown = false
                HotkeyLog.debug("\(hotkey.displayName) up")
                fire(onRelease, at: Self.time(of: event))
                return true
            }
            if keyCode == 53, escapeSwallowed {
                escapeSwallowed = false
                return true
            }
            return false

        default:
            return false
        }
    }

    /// While the tap was off its events were lost, the hotkey's release among them: a hotkey still marked down that no
    /// longer is gets its release now, or the recording would go on as if the key were held.
    private func resyncAfterOutage() {
        guard isDown, !isSuspended else { return }
        let held = hotkey.isModifierOnly
            ? hotkey.isHeld(in: CGEventSource.flagsState(.hidSystemState))
            : CGEventSource.keyState(.hidSystemState, key: CGKeyCode(hotkey.keyCode))
        guard !held else { return }
        isDown = false
        HotkeyLog.debug("\(hotkey.displayName) was released while the tap was off — released now")
        fire(onRelease, at: ProcessInfo.processInfo.systemUptime)
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
