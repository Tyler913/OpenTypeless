import AppKit
import Carbon

/// Inserts text at the cursor of the frontmost app by pasting it.
///
/// The text goes on the pasteboard as *promised* data: macOS asks this app for it only when some
/// app actually pastes. That tells us whether ⌘V landed, even in browsers and Electron apps that don't
/// expose their text fields to Accessibility. If it landed, the previous clipboard is restored; if
/// nothing asked for the text, it's left on the clipboard for the user to paste themselves.
enum TextInserter {
    enum Outcome {
        /// The frontmost app read the text in response to ⌘V.
        case pasted
        /// ⌘V was sent but nothing read the text; it's on the clipboard.
        case notPasted
        /// No Accessibility permission to send ⌘V; the text is on the clipboard.
        case noPermission
    }

    private static let transientType = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")
    /// How long to wait for the target app to read the pasteboard after ⌘V.
    private static let pasteTimeout: Duration = .milliseconds(1200)

    private final class PasteTracker: NSObject, NSPasteboardItemDataProvider {
        let text: String
        var requested = false

        init(text: String) { self.text = text }

        func pasteboard(_ pasteboard: NSPasteboard?, item: NSPasteboardItem,
                        provideDataForType type: NSPasteboard.PasteboardType) {
            requested = true
            item.setString(text, forType: type)
        }
    }

    @MainActor
    static func insert(_ text: String, restoreClipboard: Bool) async -> Outcome {
        guard AXIsProcessTrusted() else {
            copyToClipboard(text)
            return .noPermission
        }
        let pasteboard = NSPasteboard.general
        let saved = snapshot(pasteboard)

        let tracker = PasteTracker(text: text)
        let item = NSPasteboardItem()
        item.setDataProvider(tracker, forTypes: [.string])
        // Tells clipboard managers not to record (or eagerly read) this temporary entry.
        item.setString("", forType: transientType)
        pasteboard.clearContents()
        pasteboard.writeObjects([item])
        let ourChange = pasteboard.changeCount

        try? await Task.sleep(for: .milliseconds(60))
        postCommandV()

        let clock = ContinuousClock()
        let deadline = clock.now + pasteTimeout
        while !tracker.requested, clock.now < deadline {
            try? await Task.sleep(for: .milliseconds(20))
        }
        // Someone else changed the clipboard meanwhile: leave their content alone.
        guard pasteboard.changeCount == ourChange else { return tracker.requested ? .pasted : .notPasted }

        if tracker.requested {
            // Let the app finish reading any other representations before swapping the clipboard.
            try? await Task.sleep(for: .milliseconds(150))
            guard pasteboard.changeCount == ourChange else { return .pasted }
            if restoreClipboard {
                pasteboard.clearContents()
                if !saved.isEmpty { pasteboard.writeObjects(saved) }
            } else {
                copyToClipboard(text)
            }
            return .pasted
        }
        // Nothing pasted: replace the promise with a plain copy so the text stays available (and
        // clipboard managers may record it) after this app stops serving it.
        copyToClipboard(text)
        return .notPasted
    }

    static func copyToClipboard(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    /// Presses Return in the frontmost app, after a paste, to send what was inserted.
    @MainActor
    static func pressReturn() async {
        // Let the app take in the pasted text first (web apps update their state asynchronously).
        try? await Task.sleep(for: .milliseconds(120))
        postKey(CGKeyCode(kVK_Return), flags: [])
    }

    private static func snapshot(_ pasteboard: NSPasteboard) -> [NSPasteboardItem] {
        (pasteboard.pasteboardItems ?? []).map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) { copy.setData(data, forType: type) }
            }
            return copy
        }
    }

    private static func postCommandV() {
        postKey(CGKeyCode(kVK_ANSI_V), flags: .maskCommand)
    }

    /// Explicit flags, so a modifier still held (or stuck) doesn't turn the key into a shortcut.
    private static func postKey(_ key: CGKeyCode, flags: CGEventFlags) {
        let source = CGEventSource(stateID: .privateState)
        guard let down = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false) else { return }
        down.flags = flags
        up.flags = flags
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }
}
