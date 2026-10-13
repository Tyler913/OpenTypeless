import AppKit
import ApplicationServices
import Carbon.HIToolbox

/// Asks the Accessibility API what currently has keyboard focus, to decide between pasting at the
/// cursor and leaving the text on the clipboard.
enum FocusProbe {
    enum Target: Equatable {
        /// A text field, text view or editable web content: paste there.
        case editable
        /// Focus is clearly on something that can't take text (a list, button, the desktop…).
        case notEditable
        /// Can't tell — e.g. Electron/web apps that don't expose their accessibility tree.
        case unknown
    }

    private static let textRoles: Set<String> = [
        kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole, "AXSearchField",
    ]

    private static let nonTextRoles: Set<String> = [
        kAXButtonRole, kAXCheckBoxRole, kAXRadioButtonRole, kAXPopUpButtonRole, kAXMenuButtonRole,
        kAXListRole, kAXOutlineRole, kAXTableRole, kAXRowRole, kAXColumnRole, kAXCellRole, kAXBrowserRole,
        kAXImageRole, kAXMenuRole, kAXMenuBarRole, kAXMenuItemRole, kAXToolbarRole, kAXSplitGroupRole,
        kAXScrollAreaRole, kAXWindowRole, kAXSheetRole, kAXApplicationRole, kAXStaticTextRole, "AXLink",
        kAXSliderRole, kAXTabGroupRole, kAXDisclosureTriangleRole, kAXLayoutAreaRole,
    ]

    static func focusedTarget() -> Target {
        guard AXIsProcessTrusted() else { return .unknown }
        let system = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(system, 0.3)

        var value: CFTypeRef?
        let result = AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &value)
        guard result == .success, let value, CFGetTypeID(value) == AXUIElementGetTypeID() else {
            // Nothing focused on the desktop / in Finder; elsewhere an error usually means the app
            // simply doesn't expose accessibility, so don't assume.
            let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier
            return front == "com.apple.finder" ? .notEditable : .unknown
        }
        let element = value as! AXUIElement
        AXUIElementSetMessagingTimeout(element, 0.3)

        let role = string(element, kAXRoleAttribute) ?? ""
        if textRoles.contains(role) { return .editable }
        // Inside editable web content (contenteditable, rich editors) WebKit and Chromium expose these.
        if has(element, "AXEditableAncestor") || has(element, "AXHighestEditableAncestor") { return .editable }
        if isSettable(element, kAXSelectedTextRangeAttribute) { return .editable }
        if nonTextRoles.contains(role) {
            // Browsers and Electron apps often don't expose their page's accessibility tree (Firefox
            // only builds it for assistive tech), and report the window or a scroll area as focused even
            // when the cursor is in a web text box. So a vague answer there means "can't tell".
            if isWebBased(NSWorkspace.shared.frontmostApplication) || hasWebAreaAncestor(element) { return .unknown }
            return .notEditable
        }
        return .unknown
    }

    /// The text either side of the cursor in the focused field, and where it was read.
    struct Surroundings {
        let element: AXUIElement
        let selection: CFRange
        /// Up to a few characters before the cursor (or the selection the paste replaces); "" at the start of the field.
        let before: String
        /// Up to a few characters after it; "" at the end of the field.
        let after: String
    }

    /// Terminals report their whole scrollback as the field, with a selection that isn't the shell's cursor.
    private static let terminalBundleIDs: Set<String> = [
        "com.apple.Terminal", "com.googlecode.iterm2", "dev.warp.Warp-Stable", "net.kovidgoyal.kitty", "org.alacritty",
        "io.alacritty", "com.github.wez.wezterm", "com.mitchellh.ghostty", "co.zeit.hyper",
    ]

    /// How long a read of the cursor may keep the main thread waiting in all, however many calls it takes: each call
    /// into the other app gets what's left (0.2 s at most), and none is made once the time is up.
    private struct Budget {
        let deadline: TimeInterval
        init(seconds: TimeInterval) { deadline = ProcessInfo.processInfo.systemUptime + seconds }

        func allows(_ element: AXUIElement) -> Bool {
            let left = deadline - ProcessInfo.processInfo.systemUptime
            guard left > 0.01 else { return false }
            AXUIElementSetMessagingTimeout(element, Float(min(0.2, left)))
            return true
        }
    }

    /// What's just before and after the cursor in the focused field, for fitting the dictation to it (see
    /// InsertionJoiner). nil whenever it can't be told for sure: no Accessibility, a field that isn't known not to be a
    /// password field, secure input on, a terminal, a field that doesn't report its cursor or length, an answer that
    /// doesn't add up, or more than 0.3 s gone.
    static func textAroundCursor(before beforeCount: Int = 12, after afterCount: Int = 8) -> Surroundings? {
        guard AXIsProcessTrusted(), !IsSecureEventInputEnabled(),
              !terminalBundleIDs.contains(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "") else { return nil }
        let budget = Budget(seconds: 0.3)
        guard let element = focusedElement(budget), isKnownNotSecure(element, budget),
              let selection = selectedRange(element, budget), budget.allows(element) else { return nil }
        var count: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXNumberOfCharactersAttribute as CFString, &count) == .success,
              let total = count as? Int else { return nil }
        let end = selection.location + selection.length
        guard end <= total else { return nil }
        let start = max(0, selection.location - beforeCount)
        guard let before = text(element, CFRange(location: start, length: selection.location - start), budget),
              let after = text(element, CFRange(location: end, length: min(afterCount, total - end)), budget) else { return nil }
        return Surroundings(element: element, selection: selection, before: before, after: after)
    }

    /// Whether the cursor is still where `surroundings` was read: the same focused element, with the same selection.
    /// False when it moved, or when that can't be told within 0.15 s.
    static func isCursorStill(at surroundings: Surroundings) -> Bool {
        guard AXIsProcessTrusted() else { return false }
        let budget = Budget(seconds: 0.15)
        guard let element = focusedElement(budget), CFEqual(element, surroundings.element),
              let selection = selectedRange(element, budget) else { return false }
        return selection.location == surroundings.selection.location && selection.length == surroundings.selection.length
    }

    private static func focusedElement(_ budget: Budget) -> AXUIElement? {
        let system = AXUIElementCreateSystemWide()
        guard budget.allows(system) else { return nil }
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
        return (focused as! AXUIElement)
    }

    /// Whether the element is known not to be a password field: it has another subrole, or none at all. A subrole that
    /// couldn't be read leaves it unknown.
    private static func isKnownNotSecure(_ element: AXUIElement, _ budget: Budget) -> Bool {
        guard budget.allows(element) else { return false }
        var value: CFTypeRef?
        switch AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &value) {
        case .success: return (value as? String).map { $0 != kAXSecureTextFieldSubrole } ?? false
        case .attributeUnsupported, .noValue: return true
        default: return false
        }
    }

    private static func selectedRange(_ element: AXUIElement, _ budget: Budget) -> CFRange? {
        guard budget.allows(element) else { return nil }
        var value: CFTypeRef?
        var range = CFRange()
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXValueGetTypeID(), AXValueGetValue(value as! AXValue, .cfRange, &range),
              range.location >= 0, range.length >= 0 else { return nil }
        return range
    }

    /// The field's text in `range` (UTF-16 offsets, as Accessibility counts), only when exactly that much came back.
    private static func text(_ element: AXUIElement, _ range: CFRange, _ budget: Budget) -> String? {
        guard range.length > 0 else { return "" }
        guard budget.allows(element) else { return nil }
        var range = range
        guard let rangeValue = AXValueCreate(.cfRange, &range) else { return nil }
        var value: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, kAXStringForRangeParameterizedAttribute as CFString,
                                                         rangeValue, &value) == .success,
              let text = value as? String, text.utf16.count == range.length else { return nil }
        return text
    }

    private static let browserBundleIDs: Set<String> = [
        "org.mozilla.firefox", "org.mozilla.firefoxdeveloperedition", "org.mozilla.nightly",
        "app.zen-browser.zen", "net.waterfox.waterfox", "io.gitlab.librewolf-community",
        "com.google.Chrome", "com.google.Chrome.beta", "com.google.Chrome.canary", "org.chromium.Chromium",
        "com.apple.Safari", "com.apple.SafariTechnologyPreview", "company.thebrowser.Browser",
        "company.thebrowser.dia", "com.microsoft.edgemac", "com.brave.Browser", "com.operasoftware.Opera",
        "com.vivaldi.Vivaldi", "com.kagi.kagimacOS", "ai.perplexity.comet",
    ]
    private static var webBasedCache: [String: Bool] = [:]

    /// A browser, or an app built on Electron / the Chromium Embedded Framework (Slack, VS Code, Notion…).
    static func isWebBased(_ app: NSRunningApplication?) -> Bool {
        guard let app, let id = app.bundleIdentifier else { return false }
        if browserBundleIDs.contains(id) { return true }
        if let cached = webBasedCache[id] { return cached }
        let frameworks = app.bundleURL?.appendingPathComponent("Contents/Frameworks")
        let result = ["Electron Framework.framework", "Chromium Embedded Framework.framework"].contains { name in
            frameworks.map { FileManager.default.fileExists(atPath: $0.appendingPathComponent(name).path) } ?? false
        }
        webBasedCache[id] = result
        return result
    }

    private static func hasWebAreaAncestor(_ element: AXUIElement) -> Bool {
        var current = element
        for _ in 0..<12 {
            var parent: CFTypeRef?
            guard AXUIElementCopyAttributeValue(current, kAXParentAttribute as CFString, &parent) == .success,
                  let parent, CFGetTypeID(parent) == AXUIElementGetTypeID() else { return false }
            current = parent as! AXUIElement
            if string(current, kAXRoleAttribute) == "AXWebArea" { return true }
        }
        return false
    }

    private static func string(_ element: AXUIElement, _ attribute: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func has(_ element: AXUIElement, _ attribute: String) -> Bool {
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success && value != nil
    }

    private static func isSettable(_ element: AXUIElement, _ attribute: String) -> Bool {
        var settable = DarwinBoolean(false)
        return AXUIElementIsAttributeSettable(element, attribute as CFString, &settable) == .success && settable.boolValue
    }
}
