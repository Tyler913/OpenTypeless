import AppKit
import ApplicationServices
import Carbon
import TypelessCore

/// Reads the text selected in the app in front, for editing it by voice (see `SelectionReader` for the order): from
/// Accessibility; else by pressing the app's own Edit ▸ Copy through Accessibility, as SelectedTextKit's menu action
/// does, which sends no keys, so neither the keyboard layout nor the hotkey and chord still held change what it does;
/// and last with ⌘C, once those keys are let go of. Both copies put the clipboard back as it was.
///
/// Accessibility calls go into the other app and wait for it, so they run off the main thread, where the hotkey's event
/// tap lives, each batch with a deadline (see `offMain`): an app slow to answer can't hold the user's keys up.
@MainActor
enum SelectedText {
    /// An Accessibility element, handed between threads (AXUIElement is thread-safe, but not marked so).
    struct Element: @unchecked Sendable {
        let ref: AXUIElement
    }

    /// Where a selection was read: the app, its focused window, the window's title (a browser's tab), and the element
    /// with keyboard focus (nil where Accessibility doesn't say). The edit goes back only while they're all still the
    /// same (see `isInFront`).
    struct Place: Sendable {
        let pid: pid_t
        let bundleID: String?
        let window: Element?
        let title: String?
        let element: Element?

        /// Where `app` (the app in front) is, unless it's this one: its own windows (Settings, say) aren't edited.
        static func front(_ app: NSRunningApplication?) async -> Place? {
            guard let app, app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return nil }
            let pid = app.processIdentifier
            let bundleID = app.bundleIdentifier
            let found = await SelectedText.offMain(fallback: (nil, nil, nil)) { () -> (Element?, String?, Element?) in
                let window = SelectedText.focusedWindow(pid: pid)
                return (window, window.flatMap(SelectedText.title), SelectedText.focusedElement(pid: pid))
            }
            return Place(pid: pid, bundleID: bundleID, window: found.0, title: found.1, element: found.2)
        }

        /// Whether the same app is still in front, in the same window with the same title, with the same element
        /// focused (each where it's known both then and now; when Accessibility doesn't answer in time, it isn't).
        func isInFront() async -> Bool {
            guard NSWorkspace.shared.frontmostApplication?.processIdentifier == pid else { return false }
            let place = self
            let same = await SelectedText.offMain(fallback: false) { () -> Bool in
                let window = SelectedText.focusedWindow(pid: place.pid)
                if let then = place.window, let window, !CFEqual(then.ref, window.ref) { return false }
                if let then = place.title, let window, let now = SelectedText.title(window), now != then { return false }
                if let then = place.element, let now = SelectedText.focusedElement(pid: place.pid), !CFEqual(then.ref, now.ref) {
                    return false
                }
                return true
            }
            return same && NSWorkspace.shared.frontmostApplication?.processIdentifier == pid
        }
    }

    /// How many clicks and keys had reached apps (not this one's own) at some moment, to tell whether any did since
    /// (see `SelectionCommand.delivery`): one may have moved the selection within the same field.
    struct InputMark: Equatable {
        let keys: Int
        let clicks: UInt32

        static func now() -> InputMark {
            let clicks = [CGEventType.leftMouseDown, .rightMouseDown, .otherMouseDown].reduce(UInt32(0)) {
                $0 &+ CGEventSource.counterForEventType(.combinedSessionState, eventType: $1)
            }
            return InputMark(keys: HotkeyMonitor.shared.keysLetThrough, clicks: clicks)
        }
    }

    /// The selection at `place`. `hotkey`: the dictation hotkey, which with the chord and every other modifier is let go
    /// of before ⌘C is sent, since some apps read the modifiers held at the time rather than the event's own; it waits
    /// for up to `releaseTimeout` (as long as the recording may last), and gives up when the task is cancelled.
    static func read(at place: Place, releasing hotkey: Hotkey, releaseTimeout: TimeInterval) async -> SelectionRead {
        let pid = place.pid
        let element = place.element
        let secure = await offMain(fallback: false) { element.map { isSecure($0.ref) } ?? false }
        return await SelectionReader.read(
            app: SelectionApp.mac(bundleID: place.bundleID),
            isSecure: secure,
            accessibility: {
                guard let element else { return .unavailable }
                return await offMain(fallback: .unavailable) { selectedText(element.ref) }
            },
            menuCopy: {
                // Secure input on (a password being typed somewhere): nothing is copied.
                guard !IsSecureEventInputEnabled() else { return .unavailable }
                return await menuCopy(pid: pid)
            },
            copyShortcut: {
                guard await HotkeyMonitor.waitForRelease(of: hotkey, timeout: releaseTimeout), !Task.isCancelled,
                      await place.isInFront(), !IsSecureEventInputEnabled() else { return .unavailable }
                return await TextInserter.copySelection().map { .text($0) } ?? .unavailable
            })
    }

    /// The selection in `place`'s app as Accessibility reports it now, to check it's still the one read; nil when it
    /// can't tell.
    static func selection(at place: Place) async -> String? {
        let pid = place.pid
        return await offMain(fallback: nil) { () -> String? in
            guard let element = focusedElement(pid: pid) else { return nil }
            switch selectedText(element.ref) {
            case let .text(text): return text
            case .empty: return ""
            case .unavailable: return nil
            }
        }
    }

    // MARK: Off the main thread

    /// Runs `work` on a background queue, and gives up on it (`fallback`) after `seconds`. Accessibility calls can't be
    /// cancelled: one that's late just finishes there, unheard.
    nonisolated static func offMain<T: Sendable>(seconds: Double = 0.6, fallback: T,
                                                 _ work: @escaping @Sendable () -> T) async -> T {
        await withCheckedContinuation { (continuation: CheckedContinuation<T, Never>) in
            let once = Once(continuation)
            DispatchQueue.global(qos: .userInitiated).async { once.resume(work()) }
            DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + seconds) { once.resume(fallback) }
        }
    }

    /// Resumes a continuation once, whichever comes first.
    private final class Once<T: Sendable>: @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: CheckedContinuation<T, Never>?

        init(_ continuation: CheckedContinuation<T, Never>) { self.continuation = continuation }

        func resume(_ value: T) {
            let pending = lock.withLock { () -> CheckedContinuation<T, Never>? in
                defer { continuation = nil }
                return continuation
            }
            pending?.resume(returning: value)
        }
    }

    // MARK: Accessibility (any thread)

    nonisolated private static func focusedElement(pid: pid_t) -> Element? {
        guard AXIsProcessTrusted() else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.25)
        guard let element = value(app, kAXFocusedUIElementAttribute).flatMap(asElement) else { return nil }
        AXUIElementSetMessagingTimeout(element, 0.25)
        return Element(ref: element)
    }

    nonisolated private static func focusedWindow(pid: pid_t) -> Element? {
        guard AXIsProcessTrusted() else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.25)
        guard let window = value(app, kAXFocusedWindowAttribute).flatMap(asElement) else { return nil }
        AXUIElementSetMessagingTimeout(window, 0.25)
        return Element(ref: window)
    }

    nonisolated private static func title(_ window: Element) -> String? {
        value(window.ref, kAXTitleAttribute) as? String
    }

    nonisolated private static func selectedText(_ element: AXUIElement) -> SelectionProbe {
        guard let text = value(element, kAXSelectedTextAttribute) as? String else { return .unavailable }
        return text.isEmpty ? .empty : .text(text)
    }

    nonisolated private static func isSecure(_ element: AXUIElement) -> Bool {
        (value(element, kAXSubroleAttribute) as? String) == kAXSecureTextFieldSubrole
    }

    // MARK: Edit ▸ Copy

    /// Presses the app's Copy menu item. Found by its shortcut, ⌘C, not by its name, which depends on the app's language.
    /// A disabled one means nothing is selected; none found, or nothing copied in time, can't tell.
    private static func menuCopy(pid: pid_t) async -> SelectionProbe {
        let found = await offMain(fallback: (nil, nil)) { () -> (Element?, Bool?) in
            guard AXIsProcessTrusted(), let item = copyMenuItem(pid: pid) else { return (nil, nil) }
            return (item, value(item.ref, kAXEnabledAttribute) as? Bool)
        }
        guard let item = found.0 else { return .unavailable }
        if found.1 == false { return .empty }
        let copied = await TextInserter.copySelection(by: {
            await offMain(fallback: false) { AXUIElementPerformAction(item.ref, kAXPressAction as CFString) == .success }
        })
        return copied.map { .text($0) } ?? .unavailable
    }

    /// The item with ⌘C as its shortcut in one of the menu bar's menus (the Edit menu, in practice), looking no deeper
    /// than the menus themselves and for no longer than half a second: an app slow to answer can't hold the edit up.
    nonisolated private static func copyMenuItem(pid: pid_t) -> Element? {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.25)
        guard let bar = value(app, kAXMenuBarAttribute).flatMap(asElement) else { return nil }
        AXUIElementSetMessagingTimeout(bar, 0.25)
        let deadline = ProcessInfo.processInfo.systemUptime + 0.5
        // The first is the Apple menu.
        for title in children(bar).dropFirst() {
            if ProcessInfo.processInfo.systemUptime > deadline { return nil }
            guard let menu = children(title).first else { continue }
            for item in children(menu) {
                if ProcessInfo.processInfo.systemUptime > deadline { return nil }
                guard (value(item, kAXMenuItemCmdCharAttribute) as? String)?.uppercased() == "C",
                      (value(item, kAXMenuItemCmdModifiersAttribute) as? Int) == 0 else { continue }
                return Element(ref: item)
            }
        }
        return nil
    }

    /// The element's children, each with the same short timeout as the element.
    nonisolated private static func children(_ element: AXUIElement) -> [AXUIElement] {
        let children = (value(element, kAXChildrenAttribute) as? [AnyObject] ?? []).compactMap(asElement)
        for child in children { AXUIElementSetMessagingTimeout(child, 0.25) }
        return children
    }

    nonisolated private static func value(_ element: AXUIElement, _ attribute: String) -> AnyObject? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value
    }

    nonisolated private static func asElement(_ value: AnyObject) -> AXUIElement? {
        CFGetTypeID(value) == AXUIElementGetTypeID() ? (value as! AXUIElement) : nil
    }
}
