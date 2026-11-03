import AppKit
import ApplicationServices
import TypelessCore

/// Where the user is working, for placing the recording capsule (see `HUDPlacement`): the frame of the focused window of
/// the app in front and, for a capsule near the text cursor, the line being typed into. Read through Accessibility off
/// the main thread, since every call goes into the other app, each with a short timeout and all within a small budget;
/// whatever doesn't answer in time is simply unknown, and the capsule doesn't wait for it long (see
/// `HUDController.locate`). Only positions are read, never text, so a password field is no different from any other.
enum CaretLocator {
    struct Location: Sendable {
        /// The focused window's frame, in Accessibility's coordinates (y down from the top of the main screen).
        var window: HUDRect?
        /// The line being typed into (the cursor, or the selection), else the focused text field; nil when unknown or not
        /// asked for.
        var caret: HUDRect?
    }

    /// Each call into the app gets at most this long, and the whole read about three of them.
    private static let timeout: Float = 0.1
    private static let budget = 0.3
    private static let textRoles: Set<String> = [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole, "AXSearchField"]

    /// Reads where the focus of the app `pid` is, on a background queue; `completion` gets it on the main thread, once.
    /// `caret`: whether the text cursor is wanted too, or only the focused window.
    static func locate(pid: pid_t, caret: Bool, completion: @escaping @MainActor (Location) -> Void) {
        DispatchQueue.global(qos: .userInteractive).async {
            let location = AXIsProcessTrusted() ? read(pid: pid, caret: caret) : Location()
            DispatchQueue.main.async {
                MainActor.assumeIsolated { completion(location) }
            }
        }
    }

    /// Off the main thread.
    private static func read(pid: pid_t, caret wantsCaret: Bool) -> Location {
        let deadline = ProcessInfo.processInfo.systemUptime + budget
        func inTime() -> Bool { ProcessInfo.processInfo.systemUptime < deadline }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, timeout)
        var location = Location()
        if let window = element(app, kAXFocusedWindowAttribute) { location.window = frame(of: window) }
        guard wantsCaret, inTime(), let focused = element(app, kAXFocusedUIElementAttribute),
              string(focused, kAXRoleAttribute) != kAXWindowRole, inTime() else { return location }
        var caret = lineBounds(focused, inTime)
        if caret == nil, inTime(), isTextField(focused) { caret = frame(of: focused) }
        // An answer outside the focused window isn't where the cursor is (an app that gives AppKit's coordinates, say).
        if let found = caret, let window = location.window,
           !window.contains(HUDPoint(x: found.midX, y: found.midY)) {
            caret = nil
        }
        location.caret = caret
        return location
    }

    /// The rectangle of the selection or, with nothing selected, the cursor. Many fields answer for an empty range with
    /// nothing or an empty rectangle: then the character after the cursor, or else the one before it (which may end the
    /// line above, so the line after it counts too).
    private static func lineBounds(_ element: AXUIElement, _ inTime: () -> Bool) -> HUDRect? {
        var value: CFTypeRef?
        var selection = CFRange()
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXValueGetTypeID(), AXValueGetValue(value as! AXValue, .cfRange, &selection),
              selection.location >= 0, selection.length >= 0 else { return nil }
        if let rect = bounds(element, selection), rect.height > 0 { return rect }
        guard selection.length == 0 else { return nil }
        if inTime(), let next = bounds(element, CFRange(location: selection.location, length: 1)), next.height > 0 {
            return next
        }
        if selection.location > 0, inTime(),
           var previous = bounds(element, CFRange(location: selection.location - 1, length: 1)), previous.height > 0 {
            previous.height *= 2
            return previous
        }
        return nil
    }

    private static func bounds(_ element: AXUIElement, _ range: CFRange) -> HUDRect? {
        var range = range
        guard let rangeValue = AXValueCreate(.cfRange, &range) else { return nil }
        var result: CFTypeRef?
        var rect = CGRect.zero
        guard AXUIElementCopyParameterizedAttributeValue(element, kAXBoundsForRangeParameterizedAttribute as CFString,
                                                         rangeValue, &result) == .success,
              let result, CFGetTypeID(result) == AXValueGetTypeID(), AXValueGetValue(result as! AXValue, .cgRect, &rect),
              rect.minX.isFinite, rect.minY.isFinite, rect.width.isFinite, rect.height.isFinite else { return nil }
        return HUDRect(x: rect.minX, y: rect.minY, width: rect.width, height: rect.height)
    }

    /// A field that takes typed text, as `CursorContextReader` tells one: below it is clear of the line being typed into.
    private static func isTextField(_ element: AXUIElement) -> Bool {
        textRoles.contains(string(element, kAXRoleAttribute) ?? "") || has(element, "AXEditableAncestor")
    }

    /// An element's frame, from its position and size.
    private static func frame(of element: AXUIElement) -> HUDRect? {
        var positionValue: CFTypeRef?
        var sizeValue: CFTypeRef?
        var origin = CGPoint.zero
        var size = CGSize.zero
        guard AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &positionValue) == .success,
              let positionValue, CFGetTypeID(positionValue) == AXValueGetTypeID(),
              AXValueGetValue(positionValue as! AXValue, .cgPoint, &origin),
              AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &sizeValue) == .success,
              let sizeValue, CFGetTypeID(sizeValue) == AXValueGetTypeID(),
              AXValueGetValue(sizeValue as! AXValue, .cgSize, &size),
              origin.x.isFinite, origin.y.isFinite, size.width > 0, size.height > 0 else { return nil }
        return HUDRect(x: origin.x, y: origin.y, width: size.width, height: size.height)
    }

    private static func element(_ source: AXUIElement, _ attribute: String) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(source, attribute as CFString, &value) == .success, let value,
              CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        let element = value as! AXUIElement
        AXUIElementSetMessagingTimeout(element, timeout)
        return element
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
}
