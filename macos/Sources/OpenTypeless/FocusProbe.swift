import AppKit
import ApplicationServices

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
