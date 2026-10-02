import AppKit
import ApplicationServices
import Carbon.HIToolbox
import TypelessCore

/// After a dictation is pasted, keeps an eye on that text field to see whether the user fixes any word
/// in it, so the fix can be learned (see CorrectionLearner).
///
/// The field is read through Accessibility, locally; nothing leaves the Mac. Many apps don't let their fields be read
/// that way (Electron apps such as Claude, Discord, Slack or Codex, Firefox, WeChat): they are first asked to turn on
/// their accessibility support, and where the field still can't be read, the keys pressed after the paste are followed
/// instead (see TypedEdit). A watch ends when the user leaves the field, sends or clears it, starts another dictation,
/// or after two minutes. Only the state at that point is compared, never a half-finished edit.
@MainActor
final class EditWatcher {
    /// Called with the learnable corrections when a watch ends, and whether it ended quietly (a new
    /// dictation is starting, so nothing should be shown).
    var onCorrections: (([Correction], _ quiet: Bool) -> Void)?

    /// Reading the field itself.
    private struct FieldSession {
        let element: AXUIElement
        let baseline: String
        var latest: String
    }

    private struct Watch {
        let app: String?
        let inserted: String
        let started: Date
        var field: FieldSession?
        /// Following the keys, when the field can't be read.
        var keys: TypedEdit?
    }

    private var watch: Watch?
    private var timer: Timer?
    private var monitors: [Any] = []
    private var activationObserver: NSObjectProtocol?
    private static let pollInterval: TimeInterval = 0.5
    private static let maxDuration: TimeInterval = 120
    /// Big documents and terminal scrollback aren't worth re-reading twice a second.
    private static let maxFieldLength = 50_000
    /// Apps already asked to turn on their accessibility support, by process ID.
    private var enabledApps: Set<pid_t> = []

    /// Starts watching the focused field, which should now contain `inserted`.
    func watch(inserted: String) {
        finish(quiet: true, reason: "next paste")
        guard let front = NSWorkspace.shared.frontmostApplication else { return }
        let app = front.bundleIdentifier
        let asked = enableAccessibility(front)
        // Give the target app a moment to apply the paste before taking the baseline (and, the first time, to build
        // the accessibility tree it was just asked for).
        DispatchQueue.main.asyncAfter(deadline: .now() + (asked ? 0.9 : 0.4)) { [weak self] in
            guard let self, self.watch == nil, NSWorkspace.shared.frontmostApplication == front else { return }
            self.begin(app: app, inserted: inserted, askedForAccessibility: asked)
        }
    }

    private func begin(app: String?, inserted: String, askedForAccessibility: Bool) {
        var fields: [String: Any] = ["insertedLength": inserted.count]
        if askedForAccessibility { fields["askedForAccessibility"] = true }
        let unreadable: String
        if let element = Self.focusedElement() {
            if Self.isSecure(element) {
                LearningLog.write("start", app: app, fields.merging(["result": "password field"]) { $1 })
                return
            }
            if let value = Self.value(of: element) {
                guard value.count <= Self.maxFieldLength else {
                    LearningLog.write("start", app: app, fields.merging(["result": "field too long", "fieldLength": value.count]) { $1 })
                    return
                }
                if CorrectionLearner.editedRegion(inserted: inserted, before: value, after: value) != nil {
                    LearningLog.write("start", app: app, fields.merging(["result": "watching", "fieldLength": value.count]) { $1 })
                    start(Watch(app: app, inserted: inserted, started: Date(),
                                field: FieldSession(element: element, baseline: value, latest: value)))
                    return
                }
                // Found once spacing is ignored: the app reformatted the text (line breaks, list markers…).
                let squeeze = { (text: String) in text.filter { !$0.isWhitespace } }
                fields["foundIgnoringSpaces"] = squeeze(value).contains(squeeze(inserted))
                fields["fieldLength"] = value.count
                unreadable = "dictated text not found in field"
            } else {
                unreadable = "field text unreadable"
            }
        } else {
            unreadable = "no focused field"
        }
        // The field can't be compared: follow the keys instead.
        fields["field"] = unreadable
        guard Self.typesPlainText else {
            LearningLog.write("start", app: app, fields.merging(["result": "input method active"]) { $1 })
            return
        }
        LearningLog.write("start", app: app, fields.merging(["result": "following keys"]) { $1 })
        start(Watch(app: app, inserted: inserted, started: Date(), keys: TypedEdit(pasted: inserted)))
    }

    private func start(_ watch: Watch) {
        self.watch = watch
        timer = Timer.scheduledTimer(withTimeInterval: Self.pollInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        guard watch.keys != nil else { return }
        if let keys = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: { [weak self] event in
            MainActor.assumeIsolated { self?.handle(event) }
        }) { monitors.append(keys) }
        // A click puts the cursor somewhere the keys can't tell.
        if let clicks = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown], handler: { [weak self] _ in
            MainActor.assumeIsolated { self?.finish(quiet: false, reason: "clicked") }
        }) { monitors.append(clicks) }
        activationObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.finish(quiet: false, reason: "switched app") }
        }
    }

    /// Ends the current watch now and reports what was learned. `reason` is for the learning log.
    func finish(quiet: Bool, reason: String) {
        timer?.invalidate()
        timer = nil
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
        if let activationObserver { NSWorkspace.shared.notificationCenter.removeObserver(activationObserver) }
        activationObserver = nil
        guard let watch else { return }
        self.watch = nil
        var fields: [String: Any] = ["reason": reason, "seconds": Int(Date().timeIntervalSince(watch.started)),
                                     "mode": watch.keys != nil ? "keys" : "field"]
        defer { LearningLog.write("end", app: watch.app, fields) }
        let edited: String?
        if let keys = watch.keys {
            edited = keys.text != watch.inserted ? keys.text : nil
        } else if let field = watch.field, field.latest != field.baseline {
            edited = CorrectionLearner.editedRegion(inserted: watch.inserted, before: field.baseline, after: field.latest)
        } else {
            edited = nil
        }
        guard let edited else {
            fields["edited"] = false
            return
        }
        fields["edited"] = true
        let review = CorrectionLearner.review(original: watch.inserted, edited: edited, isCommonWord: Self.isCommonWord)
        fields.merge(LearningLog.review(review)) { $1 }
        let corrections = review.learnable
        if !corrections.isEmpty { onCorrections?(corrections, quiet) }
    }

    private func poll() {
        guard var watch else { return }
        if Date().timeIntervalSince(watch.started) > Self.maxDuration { return finish(quiet: false, reason: "2 minutes") }
        guard let field = watch.field else { return }
        guard let focused = Self.focusedElement(), CFEqual(focused, field.element) else {
            return finish(quiet: false, reason: "focus left the field")
        }
        if let value = Self.value(of: field.element), value != field.latest {
            // Sent, cleared or rewritten beyond the dictated text: judge the last state that still made sense.
            guard value.count <= Self.maxFieldLength,
                  CorrectionLearner.editedRegion(inserted: watch.inserted, before: field.baseline, after: value) != nil
            else {
                return finish(quiet: false, reason: "sent, cleared or edited outside the dictated text")
            }
            watch.field?.latest = value
            self.watch = watch
        }
    }

    // MARK: Following the keys

    private func handle(_ event: NSEvent) {
        guard var watch, var keys = watch.keys else { return }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let command = flags.contains(.command), option = flags.contains(.option), shift = flags.contains(.shift)
        let unit: TypedEdit.Unit = option ? .word : .character
        let key: TypedEdit.Key
        switch Int(event.keyCode) {
        case kVK_Return, kVK_ANSI_KeypadEnter: return finish(quiet: false, reason: "return")
        case kVK_Tab: return finish(quiet: false, reason: "tab")
        case kVK_Escape: return
        case kVK_Delete:
            guard !command else { return finish(quiet: false, reason: "key not followed") }
            key = .deleteBackward(unit)
        case kVK_ForwardDelete:
            guard !command else { return finish(quiet: false, reason: "key not followed") }
            key = .deleteForward(unit)
        case kVK_LeftArrow, kVK_RightArrow:
            guard !command, !flags.contains(.control) else { return finish(quiet: false, reason: "key not followed") }
            key = Int(event.keyCode) == kVK_LeftArrow ? .left(unit, extend: shift) : .right(unit, extend: shift)
        case kVK_ANSI_V where command:
            guard let pasted = NSPasteboard.general.string(forType: .string) else { return finish(quiet: false, reason: "key not followed") }
            key = .insert(pasted)
        case kVK_ANSI_C where command:
            return
        default:
            // Other shortcuts (undo, select all…), up / down, Home / End, function keys.
            guard !command, !flags.contains(.control), let characters = event.characters, !characters.isEmpty,
                  !characters.unicodeScalars.contains(where: { (0xF700...0xF8FF).contains($0.value) || $0.properties.generalCategory == .control })
            else { return finish(quiet: false, reason: "key not followed") }
            guard Self.typesPlainText else { return finish(quiet: false, reason: "input method") }
            key = .insert(characters)
        }
        guard keys.apply(key) else { return finish(quiet: false, reason: "cursor left the dictated text") }
        watch.keys = keys
        self.watch = watch
    }

    /// Whether the current input source types what the keys say: a keyboard layout, not an input method such as
    /// Pinyin, whose keys spell a word before it's chosen.
    private static var typesPlainText: Bool {
        guard let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
              let type = TISGetInputSourceProperty(source, kTISPropertyInputSourceType) else { return true }
        let value = Unmanaged<CFString>.fromOpaque(type).takeUnretainedValue()
        return CFEqual(value, kTISTypeKeyboardLayout)
    }

    // MARK: Accessibility

    /// Browsers that build their accessibility tree for assistive apps that set AXEnhancedUserInterface.
    private static let browsers: Set<String> = [
        "org.mozilla.firefox", "org.mozilla.firefoxdeveloperedition", "org.mozilla.nightly", "com.google.Chrome",
        "com.google.Chrome.canary", "com.brave.Browser", "com.microsoft.edgemac", "company.thebrowser.Browser", "com.vivaldi.Vivaldi",
    ]

    /// Asks an app whose fields can't be read to turn on its accessibility support, once per process. Electron apps
    /// (Claude, Discord, Slack, VS Code…) take AXManualAccessibility, which changes nothing else; browsers take
    /// AXEnhancedUserInterface. Returns whether it asked just now.
    private func enableAccessibility(_ app: NSRunningApplication) -> Bool {
        let pid = app.processIdentifier
        guard AXIsProcessTrusted(), !enabledApps.contains(pid), Self.focusedElement().flatMap(Self.value(of:)) == nil else { return false }
        enabledApps.insert(pid)
        let element = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(element, 0.2)
        AXUIElementSetAttributeValue(element, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        if Self.browsers.contains(app.bundleIdentifier ?? "") {
            AXUIElementSetAttributeValue(element, "AXEnhancedUserInterface" as CFString, kCFBooleanTrue)
        }
        return true
    }

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
