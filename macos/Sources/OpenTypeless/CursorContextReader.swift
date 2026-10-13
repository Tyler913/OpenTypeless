import AppKit
import ApplicationServices
import Carbon.HIToolbox
import TypelessCore

/// Reads the text around the cursor in the field being dictated into, for the clean-up to spell names by (see
/// CursorContext). Through Accessibility, like EditWatcher, but once, as recording starts (the dictated text only goes in
/// at the end, so the field is still the speaker's), and off the main thread: every call goes into the other app with a
/// short timeout, and whatever fails or takes long just means no context. Password fields are never read, nor anything
/// while secure input is on (a password field has keyboard focus somewhere, also in apps whose fields can't be seen).
/// Only text fields and editable web content are read, never a page or a list.
enum CursorContextReader {
    /// An app that shows no focused element (Electron, until its accessibility tree is switched on) is asked again this
    /// many times, this far apart: about a second in all, while the speaker talks.
    private static let attempts = 4
    private static let retryDelay = 0.3

    private enum Outcome {
        /// No focused element yet: worth asking again.
        case noField
        /// The excerpt, or nil for a field that isn't one to read, can't be read, or is empty.
        case done(String?)
    }

    /// Calls `completion` on the main thread with the excerpt around the cursor in `pid`'s focused field, or nil.
    @MainActor
    static func read(pid: pid_t, completion: @escaping @MainActor (String?) -> Void) {
        attempt(1, pid: pid, completion: completion)
    }

    @MainActor
    private static func attempt(_ number: Int, pid: pid_t, completion: @escaping @MainActor (String?) -> Void) {
        guard AXIsProcessTrusted(), !IsSecureEventInputEnabled() else { return completion(nil) }
        DispatchQueue.global(qos: .userInitiated).async {
            let outcome = read(pid: pid)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    switch outcome {
                    case .noField where number < attempts:
                        DispatchQueue.main.asyncAfter(deadline: .now() + retryDelay) {
                            MainActor.assumeIsolated { attempt(number + 1, pid: pid, completion: completion) }
                        }
                    case .noField:
                        completion(nil)
                    case let .done(text):
                        completion(text)
                    }
                }
            }
        }
    }

    private static func read(pid: pid_t) -> Outcome {
        guard let element = EditWatcher.focusedElement(pid: pid) else {
            // Builds the tree for the next attempt (the speaker may not dictate with learning on, which would have).
            EditWatcher.switchOnAccessibility(pid)
            return .noField
        }
        guard !EditWatcher.isSecure(element), FocusProbe.isTextInput(element) else { return .done(nil) }
        let selection = selectedRange(element)
        if let selection, let around = textAround(selection, in: element) {
            return .done(CursorContext.excerpt(before: around.before, selected: around.selected, after: around.after))
        }
        // No ranges to read by: the whole value, with the cursor where the field says, or at its end.
        guard let value = EditWatcher.value(of: element) else { return .done(nil) }
        return .done(CursorContext.excerpt(text: value, selection: selection.map { ($0.location, $0.length) }))
    }

    /// The text before the selection, the selection and the text after it, each read as a range of at most twice the
    /// limit (in UTF-16 units: a character is one or more), so a long document isn't copied over whole. Nil where the
    /// field doesn't answer for ranges.
    private static func textAround(_ selection: CFRange,
                                   in element: AXUIElement) -> (before: String, selected: String, after: String)? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXNumberOfCharactersAttribute as CFString, &value) == .success,
              let count = value as? Int else { return nil }
        let span = 2 * CursorContext.limit
        let start = min(max(0, selection.location), count)
        let end = min(count, start + max(0, selection.length))
        guard let before = string(element, from: max(0, start - span), to: start),
              let selected = string(element, from: start, to: min(end, start + span)),
              let after = string(element, from: end, to: min(count, end + span)) else { return nil }
        return (before, selected, after)
    }

    private static func string(_ element: AXUIElement, from start: Int, to end: Int) -> String? {
        guard end > start else { return "" }
        var range = CFRange(location: start, length: end - start)
        guard let rangeValue = AXValueCreate(.cfRange, &range) else { return nil }
        var text: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, kAXStringForRangeParameterizedAttribute as CFString,
                                                         rangeValue, &text) == .success else { return nil }
        return text as? String
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
}
