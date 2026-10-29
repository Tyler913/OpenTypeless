import AppKit
import ApplicationServices
import Carbon.HIToolbox
import TypelessCore

/// One dictation's read of the text around the cursor. Cancelled when the dictation ends, the screen locks, or the
/// clean-up has taken what came in so far; a cancelled read gives nothing, whenever it finishes.
final class CursorContextRead: @unchecked Sendable {
    /// The app in front when recording started: the only one read.
    let pid: pid_t
    private let lock = NSLock()
    private var cancelled = false

    init(pid: pid_t) { self.pid = pid }

    func cancel() { lock.withLock { cancelled = true } }
    var isCancelled: Bool { lock.withLock { cancelled } }
}

/// Reads the text around the cursor in the field being dictated into, for the clean-up to spell names by (see
/// CursorContext). Through Accessibility, like EditWatcher, but once, as recording starts (the dictated text only goes in
/// at the end, so the field is still the speaker's), and off the main thread: every call goes into the other app with a
/// short timeout, and whatever fails or takes long just means no context.
///
/// Only a field that answers clearly is read: one of the app in front that says it isn't a password field and that it
/// takes text. Nothing is read while secure input is on (a password field has keyboard focus somewhere, also in apps
/// whose fields can't be seen). The text is kept only if, once it's read, the same field of the same app still has focus,
/// no password field came up meanwhile, and the read wasn't cancelled.
enum CursorContextReader {
    /// An app that shows no focused element (Electron, until its accessibility tree is switched on) is asked again this
    /// many times, this far apart: about a second in all, while the speaker talks.
    private static let attempts = 4
    private static let retryDelay = 0.3
    /// Where a field doesn't answer for ranges, its whole value is read, if it isn't longer than this.
    private static let maxFieldLength = 200_000
    private static let textRoles: Set<String> = [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole, "AXSearchField"]

    private enum Outcome {
        /// No focused element yet: worth asking again.
        case noField
        /// The excerpt, or nil for a field that isn't one to read, can't be read, changed meanwhile, or is empty.
        case done(String?)
    }

    private enum Security { case secure, notSecure, unknown }

    /// Starts reading the focused field of the app `pid`; `completion` gets the read and the excerpt (or nil) on the main
    /// thread, once.
    @MainActor
    static func start(pid: pid_t, completion: @escaping @MainActor (CursorContextRead, String?) -> Void) -> CursorContextRead {
        let read = CursorContextRead(pid: pid)
        attempt(1, read, completion)
        return read
    }

    @MainActor
    private static func attempt(_ number: Int, _ read: CursorContextRead,
                                _ completion: @escaping @MainActor (CursorContextRead, String?) -> Void) {
        guard !read.isCancelled, AXIsProcessTrusted(), !IsSecureEventInputEnabled() else { return completion(read, nil) }
        DispatchQueue.global(qos: .userInitiated).async {
            let outcome = readField(read)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    switch outcome {
                    case .noField where number < attempts && !read.isCancelled:
                        DispatchQueue.main.asyncAfter(deadline: .now() + retryDelay) {
                            MainActor.assumeIsolated { attempt(number + 1, read, completion) }
                        }
                    case let .done(found?) where isStillThere(read):
                        completion(read, found)
                    default:
                        completion(read, nil)
                    }
                }
            }
        }
    }

    /// The last check, on the main thread, before the text is taken: the read wasn't cancelled, its app is still in
    /// front, and no password field has taken keyboard focus meanwhile.
    @MainActor
    private static func isStillThere(_ read: CursorContextRead) -> Bool {
        !read.isCancelled && NSWorkspace.shared.frontmostApplication?.processIdentifier == read.pid
            && !IsSecureEventInputEnabled()
    }

    /// Off the main thread.
    private static func readField(_ read: CursorContextRead) -> Outcome {
        guard let element = focusedElement(pid: read.pid) else {
            // Builds the tree for the next attempt (with learning on, EditWatcher.prepare has asked already).
            switchOnAccessibility(read.pid)
            return .noField
        }
        guard security(of: element) == .notSecure, isEditableText(element), !read.isCancelled,
              !IsSecureEventInputEnabled() else { return .done(nil) }
        let found = excerpt(of: element)
        // Asked again once it's read: the field could have turned into a password field (a sign-in form swapping it) or
        // lost focus meanwhile.
        guard found != nil, !read.isCancelled, !IsSecureEventInputEnabled(), security(of: element) == .notSecure,
              let focused = focusedElement(pid: read.pid), CFEqual(focused, element) else { return .done(nil) }
        return .done(found)
    }

    /// The focused element of the app `pid`, asked of the app itself, or system-wide when the app doesn't answer, but then
    /// only if it belongs to that app: a field of another app is never read. A focused window (a dormant Chromium tree)
    /// counts as none.
    private static func focusedElement(pid: pid_t) -> AXUIElement? {
        for source in [AXUIElementCreateApplication(pid), AXUIElementCreateSystemWide()] {
            AXUIElementSetMessagingTimeout(source, 0.2)
            var value: CFTypeRef?
            guard AXUIElementCopyAttributeValue(source, kAXFocusedUIElementAttribute as CFString, &value) == .success,
                  let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { continue }
            let element = value as! AXUIElement
            var owner: pid_t = 0
            guard AXUIElementGetPid(element, &owner) == .success, owner == pid else { continue }
            AXUIElementSetMessagingTimeout(element, 0.2)
            if string(element, kAXRoleAttribute) == kAXWindowRole { continue }
            return element
        }
        return nil
    }

    /// Whether the element is a password field, by its role and subrole. Only a clear answer counts: a role that can't be
    /// read, a subrole that fails to answer, or an answer that isn't text is unknown, and an unknown field isn't read.
    private static func security(of element: AXUIElement) -> Security {
        var roleValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &roleValue) == .success,
              let role = roleValue as? String else { return .unknown }
        if role == kAXSecureTextFieldSubrole { return .secure }
        var subroleValue: CFTypeRef?
        switch AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subroleValue) {
        case .success:
            guard let subrole = subroleValue as? String else { return .unknown }
            return subrole == kAXSecureTextFieldSubrole ? .secure : .notSecure
        case .noValue, .attributeUnsupported:
            // No subrole at all, so not marked as a password field.
            return .notSecure
        default:
            return .unknown
        }
    }

    /// Whether the element says it takes typed text: a text field whose value can be set, or editable web content (WebKit
    /// and Chromium give it an editable ancestor). A read-only text view only lets its selection be set, which isn't
    /// enough.
    private static func isEditableText(_ element: AXUIElement) -> Bool {
        if has(element, "AXEditableAncestor") || has(element, "AXHighestEditableAncestor") { return true }
        guard textRoles.contains(string(element, kAXRoleAttribute) ?? "") else { return false }
        var settable = DarwinBoolean(false)
        return AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &settable) == .success
            && settable.boolValue
    }

    /// The excerpt around the field's cursor: read as ranges around its selection where it answers for them, else from
    /// its whole value, with the cursor where the field says, or at its end.
    private static func excerpt(of element: AXUIElement) -> String? {
        let selection = selectedRange(element)
        if let selection, let around = textAround(selection, in: element) {
            return CursorContext.excerpt(before: around.before, selected: around.selected, after: around.after,
                                         cut: around.cut)
        }
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &value) == .success,
              let whole = value as? String, whole.utf16.count <= maxFieldLength else { return nil }
        return CursorContext.excerpt(text: whole, selection: selection.map { ($0.location, $0.length) })
    }

    /// The text before the selection, the selection and the text after it, each read as a range of at most twice the
    /// limit (in UTF-16 units: a character is one or more), so a long document isn't copied over whole. A range that
    /// stops short of the field's start or end may end inside a character, which is marked for CursorContext to drop.
    /// Nil where the field doesn't answer for ranges.
    private static func textAround(_ selection: CFRange, in element: AXUIElement)
        -> (before: String, selected: String, after: String, cut: CursorContext.Cut)? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXNumberOfCharactersAttribute as CFString, &value) == .success,
              let count = value as? Int else { return nil }
        let span = 2 * CursorContext.limit
        let start = min(max(0, selection.location), count)
        let end = min(count, start + max(0, selection.length))
        var cut: CursorContext.Cut = []
        if start > span { cut.insert(.before) }
        if end - start > span { cut.insert(.selection) }
        if count - end > span { cut.insert(.after) }
        guard let before = text(of: element, from: max(0, start - span), to: start),
              let selected = text(of: element, from: start, to: min(end, start + span)) else { return nil }
        // After a selection read only in part comes nothing: what follows its first part isn't what was read.
        let afterText = cut.contains(.selection) ? "" : text(of: element, from: end, to: min(count, end + span))
        guard let after = afterText else { return nil }
        return (before, selected, after, cut)
    }

    private static func text(of element: AXUIElement, from start: Int, to end: Int) -> String? {
        guard end > start else { return "" }
        var range = CFRange(location: start, length: end - start)
        guard let rangeValue = AXValueCreate(.cfRange, &range) else { return nil }
        var result: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, kAXStringForRangeParameterizedAttribute as CFString,
                                                         rangeValue, &result) == .success else { return nil }
        return result as? String
    }

    /// Where the cursor or the selection is, in UTF-16 units.
    private static func selectedRange(_ element: AXUIElement) -> CFRange? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var range = CFRange()
        guard AXValueGetValue(value as! AXValue, .cfRange, &range) else { return nil }
        return range
    }

    /// As EditWatcher does: Electron apps build their accessibility tree once AXManualAccessibility is set, and Chrome
    /// once its role is read.
    private static func switchOnAccessibility(_ pid: pid_t) {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.2)
        _ = string(app, kAXRoleAttribute)
        AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue)
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
