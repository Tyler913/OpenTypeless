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

    /// The text either side of the cursor in the focused field.
    struct Surroundings {
        /// Up to a few characters before the cursor (or the selection the paste replaces); "" at the start of the field.
        let before: String
        /// Up to a few characters after it; "" at the end of the field, nil when that couldn't be read.
        let after: String?
    }

    /// Terminals report their whole scrollback as the field, with a selection that isn't the shell's cursor.
    private static let terminalBundleIDs: Set<String> = [
        "com.apple.Terminal", "com.googlecode.iterm2", "dev.warp.Warp-Stable", "net.kovidgoyal.kitty", "org.alacritty",
        "io.alacritty", "com.github.wez.wezterm", "com.mitchellh.ghostty", "co.zeit.hyper",
    ]

    /// What's just before and after the cursor in the focused field, for fitting the dictation to it (see
    /// InsertionJoiner). nil whenever it can't be told for sure: no Accessibility, a password field or secure input on,
    /// a terminal, a field that doesn't report its cursor, or an answer that doesn't add up. Each call into the other app
    /// waits at most 0.2 s.
    static func textAroundCursor(before beforeCount: Int = 12, after afterCount: Int = 8) -> Surroundings? {
        guard AXIsProcessTrusted(), !IsSecureEventInputEnabled(),
              !terminalBundleIDs.contains(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "") else { return nil }
        let system = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(system, 0.2)
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
        let element = focused as! AXUIElement
        AXUIElementSetMessagingTimeout(element, 0.2)
        guard string(element, kAXSubroleAttribute) != kAXSecureTextFieldSubrole else { return nil }

        var rangeValue: CFTypeRef?
        var selection = CFRange()
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &rangeValue) == .success,
              let rangeValue, CFGetTypeID(rangeValue) == AXValueGetTypeID(),
              AXValueGetValue(rangeValue as! AXValue, .cfRange, &selection),
              selection.location >= 0, selection.length >= 0 else { return nil }
        var count: CFTypeRef?
        let total = AXUIElementCopyAttributeValue(element, kAXNumberOfCharactersAttribute as CFString, &count) == .success
            ? count as? Int : nil
        let end = selection.location + selection.length
        if let total, end > total { return nil }

        let start = max(0, selection.location - beforeCount)
        guard let before = text(element, CFRange(location: start, length: selection.location - start)) else { return nil }
        let after: String?
        if let total {
            after = text(element, CFRange(location: end, length: min(afterCount, total - end)))
        } else {
            after = nil
        }
        return Surroundings(before: before, after: after)
    }

    /// The field's text in `range` (UTF-16 offsets, as Accessibility counts), only when exactly that much came back.
    private static func text(_ element: AXUIElement, _ range: CFRange) -> String? {
        guard range.length > 0 else { return "" }
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
