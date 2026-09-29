import AppKit
import ApplicationServices
import TypelessCore

/// After a dictation is pasted, keeps an eye on that text field to see whether the user fixes any word
/// in it, so the fix can be learned (see CorrectionLearner).
///
/// The field is read through Accessibility, locally; nothing leaves the Mac. A watch ends when the user
/// leaves the field, sends or clears it, starts another dictation, or after two minutes. Only the state
/// at that point is compared, never a half-finished edit.
@MainActor
final class EditWatcher {
    /// Called with the learnable corrections when a watch ends, and whether it ended quietly (a new
    /// dictation is starting, so nothing should be shown).
    var onCorrections: (([Correction], _ quiet: Bool) -> Void)?

    private struct Session {
        let app: String?
        let element: AXUIElement
        let inserted: String
        let baseline: String
        var latest: String
        let started: Date
    }

    private var session: Session?
    private var timer: Timer?
    private static let pollInterval: TimeInterval = 0.5
    private static let maxDuration: TimeInterval = 120
    /// Big documents and terminal scrollback aren't worth re-reading twice a second.
    private static let maxFieldLength = 50_000

    /// Starts watching the focused field, which should now contain `inserted`.
    func watch(inserted: String) {
        finish(quiet: true, reason: "next paste")
        let app = NSWorkspace.shared.frontmostApplication?.bundleIdentifier
        // Give the target app a moment to apply the paste before taking the baseline.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in
            guard let self, self.session == nil else { return }
            func skip(_ result: String, _ fields: [String: Any] = [:]) {
                LearningLog.write("start", app: app, fields.merging(["result": result, "insertedLength": inserted.count]) { $1 })
            }
            guard let element = Self.focusedElement() else { return skip("no focused field") }
            guard !Self.isSecure(element) else { return skip("password field") }
            guard let value = Self.value(of: element) else { return skip("field text unreadable") }
            guard value.count <= Self.maxFieldLength else { return skip("field too long", ["fieldLength": value.count]) }
            guard CorrectionLearner.editedRegion(inserted: inserted, before: value, after: value) != nil else {
                // Found once spacing is ignored: the app reformatted the text (line breaks, list markers…).
                let squeeze = { (text: String) in text.filter { !$0.isWhitespace } }
                return skip("dictated text not found in field", ["fieldLength": value.count,
                                                                 "foundIgnoringSpaces": squeeze(value).contains(squeeze(inserted))])
            }
            skip("watching", ["fieldLength": value.count])
            self.session = Session(app: app, element: element, inserted: inserted, baseline: value, latest: value, started: Date())
            self.timer = Timer.scheduledTimer(withTimeInterval: Self.pollInterval, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.poll() }
            }
        }
    }

    /// Ends the current watch now and reports what was learned. `reason` is for the learning log.
    func finish(quiet: Bool, reason: String) {
        timer?.invalidate()
        timer = nil
        guard let session else { return }
        self.session = nil
        var fields: [String: Any] = ["reason": reason, "seconds": Int(Date().timeIntervalSince(session.started))]
        defer { LearningLog.write("end", app: session.app, fields) }
        guard session.latest != session.baseline,
              let edited = CorrectionLearner.editedRegion(inserted: session.inserted, before: session.baseline,
                                                          after: session.latest) else {
            fields["edited"] = false
            return
        }
        fields["edited"] = true
        let review = CorrectionLearner.review(original: session.inserted, edited: edited, isCommonWord: Self.isCommonWord)
        fields.merge(LearningLog.review(review)) { $1 }
        let corrections = review.learnable
        if !corrections.isEmpty { onCorrections?(corrections, quiet) }
    }

    private func poll() {
        guard var session else { return }
        guard let focused = Self.focusedElement(), CFEqual(focused, session.element) else {
            finish(quiet: false, reason: "focus left the field")
            return
        }
        if let value = Self.value(of: session.element), value != session.latest {
            // Sent, cleared or rewritten beyond the dictated text: judge the last state that still made sense.
            guard value.count <= Self.maxFieldLength,
                  CorrectionLearner.editedRegion(inserted: session.inserted, before: session.baseline, after: value) != nil
            else {
                finish(quiet: false, reason: "sent, cleared or edited outside the dictated text")
                return
            }
            session.latest = value
            self.session = session
        }
        if Date().timeIntervalSince(session.started) > Self.maxDuration { finish(quiet: false, reason: "2 minutes") }
    }

    // MARK: Accessibility

    private static func focusedElement() -> AXUIElement? {
        guard AXIsProcessTrusted() else { return nil }
        let system = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(system, 0.2)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        let element = value as! AXUIElement
        AXUIElementSetMessagingTimeout(element, 0.2)
        return element
    }

    private static func value(of element: AXUIElement) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func isSecure(_ element: AXUIElement) -> Bool {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &value) == .success else { return false }
        return (value as? String) == kAXSecureTextFieldSubrole
    }

    /// An everyday English word, per the system spelling dictionary.
    private static func isCommonWord(_ word: String) -> Bool {
        let checker = NSSpellChecker.shared
        return checker.checkSpelling(of: word.lowercased(), startingAt: 0, language: "en",
                                     wrap: false, inSpellDocumentWithTag: 0, wordCount: nil).location == NSNotFound
    }
}
