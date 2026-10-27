import AppKit
import ApplicationServices
import Carbon
import TypelessCore

/// Reads the text selected in the app in front, for editing it by voice (see `SelectionReader` for the order): from
/// Accessibility; else by pressing the app's own Edit ▸ Copy through Accessibility, as SelectedTextKit's menu action
/// does, which sends no keys, so neither the keyboard layout nor the hotkey and chord still held change what it does;
/// and last with ⌘C, once those keys are let go of. Both copies put the clipboard back as it was.
@MainActor
enum SelectedText {
    /// Where a selection was read: the app, its focused window and the element with keyboard focus (nil where
    /// Accessibility doesn't say). The edit goes back only while all three are still the same (see `isInFront`).
    @MainActor
    struct Place {
        let app: NSRunningApplication
        let window: AXUIElement?
        let element: AXUIElement?

        /// The app in front now, unless it's this one: its own windows (Settings, say) aren't edited.
        static func front() -> Place? {
            guard let app = NSWorkspace.shared.frontmostApplication,
                  app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return nil }
            let pid = app.processIdentifier
            return Place(app: app, window: SelectedText.focusedWindow(pid: pid),
                         element: SelectedText.focusedElement(pid: pid))
        }

        /// Whether the same app is still in front, in the same window, with the same element focused (each where it's
        /// known both then and now).
        var isInFront: Bool {
            let pid = app.processIdentifier
            guard NSWorkspace.shared.frontmostApplication?.processIdentifier == pid else { return false }
            if let window, let now = SelectedText.focusedWindow(pid: pid), !CFEqual(window, now) { return false }
            if let element, let now = SelectedText.focusedElement(pid: pid), !CFEqual(element, now) { return false }
            return true
        }
    }

    /// The selection at `place`. `hotkey`: the dictation hotkey, which with the chord and every other modifier is let go
    /// of before ⌘C is sent, since some apps read the modifiers held at the time rather than the event's own; it waits
    /// for up to `releaseTimeout` (as long as the recording may last), and gives up when the task is cancelled.
    static func read(at place: Place, releasing hotkey: Hotkey, releaseTimeout: TimeInterval) async -> SelectionRead {
        let pid = place.app.processIdentifier
        let element = place.element
        return await SelectionReader.read(
            app: SelectionApp.mac(bundleID: place.app.bundleIdentifier),
            isSecure: element.map(isSecure) ?? false,
            accessibility: { element.map(selectedText) ?? .unavailable },
            menuCopy: {
                // Secure input on (a password being typed somewhere): nothing is copied.
                guard !IsSecureEventInputEnabled() else { return .unavailable }
                return await menuCopy(pid: pid)
            },
            copyShortcut: {
                guard await HotkeyMonitor.waitForRelease(of: hotkey, timeout: releaseTimeout), !Task.isCancelled,
                      place.isInFront, !IsSecureEventInputEnabled() else { return .unavailable }
                return await TextInserter.copySelection().map { .text($0) } ?? .unavailable
            })
    }

    /// The selection in `place`'s app as Accessibility reports it now, to check it's still the one read; nil when it
    /// can't tell.
    static func selection(at place: Place) -> String? {
        guard let element = focusedElement(pid: place.app.processIdentifier) else { return nil }
        switch selectedText(element) {
        case let .text(text): return text
        case .empty: return ""
        case .unavailable: return nil
        }
    }

    // MARK: Accessibility

    private static func focusedElement(pid: pid_t) -> AXUIElement? {
        guard AXIsProcessTrusted() else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.25)
        guard let element = value(app, kAXFocusedUIElementAttribute).flatMap(asElement) else { return nil }
        AXUIElementSetMessagingTimeout(element, 0.25)
        return element
    }

    private static func focusedWindow(pid: pid_t) -> AXUIElement? {
        guard AXIsProcessTrusted() else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.25)
        return value(app, kAXFocusedWindowAttribute).flatMap(asElement)
    }

    private static func selectedText(_ element: AXUIElement) -> SelectionProbe {
        guard let text = value(element, kAXSelectedTextAttribute) as? String else { return .unavailable }
        return text.isEmpty ? .empty : .text(text)
    }

    private static func isSecure(_ element: AXUIElement) -> Bool {
        (value(element, kAXSubroleAttribute) as? String) == kAXSecureTextFieldSubrole
    }

    // MARK: Edit ▸ Copy

    /// Presses the app's Copy menu item. Found by its shortcut, ⌘C, not by its name, which depends on the app's language.
    /// A disabled one means nothing is selected; none found, or nothing copied in time, can't tell.
    private static func menuCopy(pid: pid_t) async -> SelectionProbe {
        guard AXIsProcessTrusted(), let item = copyMenuItem(pid: pid) else { return .unavailable }
        if (value(item, kAXEnabledAttribute) as? Bool) == false { return .empty }
        let copied = await TextInserter.copySelection(by: {
            AXUIElementPerformAction(item, kAXPressAction as CFString) == .success
        })
        return copied.map { .text($0) } ?? .unavailable
    }

    /// The item with ⌘C as its shortcut in one of the menu bar's menus (the Edit menu, in practice), looking no deeper
    /// than the menus themselves and for no longer than half a second: an app slow to answer can't hold the edit up.
    private static func copyMenuItem(pid: pid_t) -> AXUIElement? {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.25)
        guard let bar = value(app, kAXMenuBarAttribute).flatMap(asElement) else { return nil }
        let deadline = ProcessInfo.processInfo.systemUptime + 0.5
        // The first is the Apple menu.
        for title in children(bar).dropFirst() {
            guard let menu = children(title).first else { continue }
            for item in children(menu) {
                if ProcessInfo.processInfo.systemUptime > deadline { return nil }
                guard (value(item, kAXMenuItemCmdCharAttribute) as? String)?.uppercased() == "C",
                      (value(item, kAXMenuItemCmdModifiersAttribute) as? Int) == 0 else { continue }
                return item
            }
        }
        return nil
    }

    private static func children(_ element: AXUIElement) -> [AXUIElement] {
        AXUIElementSetMessagingTimeout(element, 0.25)
        return (value(element, kAXChildrenAttribute) as? [AnyObject] ?? []).compactMap(asElement)
    }

    private static func value(_ element: AXUIElement, _ attribute: String) -> AnyObject? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value
    }

    private static func asElement(_ value: AnyObject) -> AXUIElement? {
        CFGetTypeID(value) == AXUIElementGetTypeID() ? (value as! AXUIElement) : nil
    }
}
