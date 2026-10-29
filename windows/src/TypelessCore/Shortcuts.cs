namespace TypelessCore;

/// <summary>
/// What a shortcut of its own can do besides dictating; the dictation hotkey stays separate (see
/// <see cref="HotkeyPress"/>). An action fires as its keys go down, is ignored while a dictation is under way, and its
/// keys are kept from the focused app. None has a shortcut until the user records one. A new action is a new member here.
/// </summary>
public enum ShortcutAction
{
    /// <summary>Puts the newest finished dictation at the cursor again (see <see cref="LastDictation"/>).</summary>
    PasteLastDictation,
}

public static class ShortcutActions
{
    public static readonly ShortcutAction[] All = Enum.GetValues<ShortcutAction>();

    /// <summary>The name its shortcut is saved under, the same as on macOS.</summary>
    public static string RawValue(this ShortcutAction action) => action switch
    {
        ShortcutAction.PasteLastDictation => "pasteLastDictation",
        _ => action.ToString(),
    };

    public static ShortcutAction? Parse(string raw)
    {
        foreach (var action in All)
        {
            if (action.RawValue() == raw) return action;
        }
        return null;
    }
}

/// <summary>
/// A key and the modifiers held with it, in the platform's own codes (Windows: a virtual-key code and
/// <c>HotkeyModifiers</c>; macOS: a virtual key code and <c>CGEventFlags</c> bits), or a modifier key held on its own.
/// </summary>
public readonly record struct KeyChord
{
    public int KeyCode { get; }
    /// <summary>Only the modifiers shortcuts are made of (Ctrl Alt Shift Win, or ⌃⌥⇧⌘); none for a modifier on its own.</summary>
    public int Modifiers { get; }
    public bool IsModifierOnly { get; }

    public KeyChord(int keyCode, int modifiers = 0, bool isModifierOnly = false)
    {
        KeyCode = keyCode;
        Modifiers = isModifierOnly ? 0 : modifiers;
        IsModifierOnly = isModifierOnly;
    }
}

/// <summary>Why a key combination can't be an action's shortcut.</summary>
public abstract record ShortcutConflict
{
    /// <summary>A modifier on its own would fire with every other shortcut that uses it.</summary>
    public sealed record ModifierOnly : ShortcutConflict;
    /// <summary>Esc cancels a dictation (and is kept from the focused app meanwhile).</summary>
    public sealed record Escape : ShortcutConflict;
    /// <summary>It's the dictation hotkey.</summary>
    public sealed record Dictation : ShortcutConflict;
    /// <summary>It's already this action's shortcut.</summary>
    public sealed record Action(ShortcutAction Other) : ShortcutConflict;
}

/// <summary>The shortcuts the user gave to actions, and which action a key going down fires. Immutable.</summary>
public sealed class ShortcutMap
{
    public static readonly ShortcutMap Empty = new(new Dictionary<ShortcutAction, KeyChord>());

    public IReadOnlyDictionary<ShortcutAction, KeyChord> Chords { get; }

    /// <summary>A modifier on its own never makes a shortcut here (see <see cref="ShortcutConflict.ModifierOnly"/>), whatever was saved.</summary>
    public ShortcutMap(IReadOnlyDictionary<ShortcutAction, KeyChord> chords)
    {
        Chords = chords.Where(pair => !pair.Value.IsModifierOnly).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public bool IsEmpty => Chords.Count == 0;

    /// <summary>
    /// The action whose shortcut is this key with exactly these modifiers held (counting only those shortcuts are made
    /// of), if any: Ctrl + Alt + V doesn't also fire on Ctrl + Alt + Shift + V.
    /// </summary>
    public ShortcutAction? ActionFor(int keyCode, int modifiers)
    {
        var pressed = new KeyChord(keyCode, modifiers);
        foreach (var action in ShortcutActions.All)
        {
            if (Chords.TryGetValue(action, out var chord) && chord == pressed) return action;
        }
        return null;
    }

    /// <summary>Why <paramref name="chord"/> can't be the shortcut of <paramref name="action"/>, or null when it can.</summary>
    /// <param name="dictation">The dictation hotkey.</param>
    public ShortcutConflict? Conflict(KeyChord chord, ShortcutAction action, KeyChord dictation, int escapeKeyCode)
    {
        if (chord.IsModifierOnly) return new ShortcutConflict.ModifierOnly();
        if (chord.KeyCode == escapeKeyCode) return new ShortcutConflict.Escape();
        if (chord == dictation) return new ShortcutConflict.Dictation();
        foreach (var other in ShortcutActions.All)
        {
            if (other != action && Chords.TryGetValue(other, out var taken) && taken == chord) return new ShortcutConflict.Action(other);
        }
        return null;
    }
}

/// <summary>
/// Which dictation "Paste last dictation" puts at the cursor again: the newest that finished with text. When the newest
/// one failed and its recording is saved, that one is re-sent instead and pasted once it's done, as the tray panel's
/// Retry does: what the user is after is what they said last, not the dictation before it.
/// </summary>
public static class LastDictation
{
    /// <param name="HasText">Its text (cleaned up, or as transcribed) isn't empty.</param>
    /// <param name="IsCommand">
    /// An edit of a selection by voice (see <see cref="SelectionCommand"/>), not a dictation: its text replaced a selection,
    /// and without that selection (which isn't kept) it can't be run again.
    /// </param>
    public readonly record struct Entry(HistoryPruning.Kind Kind, bool HasText, bool HasAudio, bool IsCommand = false);

    public abstract record Choice
    {
        /// <summary>Paste the text of the entry at this index.</summary>
        public sealed record Paste(int Index) : Choice;
        /// <summary>Re-send the recording of the entry at this index, and paste what comes back.</summary>
        public sealed record Retry(int Index) : Choice;
        /// <summary>There's nothing to paste.</summary>
        public sealed record Nothing : Choice;
    }

    /// <summary>
    /// <paramref name="entries"/> are newest first, as History keeps them. Passed over: dictations still under way,
    /// cancelled ones (the user threw them away with Esc), ones that heard no speech, failed ones whose recording is gone,
    /// and edits of a selection by voice.
    /// </summary>
    public static Choice Choose(IReadOnlyList<Entry> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.IsCommand) continue;
            if (entry.Kind == HistoryPruning.Kind.Finished && entry.HasText) return new Choice.Paste(index);
            if (entry.Kind == HistoryPruning.Kind.Failed && entry.HasAudio) return new Choice.Retry(index);
        }
        return new Choice.Nothing();
    }
}
