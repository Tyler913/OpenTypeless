import Foundation

/// Editing the text selected in another app by voice ("command mode"): while holding the dictation hotkey the user
/// presses the command chord (see `chordKeys`), the selection is read (see `SelectionReader`), they speak an instruction
/// ("make this more formal", "把这段改成要点列表"), and the selection is replaced with the rewrite, pasted over it so the
/// app's own undo takes it back. A question about the text ("what does this mean") is answered instead (see `Reply`),
/// and the answer shown and copied, never pasted over the selection.
///
/// Only ever on its chord: Aqua's edit mode switches on whenever text is selected as the dictation starts, which turns
/// a selection left over by accident into an instruction and the dictation into a rewrite of it. Without the chord,
/// nothing is read and a dictation is what it always was.
public enum SelectionCommand {
    /// The keys (macOS virtual key codes) of the command chord `command`, pressed while the hotkey is held; none while
    /// translation mode (`translation`, nil while it's off) has the same chord: then a press couldn't say which mode it
    /// meant, and translation, there first, keeps it. A modifier-only hotkey's own key is never its chord.
    public static func chordKeys(command: TranslationChord, translation: TranslationChord?,
                                 excludingHotkey hotkey: Int64?) -> Set<Int64> {
        command == translation ? [] : command.keyCodes(excludingHotkey: hotkey)
    }

    /// The longest selection that can be edited, in characters (as Swift counts them). Aqua's edit mode has the same cap:
    /// a longer one takes long to stream back, and a rewrite cut off half way can't go over the user's text.
    public static let maxCharacters = 6000

    /// What an edit of a selection saves as its `mode` in History (see `TranslationTarget.recordMode`). Only its
    /// instruction is saved, as the transcript, and the selection's length: neither the selection nor the rewrite (or the
    /// answer about it), which can be anything the user had open (a contract, a private message), while History keeps
    /// entries for days.
    public static let recordMode = "command"

    /// Whether a History record's `mode` is an edit of a selection.
    public static func isCommand(mode: String?) -> Bool { mode == recordMode }

    /// The line an answer to a question about the selection starts with (OpenWhispr's selection editing marks questions
    /// the same way).
    public static let answerMarker = "[[ANSWER]]"

    /// What came back for an instruction.
    public enum Reply: Equatable, Sendable {
        /// The text that replaces the selection.
        case rewrite(String)
        /// An answer to a question about the selection: shown and copied, and the selection left as it is.
        case answer(String)
    }

    /// The model's output read as a rewrite, or, when it starts with `answerMarker`, as an answer — unless `selection`
    /// starts with the marker itself: its rewrite does too.
    public static func reply(_ output: String, selection: String = "") -> Reply {
        let trimmed = output.trimmingCharacters(in: .whitespacesAndNewlines)
        guard startsWithMarker(trimmed), !startsWithMarker(selection.trimmingCharacters(in: .whitespacesAndNewlines))
        else { return .rewrite(trimmed) }
        return .answer(String(trimmed.dropFirst(answerMarker.count)).trimmingCharacters(in: .whitespacesAndNewlines))
    }

    private static func startsWithMarker(_ text: String) -> Bool {
        text.prefix(answerMarker.count).uppercased() == answerMarker
    }

    /// Whether `rewrite` is the selection as it was (white space and line endings aside): the model found nothing to
    /// change, or nothing it could, and there's nothing to paste.
    public static func isUnchanged(selection: String, rewrite: String) -> Bool {
        normalized(selection) == normalized(rewrite)
    }

    /// `rewrite` with the white space the selection started and ended with (a selection of whole lines ends in a line
    /// break; one of an indented line starts with its indent), which models leave out, so the text around it stays as it
    /// was; and with the selection's line endings ("\r\n" in a Windows document).
    public static func replacement(for selection: String, rewrite: String) -> String {
        var body = rewrite.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty else { return rewrite }
        if selection.contains("\r\n") {
            body = body.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\n", with: "\r\n")
        }
        let leading = selection.prefix { $0.isWhitespace }
        let rest = selection.dropFirst(leading.count)
        let trailing = rest.reversed().prefix { $0.isWhitespace }
        return String(leading) + body + String(trailing.reversed())
    }

    /// Where the result of an edit goes.
    public enum Delivery: Equatable, Sendable {
        /// Pasted over the selection.
        case paste
        /// To the clipboard instead.
        case clipboard(ClipboardReason)
    }

    public enum ClipboardReason: Equatable, Sendable {
        /// The screen locked meanwhile.
        case locked
        /// Another app or window is in front now.
        case movedAway
        /// The selection isn't what was read any more: pasting would replace something else.
        case selectionChanged
        /// What has focus can't take text (the selection was in a web page, say).
        case notEditable
    }

    /// Whether a rewrite can be pasted over the selection: only into the same window (and tab, by its title), still
    /// showing the same selection, and into something that takes text. The selection is read again where it was read
    /// from Accessibility / UI Automation in the first place (other ways of reading it press keys, and the clipboard and
    /// the accessibility tree may disagree on line breaks); where it isn't, any click or key that reached an app since
    /// it was read (`inputSinceRead`) may have moved it, in the same field, and nothing is pasted over it.
    /// `selectionNow`: the selection read again from Accessibility, nil when it can't be.
    public static func delivery(canPaste: Bool, sameWindow: Bool, original: String, source: SelectionSource,
                                selectionNow: String?, inputSinceRead: Bool, editable: Bool) -> Delivery {
        guard canPaste else { return .clipboard(.locked) }
        guard sameWindow else { return .clipboard(.movedAway) }
        if source == .accessibility, let selectionNow {
            if normalized(selectionNow) != normalized(original) { return .clipboard(.selectionChanged) }
        } else if inputSinceRead {
            return .clipboard(.selectionChanged)
        }
        return editable ? .paste : .clipboard(.notEditable)
    }

    private static func normalized(_ text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: Running the instruction

    /// The edit of the routes' selection (their options' `selection`) by `instruction`: in one request, never split in
    /// parts (the selection is one piece of text, at most `maxCharacters`), hedged between the routes and asked a second
    /// time after a transient failure, like a clean-up. Throws when no answer came back.
    public static func run(
        instruction: String,
        primary: PolishRoute,
        backup: PolishRoute?,
        onPartial: (@Sendable (String) -> Void)? = nil
    ) async throws -> SegmentedPolishResult {
        let policy = RetryPolicy(maxAttempts: 2, retriesTimeouts: false)
        return try await run(instruction: instruction, selection: primary.options.selection ?? "", polish: { partial in
            try await policy.run { _ in
                try await HedgedPolish.run(transcript: instruction, primary: primary, backup: backup, onPartial: partial)
            }
        }, onPartial: onPartial)
    }

    /// Asks `polish` once and puts its answer through `Prompts.commandFallback`; the result has one part, whose fallback
    /// says why the answer can't be used (and then its text is empty). Throws when `polish` does.
    public static func run(
        instruction: String,
        selection: String,
        polish: @Sendable ((@Sendable (String) -> Void)?) async throws -> HedgedPolishResult,
        onPartial: (@Sendable (String) -> Void)? = nil
    ) async throws -> SegmentedPolishResult {
        let start = ProcessInfo.processInfo.systemUptime
        let answer: HedgedPolishResult
        do {
            answer = try await polish(onPartial)
        } catch {
            throw APIError.from(error)
        }
        if Task.isCancelled { throw APIError.cancelled }
        let fallback = Prompts.commandFallback(output: answer.result.text, selection: selection,
                                               truncated: answer.result.truncated)
        let part = SegmentedPolishResult.Part(transcript: instruction, answer: answer, fallback: fallback, error: nil)
        return SegmentedPolishResult(text: fallback == nil ? answer.result.text : "", parts: [part],
                                     firstTokenSeconds: answer.firstTokenSeconds,
                                     totalSeconds: ProcessInfo.processInfo.systemUptime - start)
    }
}

// MARK: - Reading the selection

/// How the selection was read.
public enum SelectionSource: String, Sendable {
    /// From Accessibility (macOS `kAXSelectedTextAttribute`) or UI Automation (Windows `TextPattern.GetSelection`):
    /// nothing is pressed, and the clipboard is left alone.
    case accessibility
    /// macOS: the app's own Edit ▸ Copy, pressed through Accessibility (SelectedTextKit's menu action). No keys are
    /// sent, so neither the keyboard layout nor keys still held change what it does.
    case menuCopy
    /// ⌘C / Ctrl+C, the clipboard put back afterwards.
    case copyShortcut
}

/// What one way of reading the selection found.
public enum SelectionProbe: Equatable, Sendable {
    case text(String)
    /// It could tell: nothing is selected (a text field with an empty selection, a disabled Copy menu item).
    case empty
    /// It couldn't tell (no accessibility, no Copy menu item, nothing copied in time).
    case unavailable
}

/// What kind of app the selection is in, as far as reading it goes.
public enum SelectionApp: Equatable, Sendable {
    case ordinary
    /// A terminal: a rewrite pasted there would be typed in, line breaks and all, and run as commands; and Ctrl+C
    /// interrupts the program running there instead of copying.
    case terminal
    /// A code editor whose copy shortcut (and Edit ▸ Copy) copies the whole line when nothing is selected (VS Code,
    /// Cursor, JetBrains IDEs, Sublime Text, Zed, Nova, Pulsar; on Windows Visual Studio and Notepad++ too): there only a selection Accessibility reports counts, or a line the
    /// user never selected would be rewritten.
    case lineCopyEditor

    /// macOS terminals, by bundle identifier.
    static let macTerminals: Set<String> = [
        "com.apple.Terminal", "com.googlecode.iterm2", "dev.warp.Warp-Stable", "dev.warp.Warp", "dev.warp.Warp-Preview",
        "net.kovidgoyal.kitty", "io.alacritty", "org.alacritty", "com.github.wez.wezterm", "co.zeit.hyper",
        "com.mitchellh.ghostty", "org.tabby", "com.raphaelamorim.rio", "dev.commandline.waveterm",
    ]

    /// macOS editors that copy the line without a selection, by bundle identifier.
    static let macLineCopyEditors: Set<String> = [
        "com.microsoft.VSCode", "com.microsoft.VSCodeInsiders", "com.vscodium", "com.visualstudio.code.oss",
        "com.todesktop.230313mzl4w4u92", "com.exafunction.windsurf", "com.google.android.studio",
        "dev.zed.Zed", "dev.zed.Zed-Preview", "com.panic.Nova", "dev.pulsar-edit.pulsar", "com.github.atom",
        "org.kde.kate",
    ]

    /// The macOS app with this bundle identifier. Every JetBrains IDE and Sublime Text version shares its prefix.
    public static func mac(bundleID: String?) -> SelectionApp {
        guard let id = bundleID else { return .ordinary }
        if macTerminals.contains(id) { return .terminal }
        if macLineCopyEditors.contains(id) || id.hasPrefix("com.jetbrains.") || id.hasPrefix("com.sublimetext") {
            return .lineCopyEditor
        }
        return .ordinary
    }
}

/// Why a selection can't be edited by voice.
public enum SelectionRefusal: Equatable, Sendable {
    case noSelection
    case terminal
    /// A password field.
    case secureField
    /// A line-copy editor (see `SelectionApp.lineCopyEditor`) whose selection Accessibility can't read.
    case unreadableInEditor
    /// Over `SelectionCommand.maxCharacters`; how many characters it has.
    case tooLong(Int)
}

/// The selection read, or why it can't be edited.
public enum SelectionRead: Equatable, Sendable {
    case selection(String, SelectionSource)
    case refused(SelectionRefusal)
}

/// Reads the selection the ways SelectedTextKit (behind VoiceInk's selected-text reading) does, in order:
/// Accessibility, which presses nothing; the app's Edit ▸ Copy, pressed through Accessibility (macOS only); and last the
/// copy shortcut, with the clipboard put back afterwards. Terminals and password fields are refused before anything is
/// read, and a line-copy editor is only read through Accessibility.
public enum SelectionReader {
    /// `menuCopy`: nil where there's no menu bar to press Copy in (Windows). A strategy that finds nothing hands over to
    /// the next, except a Copy menu item that's disabled: then the app itself says nothing is selected.
    public static func read(
        app: SelectionApp,
        isSecure: Bool,
        accessibility: () async -> SelectionProbe,
        menuCopy: (() async -> SelectionProbe)?,
        copyShortcut: () async -> SelectionProbe
    ) async -> SelectionRead {
        if app == .terminal { return .refused(.terminal) }
        if isSecure { return .refused(.secureField) }
        if case let .text(text) = await accessibility() { return checked(text, .accessibility) }
        if app == .lineCopyEditor { return .refused(.unreadableInEditor) }
        if let menuCopy {
            switch await menuCopy() {
            case let .text(text): return checked(text, .menuCopy)
            case .empty: return .refused(.noSelection)
            case .unavailable: break
            }
        }
        if case let .text(text) = await copyShortcut() { return checked(text, .copyShortcut) }
        return .refused(.noSelection)
    }

    /// White space alone is no selection; a selection over the cap is refused.
    static func checked(_ text: String, _ source: SelectionSource) -> SelectionRead {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return .refused(.noSelection) }
        if text.count > SelectionCommand.maxCharacters { return .refused(.tooLong(text.count)) }
        return .selection(text, source)
    }
}
