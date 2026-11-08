import AppKit
import SwiftUI
import TypelessCore

/// The Scratchpad's notes on disk (`scratchpad.json` in the data folder, see `Scratchpad`). Typing is saved a second
/// after it stops, everything else at once; the file is written off the main thread, in order. Main thread only.
@MainActor
final class ScratchpadStore: ObservableObject {
    static let shared = ScratchpadStore(file: AppPaths.support.appendingPathComponent("scratchpad.json"))

    @Published private(set) var notes: Scratchpad
    /// nil for the snapshots' sample notes, which are never saved.
    private let file: URL?
    private var saveTimer: Timer?
    private let writer = DispatchQueue(label: "OpenTypeless.scratchpad", qos: .utility)

    init(file: URL?, notes: Scratchpad? = nil) {
        self.file = file
        self.notes = notes ?? file.map { Scratchpad.load(from: $0) } ?? Scratchpad()
    }

    /// A few notes for the UI snapshots (the user's own words, so not translated; the same on Windows).
    static func sample() -> ScratchpadStore {
        let now = Date()
        var notes = Scratchpad()
        let first = notes.add(now: now.addingTimeInterval(-86_400 * 3))
        notes.setText("Launch review on Thursday\n- Check the demo setup\n- Draft the release notes",
                      of: first.id, now: now.addingTimeInterval(-86_400 * 2))
        let second = notes.add(now: now.addingTimeInterval(-3_600))
        notes.setText("Call Sam back about the contract.", of: second.id, now: now.addingTimeInterval(-3_600))
        notes.capture("Send the weekly update to the team and mention the new launch date.",
                      day: CalendarDay(now).key, now: now.addingTimeInterval(-600))
        return ScratchpadStore(file: nil, notes: notes)
    }

    @discardableResult
    func add() -> String {
        let id = notes.add().id
        scheduleSave()
        return id
    }

    func setText(_ text: String, of id: String) {
        guard notes.note(id)?.text != text else { return }
        notes.setText(text, of: id)
        scheduleSave()
    }

    func delete(_ id: String) {
        notes.delete(id)
        save()
    }

    func restore(_ id: String) {
        notes.restore(id)
        save()
    }

    /// Drops the notes left empty, but `id`'s.
    func removeBlank(except id: String?) {
        guard notes.entries.contains(where: { $0.isBlank && $0.id != id }) else { return }
        notes.removeBlank(except: id)
    }

    /// A dictation that had nowhere to go, added to today's note (see `Scratchpad.capture`) and saved at once.
    func capture(_ text: String) {
        notes.capture(text, day: CalendarDay(Date()).key)
        save()
    }

    private func scheduleSave() {
        saveTimer?.invalidate()
        saveTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.save() }
        }
    }

    /// Writes what's there now (in the background; `flush` waits for it).
    func save() {
        saveTimer?.invalidate()
        saveTimer = nil
        guard let file, !notes.isUnreadable, let data = notes.jsonData() else { return }
        writer.async { try? data.write(to: file, options: .atomic) }
    }

    /// Saves, and waits until it's on disk: on quit.
    func flush() {
        if saveTimer != nil { save() }
        writer.sync {}
    }
}

/// What the Scratchpad window shows: the selected note, the editor (for dictation to go into), and the note just
/// deleted, which Undo brings back.
@MainActor
final class ScratchpadModel: ObservableObject {
    @Published var selection: String?
    @Published var deletedID: String?
    /// The editor's text view, while the window has one.
    weak var textView: NSTextView?
    /// Set when the window opens: the editor takes keyboard focus as soon as it's there.
    var wantsFocus = false

    /// Gives the editor keyboard focus, with the cursor at the end of the note.
    func focusEditor() {
        guard let textView, let window = textView.window else {
            wantsFocus = true
            return
        }
        wantsFocus = false
        window.makeFirstResponder(textView)
        let end = (textView.string as NSString).length
        textView.setSelectedRange(NSRange(location: end, length: 0))
        textView.scrollRangeToVisible(NSRange(location: end, length: 0))
    }
}

/// Opens the Scratchpad: a window of plain-text notes to dictate into when there's no text field to dictate into, and
/// where dictations with nowhere to go are kept (see `ScratchpadCapture`). Closing it keeps it for next time.
@MainActor
final class ScratchpadWindowController: NSObject, NSWindowDelegate {
    static let shared = ScratchpadWindowController(store: .shared)

    let store: ScratchpadStore
    let model = ScratchpadModel()
    private var window: NSWindow?

    init(store: ScratchpadStore) {
        self.store = store
    }

    /// Brings the window up. Opened anew, it starts a fresh note at the top (unless the newest one is still empty), with
    /// the cursor in it, ready to dictate.
    func show() {
        let wasOpen = window?.isVisible == true
        if window == nil {
            let window = Self.makeWindow(store: store, model: model)
            window.delegate = self
            if !window.setFrameUsingName(Self.frameName) { window.center() }
            window.setFrameAutosaveName(Self.frameName)
            self.window = window
        }
        if !wasOpen {
            model.deletedID = nil
            if let newest = store.notes.notes.first, newest.isBlank {
                model.selection = newest.id
            } else {
                model.selection = store.add()
            }
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        model.focusEditor()
    }

    private static let frameName = "Scratchpad"

    func windowWillClose(_ notification: Notification) {
        store.removeBlank(except: nil)
        store.save()
        model.deletedID = nil
        // Back to the app that was in front before, unless another of this app's windows is still open.
        let others = NSApp.windows.contains { $0 !== window && $0.isVisible && $0.canBecomeMain }
        if !others { NSApp.hide(nil) }
    }

    /// The editor, while dictation should go into it: this app in front, the Scratchpad its key window, the cursor in it.
    private var focusedEditor: NSTextView? {
        guard NSApp.isActive, let window, window.isKeyWindow, let textView = model.textView,
              window.firstResponder === textView else { return nil }
        return textView
    }

    /// Types `text` at the editor's cursor (over a selection), as one change Undo takes back, when the cursor is in the
    /// editor. Nothing goes through the clipboard: a paste into this app's own window would only be slower and less sure.
    /// With `fit`, the spacing and capitals are fitted to the text either side, as for a paste (see InsertionJoiner).
    /// False when the editor doesn't have the cursor.
    func insert(_ text: String, fit: Bool, vocabulary: [String]) -> Bool {
        guard let textView = focusedEditor else { return false }
        let range = textView.selectedRange()
        var inserted = text
        if fit {
            let string = textView.string
            let start = String.Index(utf16Offset: range.location, in: string)
            let end = String.Index(utf16Offset: range.location + range.length, in: string)
            inserted = InsertionJoiner.fit(text, before: String(string[..<start].suffix(12)),
                                           after: String(string[end...].prefix(8)),
                                           vocabulary: vocabulary, isCommonWord: EditWatcher.isCommonWord).text
        }
        textView.breakUndoCoalescing()
        textView.insertText(inserted, replacementRange: range)
        textView.breakUndoCoalescing()
        textView.scrollRangeToVisible(textView.selectedRange())
        return true
    }

    static func makeWindow(store: ScratchpadStore, model: ScratchpadModel) -> NSWindow {
        let split = NSSplitViewController()
        let sidebarHost = NSHostingController(rootView: ScratchpadSidebar(store: store, model: model))
        let detailHost = NSHostingController(rootView: ScratchpadDetail(store: store, model: model))
        sidebarHost.sizingOptions = []
        detailHost.sizingOptions = []
        let sidebar = NSSplitViewItem(sidebarWithViewController: sidebarHost)
        sidebar.minimumThickness = 220
        sidebar.maximumThickness = 320
        sidebar.canCollapse = false
        let detail = NSSplitViewItem(viewController: detailHost)
        detail.minimumThickness = 360
        split.addSplitViewItem(sidebar)
        split.addSplitViewItem(detail)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 780, height: 520),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.title = L("草稿本", "Scratchpad")
        window.toolbar = NSToolbar(identifier: "scratchpad")
        window.toolbarStyle = .unifiedCompact
        window.contentViewController = split
        window.setContentSize(NSSize(width: 780, height: 520))
        window.contentMinSize = NSSize(width: 600, height: 360)
        window.isReleasedWhenClosed = false
        return window
    }
}

/// The notes, the last edited first, each titled by its first line.
struct ScratchpadSidebar: View {
    @ObservedObject var store: ScratchpadStore
    @ObservedObject var model: ScratchpadModel
    @AppStorage(AppLanguage.defaultsKey) private var appLanguage: AppLanguage = .system

    var body: some View {
        List(selection: $model.selection) {
            ForEach(store.notes.notes) { note in
                VStack(alignment: .leading, spacing: 2) {
                    Text(note.isBlank ? L("新笔记", "New Note") : note.title)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(note.isBlank ? .secondary : .primary)
                        .lineLimit(1)
                    Text(note.modified.shortStamp)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
                .padding(.vertical, 2)
                .tag(note.id)
            }
        }
        .listStyle(.sidebar)
        .id(appLanguage)
        // Moving on from a note never typed in drops it.
        .onChange(of: model.selection) { _, selection in
            store.removeBlank(except: selection)
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            HStack(spacing: 8) {
                Text(L("草稿本", "Scratchpad")).font(.system(size: 14, weight: .semibold))
                Spacer()
                Button {
                    model.deletedID = nil
                    model.selection = store.add()
                    model.focusEditor()
                } label: {
                    Image(systemName: "square.and.pencil").font(.system(size: 14))
                }
                .buttonStyle(.borderless)
                .keyboardShortcut("n")
                .help(L("新建笔记", "New Note"))
                .accessibilityLabel(L("新建笔记", "New Note"))
            }
            .padding(.horizontal, 16)
            .padding(.top, 8)
            .padding(.bottom, 10)
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if let deleted = model.deletedID {
                HStack(spacing: 8) {
                    Text(L("笔记已删除", "Note deleted")).font(.system(size: 12)).foregroundStyle(.secondary)
                    Spacer()
                    Button(L("撤销", "Undo")) {
                        store.restore(deleted)
                        model.selection = deleted
                        model.deletedID = nil
                    }
                    .controlSize(.small)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
            }
        }
    }
}

/// The selected note: when it was edited, Copy and Delete, and the editor.
struct ScratchpadDetail: View {
    @ObservedObject var store: ScratchpadStore
    @ObservedObject var model: ScratchpadModel
    @AppStorage(AppLanguage.defaultsKey) private var appLanguage: AppLanguage = .system
    @State private var copied = false

    var body: some View {
        Group {
            if let id = model.selection, let note = store.notes.note(id), note.deleted == nil {
                VStack(spacing: 0) {
                    header(note)
                    Divider()
                    ScratchpadEditor(noteID: note.id, text: note.text, store: store, model: model)
                }
            } else {
                VStack(spacing: 12) {
                    Image(systemName: "note.text").font(.system(size: 30)).foregroundStyle(.tertiary)
                    Text(L("没有选中的笔记", "No note selected")).font(.system(size: 13)).foregroundStyle(.secondary)
                    Button(L("新建笔记", "New Note")) {
                        model.selection = store.add()
                        model.focusEditor()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .id(appLanguage)
    }

    private func header(_ note: ScratchpadNote) -> some View {
        HStack(spacing: 10) {
            Text(L("编辑于 \(note.modified.shortStamp)", "Edited \(note.modified.shortStamp)"))
                .font(.system(size: 11.5))
                .foregroundStyle(.secondary)
                .help(L("创建于 \(note.created.shortStamp)", "Created \(note.created.shortStamp)"))
            Spacer(minLength: 8)
            Button {
                TextInserter.copyToClipboard(note.text)
                copied = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { copied = false }
            } label: {
                Image(systemName: copied ? "checkmark" : "doc.on.doc")
            }
            .buttonStyle(.borderless)
            .disabled(note.isBlank)
            .help(L("复制笔记", "Copy Note"))
            .accessibilityLabel(L("复制笔记", "Copy Note"))
            Button {
                delete(note)
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .help(L("删除笔记", "Delete Note"))
            .accessibilityLabel(L("删除笔记", "Delete Note"))
        }
        .padding(.horizontal, 18)
        .frame(height: 38)
    }

    /// Takes the note off the list, with Undo under the list to bring it back; the next note down is selected.
    private func delete(_ note: ScratchpadNote) {
        let listed = store.notes.notes
        let index = listed.firstIndex(where: { $0.id == note.id }) ?? 0
        store.delete(note.id)
        let rest = store.notes.notes
        model.selection = rest.isEmpty ? nil : rest[min(index, rest.count - 1)].id
        // An empty note goes without a trace; one with text can be brought back.
        model.deletedID = note.isBlank ? nil : note.id
    }
}

/// The note's text in a plain-text `NSTextView`: undo, spelling and input methods as in any Mac text view, and a cursor
/// dictation can type at (see `ScratchpadWindowController.insert`).
struct ScratchpadEditor: NSViewRepresentable {
    let noteID: String
    let text: String
    let store: ScratchpadStore
    let model: ScratchpadModel

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.drawsBackground = false
        let textView = scroll.documentView as! NSTextView
        textView.isRichText = false
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.drawsBackground = false
        textView.font = .systemFont(ofSize: 14)
        textView.textContainerInset = NSSize(width: 14, height: 12)
        textView.delegate = context.coordinator
        textView.string = text
        textView.setAccessibilityLabel(L("笔记", "Note"))
        context.coordinator.noteID = noteID
        context.coordinator.store = store
        model.textView = textView
        if model.wantsFocus { DispatchQueue.main.async { model.focusEditor() } }
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let textView = scroll.documentView as? NSTextView else { return }
        model.textView = textView
        if context.coordinator.noteID != noteID {
            // Another note: its own text, its own undo.
            context.coordinator.noteID = noteID
            textView.string = text
            textView.undoManager?.removeAllActions()
            textView.setSelectedRange(NSRange(location: (text as NSString).length, length: 0))
        } else if textView.string != text, !textView.hasMarkedText() {
            // Changed from elsewhere (a dictation with nowhere to go, added to this note): the cursor stays put.
            let selection = textView.selectedRange()
            textView.string = text
            textView.undoManager?.removeAllActions()
            textView.setSelectedRange(NSRange(location: min(selection.location, (text as NSString).length), length: 0))
        }
        if model.wantsFocus { DispatchQueue.main.async { model.focusEditor() } }
    }

    @MainActor
    final class Coordinator: NSObject, NSTextViewDelegate {
        var noteID = ""
        weak var store: ScratchpadStore?

        func textDidChange(_ notification: Notification) {
            guard let textView = notification.object as? NSTextView else { return }
            store?.setText(textView.string, of: noteID)
        }
    }
}
