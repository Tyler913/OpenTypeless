using System.Diagnostics;

namespace TypelessCore;

/// <summary>
/// Editing the text selected in another app by voice ("command mode"): while holding the dictation hotkey the user
/// presses the command chord (see <see cref="ChordKeys"/>), the selection is read (see <see cref="SelectionReader"/>),
/// they speak an instruction ("make this more formal", "把这段改成要点列表"), and the selection is replaced with the
/// rewrite, pasted over it so the app's own undo takes it back. A question about the text ("what does this mean") is
/// answered instead (see <see cref="Reply"/>), and the answer shown and copied, never pasted over the selection.
///
/// Only ever on its chord: Aqua's edit mode switches on whenever text is selected as the dictation starts, which turns a
/// selection left over by accident into an instruction and the dictation into a rewrite of it. Without the chord, nothing
/// is read and a dictation is what it always was. The same as the macOS app's.
/// </summary>
public static class SelectionCommand
{
    /// <summary>
    /// The keys (Windows virtual-key codes) of the command chord <paramref name="command"/>, pressed while the hotkey is
    /// held; none while translation mode (<paramref name="translation"/>, null while it's off) has the same chord: then a
    /// press couldn't say which mode it meant, and translation, there first, keeps it. A modifier-only hotkey's own key is
    /// never its chord.
    /// </summary>
    public static IReadOnlySet<int> ChordKeys(TranslationChord command, TranslationChord? translation, int? excludingHotkey) =>
        command == translation ? new HashSet<int>() : command.KeyCodes(excludingHotkey);

    /// <summary>
    /// The longest selection that can be edited, in characters (as Swift counts them, see <see cref="TextMetrics"/>). Aqua's
    /// edit mode has the same cap: a longer one takes long to stream back, and a rewrite cut off half way can't go over the
    /// user's text.
    /// </summary>
    public const int MaxCharacters = 6000;

    /// <summary>
    /// What an edit of a selection saves as its mode in History (see <see cref="TranslationTarget.RecordMode"/>). Only
    /// its instruction is saved, as the transcript, and the selection's length: neither the selection nor the rewrite (or the
    /// answer about it), which can be anything the user had open (a contract, a private message), while History keeps
    /// entries for days.
    /// </summary>
    public const string RecordMode = "command";

    /// <summary>Whether a History record's mode is an edit of a selection.</summary>
    public static bool IsCommand(string? mode) => mode == RecordMode;

    /// <summary>
    /// The line an answer to a question about the selection starts with (OpenWhispr's selection editing marks questions the
    /// same way).
    /// </summary>
    public const string AnswerMarker = "[[ANSWER]]";

    /// <summary>What came back for an instruction.</summary>
    public abstract record Reply
    {
        /// <summary>The text that replaces the selection.</summary>
        public sealed record Rewrite(string Text) : Reply;
        /// <summary>An answer to a question about the selection: shown and copied, and the selection left as it is.</summary>
        public sealed record Answer(string Text) : Reply;
    }

    /// <summary>
    /// The model's output read as a rewrite, or, when it starts with <see cref="AnswerMarker"/>, as an answer — unless
    /// <paramref name="selection"/> starts with the marker itself: its rewrite does too.
    /// </summary>
    public static Reply ReplyOf(string output, string selection = "")
    {
        var trimmed = output.Trim();
        if (!StartsWithMarker(trimmed) || StartsWithMarker(selection.Trim())) return new Reply.Rewrite(trimmed);
        return new Reply.Answer(trimmed[AnswerMarker.Length..].Trim());
    }

    private static bool StartsWithMarker(string text) =>
        text.Length >= AnswerMarker.Length && text[..AnswerMarker.Length].ToUpperInvariant() == AnswerMarker;

    /// <summary>
    /// Whether <paramref name="rewrite"/> is the selection as it was (white space and line endings aside): the model found
    /// nothing to change, or nothing it could, and there's nothing to paste.
    /// </summary>
    public static bool IsUnchanged(string selection, string rewrite) => Normalized(selection) == Normalized(rewrite);

    /// <summary>
    /// <paramref name="rewrite"/> with the white space the selection started and ended with (a selection of whole lines
    /// ends in a line break; one of an indented line starts with its indent), which models leave out, so the text around
    /// it stays as it was; and with the selection's line endings ("\r\n" in a Windows document).
    /// </summary>
    public static string Replacement(string selection, string rewrite)
    {
        var body = rewrite.Trim();
        if (body.Length == 0) return rewrite;
        if (selection.Contains("\r\n", StringComparison.Ordinal)) body = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var leading = 0;
        while (leading < selection.Length && char.IsWhiteSpace(selection[leading])) leading++;
        var trailing = 0;
        while (trailing < selection.Length - leading && char.IsWhiteSpace(selection[selection.Length - 1 - trailing])) trailing++;
        return selection[..leading] + body + selection[(selection.Length - trailing)..];
    }

    /// <summary>Where the result of an edit goes.</summary>
    public abstract record Delivery
    {
        /// <summary>Pasted over the selection.</summary>
        public sealed record Paste : Delivery;
        /// <summary>To the clipboard instead.</summary>
        public sealed record Clipboard(ClipboardReason Reason) : Delivery;
    }

    public enum ClipboardReason
    {
        /// <summary>The screen locked meanwhile.</summary>
        Locked,
        /// <summary>Another app or window is in front now.</summary>
        MovedAway,
        /// <summary>The selection isn't what was read any more: pasting would replace something else.</summary>
        SelectionChanged,
        /// <summary>What has focus can't take text (the selection was in a web page, say).</summary>
        NotEditable,
    }

    /// <summary>
    /// Whether a rewrite can be pasted over the selection: only into the same window (and tab, by its title), still
    /// showing the same selection, and into something that takes text. The selection is read again where it was read from
    /// UI Automation in the first place (copying presses keys, and the clipboard and UI Automation may disagree on line
    /// breaks); where it isn't, any click or key that reached an app since it was read (<paramref name="inputSinceRead"/>)
    /// may have moved it, in the same field, and nothing is pasted over it. <paramref name="selectionNow"/>: the selection
    /// read again from UI Automation, null when it can't be.
    /// </summary>
    public static Delivery DeliveryOf(bool canPaste, bool sameWindow, string original, SelectionSource source,
                                      string? selectionNow, bool inputSinceRead, bool editable)
    {
        if (!canPaste) return new Delivery.Clipboard(ClipboardReason.Locked);
        if (!sameWindow) return new Delivery.Clipboard(ClipboardReason.MovedAway);
        if (source == SelectionSource.Accessibility && selectionNow != null)
        {
            if (Normalized(selectionNow) != Normalized(original)) return new Delivery.Clipboard(ClipboardReason.SelectionChanged);
        }
        else if (inputSinceRead)
        {
            return new Delivery.Clipboard(ClipboardReason.SelectionChanged);
        }
        return editable ? new Delivery.Paste() : new Delivery.Clipboard(ClipboardReason.NotEditable);
    }

    private static string Normalized(string text) => text.Replace("\r\n", "\n").Trim();

    // MARK: - Running the instruction

    /// <summary>
    /// The edit of the routes' selection (their options' <see cref="PolishOptions.Selection"/>) by
    /// <paramref name="instruction"/>: in one request, never split in parts (the selection is one piece of text, at most
    /// <see cref="MaxCharacters"/>), hedged between the routes and asked a second time after a transient failure, like a
    /// clean-up. Throws when no answer came back.
    /// </summary>
    public static Task<SegmentedPolishResult> Run(string instruction, PolishRoute primary, PolishRoute? backup,
                                                  Action<string>? onPartial = null, CancellationToken cancellationToken = default)
    {
        var policy = new RetryPolicy(MaxAttempts: 2, RetriesTimeouts: false);
        return Run(instruction, primary.Options.Selection ?? "", (partial, token) => policy.Run(
                       (_, ct) => HedgedPolish.Run(instruction, primary, backup, cancellationToken: ct, onPartial: partial),
                       cancellationToken: token),
                   onPartial, cancellationToken);
    }

    /// <summary>
    /// Asks <paramref name="polish"/> once and puts its answer through <see cref="Prompts.CommandFallback"/>; the result has
    /// one part, whose fallback says why the answer can't be used (and then its text is empty). Throws when
    /// <paramref name="polish"/> does.
    /// </summary>
    public static async Task<SegmentedPolishResult> Run(string instruction, string selection,
                                                        Func<Action<string>?, CancellationToken, Task<HedgedPolishResult>> polish,
                                                        Action<string>? onPartial = null, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        HedgedPolishResult answer;
        try
        {
            answer = await polish(onPartial, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw ApiException.From(error);
        }
        if (cancellationToken.IsCancellationRequested) throw ApiException.Cancelled();
        var fallback = Prompts.CommandFallback(answer.Result.Text, selection, answer.Result.Truncated);
        var part = new SegmentedPolishResult.Part(instruction, answer, fallback, null);
        return new SegmentedPolishResult(fallback == null ? answer.Result.Text : "", [part], answer.FirstTokenSeconds,
                                         clock.Elapsed.TotalSeconds);
    }
}

// MARK: - Reading the selection

/// <summary>How the selection was read.</summary>
public enum SelectionSource
{
    /// <summary>
    /// From UI Automation (<c>TextPattern.GetSelection</c>) or, on macOS, Accessibility: nothing is pressed, and the
    /// clipboard is left alone.
    /// </summary>
    Accessibility,
    /// <summary>macOS only: the app's own Edit ▸ Copy, pressed through Accessibility.</summary>
    MenuCopy,
    /// <summary>Ctrl+C / ⌘C, the clipboard put back afterwards.</summary>
    CopyShortcut,
}

/// <summary>What one way of reading the selection found.</summary>
public abstract record SelectionProbe
{
    public sealed record Text(string Value) : SelectionProbe;
    /// <summary>It could tell: nothing is selected (a text field with an empty selection).</summary>
    public sealed record Empty : SelectionProbe;
    /// <summary>It couldn't tell (no UI Automation text pattern, nothing copied in time).</summary>
    public sealed record Unavailable : SelectionProbe;
}

/// <summary>What kind of app the selection is in, as far as reading it goes.</summary>
public enum SelectionApp
{
    Ordinary,
    /// <summary>
    /// A terminal: a rewrite pasted there would be typed in, line breaks and all, and run as commands; and Ctrl+C
    /// interrupts the program running there instead of copying.
    /// </summary>
    Terminal,
    /// <summary>
    /// A code editor whose copy shortcut copies the whole line when nothing is selected (VS Code, Cursor, JetBrains IDEs,
    /// Sublime Text, Zed, Visual Studio, Notepad++, Pulsar): there only a selection UI Automation reports counts, or a line the user never selected would be
    /// rewritten.
    /// </summary>
    LineCopyEditor,
}

public static class SelectionApps
{
    /// <summary>Windows editors that copy the line without a selection, by executable name (lower case, no ".exe").</summary>
    private static readonly HashSet<string> LineCopyEditors =
    [
        "code", "code - insiders", "codium", "cursor", "windsurf", "zed", "sublime_text", "idea64", "idea", "studio64",
        "pycharm64", "webstorm64", "rider64", "clion64", "goland64", "phpstorm64", "rubymine64", "datagrip64", "fleet",
        "devenv", "notepad++", "pulsar", "atom", "kate",
    ];

    /// <summary>
    /// The Windows app whose executable is <paramref name="exe"/> (a full path, or null when it can't be read);
    /// <paramref name="terminal"/>: the window is a terminal by its class or executable (see the app's FocusProbe). Every
    /// JetBrains IDE lives under a JetBrains folder. The path is taken apart by hand, so the tests read Windows paths the
    /// same on Linux.
    /// </summary>
    public static SelectionApp Windows(string? exe, bool terminal)
    {
        if (terminal) return SelectionApp.Terminal;
        if (exe == null) return SelectionApp.Ordinary;
        var name = exe[(exe.LastIndexOfAny(new[] { '\\', '/' }) + 1)..].ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        if (LineCopyEditors.Contains(name) || exe.Contains("\\JetBrains\\", StringComparison.OrdinalIgnoreCase))
        {
            return SelectionApp.LineCopyEditor;
        }
        return SelectionApp.Ordinary;
    }
}

/// <summary>Why a selection can't be edited by voice.</summary>
public abstract record SelectionRefusal
{
    public sealed record NoSelection : SelectionRefusal;
    public sealed record Terminal : SelectionRefusal;
    /// <summary>A password field.</summary>
    public sealed record SecureField : SelectionRefusal;
    /// <summary>A line-copy editor (see <see cref="SelectionApp.LineCopyEditor"/>) whose selection UI Automation can't read.</summary>
    public sealed record UnreadableInEditor : SelectionRefusal;
    /// <summary>Over <see cref="SelectionCommand.MaxCharacters"/>; how many characters it has.</summary>
    public sealed record TooLong(int Count) : SelectionRefusal;
}

/// <summary>The selection read, or why it can't be edited.</summary>
public abstract record SelectionRead
{
    public sealed record Selection(string Text, SelectionSource Source) : SelectionRead;
    public sealed record Refused(SelectionRefusal Reason) : SelectionRead;
}

/// <summary>
/// Reads the selection the ways SelectedTextKit (behind VoiceInk's selected-text reading) does, in order: UI Automation /
/// Accessibility, which presses nothing; the app's Edit ▸ Copy (macOS only); and last the copy shortcut, with the clipboard
/// put back afterwards. Terminals and password fields are refused before anything is read, and a line-copy editor is only
/// read through UI Automation.
/// </summary>
public static class SelectionReader
{
    /// <param name="menuCopy">
    /// Null where there's no menu bar to press Copy in (Windows). A strategy that finds nothing hands over to the next,
    /// except a Copy menu item that's disabled: then the app itself says nothing is selected.
    /// </param>
    public static async Task<SelectionRead> Read(SelectionApp app, bool isSecure, Func<Task<SelectionProbe>> accessibility,
                                                 Func<Task<SelectionProbe>>? menuCopy, Func<Task<SelectionProbe>> copyShortcut)
    {
        if (app == SelectionApp.Terminal) return new SelectionRead.Refused(new SelectionRefusal.Terminal());
        if (isSecure) return new SelectionRead.Refused(new SelectionRefusal.SecureField());
        if (await accessibility() is SelectionProbe.Text read) return Checked(read.Value, SelectionSource.Accessibility);
        if (app == SelectionApp.LineCopyEditor) return new SelectionRead.Refused(new SelectionRefusal.UnreadableInEditor());
        if (menuCopy != null)
        {
            switch (await menuCopy())
            {
                case SelectionProbe.Text menu: return Checked(menu.Value, SelectionSource.MenuCopy);
                case SelectionProbe.Empty: return new SelectionRead.Refused(new SelectionRefusal.NoSelection());
            }
        }
        if (await copyShortcut() is SelectionProbe.Text copied) return Checked(copied.Value, SelectionSource.CopyShortcut);
        return new SelectionRead.Refused(new SelectionRefusal.NoSelection());
    }

    /// <summary>White space alone is no selection; a selection over the cap is refused.</summary>
    internal static SelectionRead Checked(string text, SelectionSource source)
    {
        if (text.Trim().Length == 0) return new SelectionRead.Refused(new SelectionRefusal.NoSelection());
        var count = TextMetrics.CharacterCount(text);
        if (count > SelectionCommand.MaxCharacters) return new SelectionRead.Refused(new SelectionRefusal.TooLong(count));
        return new SelectionRead.Selection(text, source);
    }
}
