namespace TypelessCore.Tests;

/// <summary>Action shortcuts: which action a key fires, which shortcuts are refused, and what "Paste last dictation" pastes.</summary>
public class ShortcutsTests
{
    // Windows codes: V is 0x56, Esc 0x1B, Space 0x20, Right Alt 0xA5; Ctrl 1, Alt 2, Shift 4 (HotkeyModifiers).
    private const int Control = 1, Alt = 2, Shift = 4;
    private static readonly KeyChord PasteChord = new(0x56, Alt | Shift);
    private static readonly KeyChord RightAlt = new(0xA5, isModifierOnly: true);

    private static Dictionary<ShortcutAction, KeyChord> Paste(KeyChord chord) => new() { [ShortcutAction.PasteLastDictation] = chord };

    [Fact]
    public void AKeyFiresTheActionWithExactlyItsModifiers()
    {
        var map = new ShortcutMap(Paste(PasteChord));
        Assert.Equal(ShortcutAction.PasteLastDictation, map.ActionFor(0x56, Alt | Shift));
        Assert.Null(map.ActionFor(0x56, Alt));
        Assert.Null(map.ActionFor(0x56, Control | Alt | Shift));
        Assert.Null(map.ActionFor(0x57, Alt | Shift));
        Assert.Null(ShortcutMap.Empty.ActionFor(0x56, Alt | Shift));
    }

    [Fact]
    public void AFunctionKeyAloneCanBeAShortcut()
    {
        var map = new ShortcutMap(Paste(new KeyChord(0x78))); // F9
        Assert.Equal(ShortcutAction.PasteLastDictation, map.ActionFor(0x78, 0));
        Assert.Null(map.ActionFor(0x78, Control));
    }

    [Fact]
    public void AModifierOnItsOwnNeverFires()
    {
        var map = new ShortcutMap(Paste(new KeyChord(0xA3, isModifierOnly: true)));
        Assert.True(map.IsEmpty);
        Assert.Null(map.ActionFor(0xA3, 0));
        // A modifier on its own carries no modifiers to compare.
        Assert.Equal(0, new KeyChord(0xA3, Control, isModifierOnly: true).Modifiers);
    }

    [Fact]
    public void ShortcutsThatClashAreRefused()
    {
        var map = ShortcutMap.Empty;
        Assert.Null(map.Conflict(PasteChord, ShortcutAction.PasteLastDictation, RightAlt, 0x1B));
        Assert.Equal(new ShortcutConflict.ModifierOnly(),
                     map.Conflict(new KeyChord(0xA2, isModifierOnly: true), ShortcutAction.PasteLastDictation, RightAlt, 0x1B));
        Assert.Equal(new ShortcutConflict.Escape(), map.Conflict(new KeyChord(0x1B, Shift), ShortcutAction.PasteLastDictation, RightAlt, 0x1B));
        var altSpace = new KeyChord(0x20, Alt);
        Assert.Equal(new ShortcutConflict.Dictation(), map.Conflict(altSpace, ShortcutAction.PasteLastDictation, altSpace, 0x1B));
        // The same key with other modifiers is a different shortcut.
        Assert.Null(map.Conflict(new KeyChord(0x20, Alt | Shift), ShortcutAction.PasteLastDictation, altSpace, 0x1B));
        // Recording an action's own shortcut again is fine.
        var set = new ShortcutMap(Paste(PasteChord));
        Assert.Null(set.Conflict(PasteChord, ShortcutAction.PasteLastDictation, RightAlt, 0x1B));
    }

    [Fact]
    public void ActionsAreSavedUnderTheMacNames()
    {
        Assert.Equal("pasteLastDictation", ShortcutAction.PasteLastDictation.RawValue());
        Assert.Equal(ShortcutAction.PasteLastDictation, ShortcutActions.Parse("pasteLastDictation"));
        Assert.Null(ShortcutActions.Parse("somethingElse"));
    }

    private static LastDictation.Entry Entry(HistoryPruning.Kind kind, bool text = true, bool audio = true) => new(kind, text, audio);

    [Fact]
    public void PastesTheNewestFinishedDictation()
    {
        Assert.Equal(new LastDictation.Choice.Paste(0),
                     LastDictation.Choose([Entry(HistoryPruning.Kind.Finished), Entry(HistoryPruning.Kind.Finished)]));
        // Under way, cancelled with Esc, or no speech heard: passed over.
        Assert.Equal(new LastDictation.Choice.Paste(3),
                     LastDictation.Choose([Entry(HistoryPruning.Kind.InProgress), Entry(HistoryPruning.Kind.Cancelled),
                                           Entry(HistoryPruning.Kind.Finished, text: false), Entry(HistoryPruning.Kind.Finished)]));
        Assert.Equal(new LastDictation.Choice.Nothing(), LastDictation.Choose([]));
        Assert.Equal(new LastDictation.Choice.Nothing(),
                     LastDictation.Choose([Entry(HistoryPruning.Kind.Cancelled), Entry(HistoryPruning.Kind.Finished, text: false)]));
    }

    [Fact]
    public void AFailedNewestDictationIsRetriedInstead()
    {
        Assert.Equal(new LastDictation.Choice.Retry(0),
                     LastDictation.Choose([Entry(HistoryPruning.Kind.Failed), Entry(HistoryPruning.Kind.Finished)]));
        Assert.Equal(new LastDictation.Choice.Retry(1),
                     LastDictation.Choose([Entry(HistoryPruning.Kind.Cancelled), Entry(HistoryPruning.Kind.Failed, text: false)]));
        // Its recording is gone: the newest finished one instead.
        Assert.Equal(new LastDictation.Choice.Paste(1),
                     LastDictation.Choose([Entry(HistoryPruning.Kind.Failed, audio: false), Entry(HistoryPruning.Kind.Finished)]));
        // An older failure doesn't stand in the way of a newer dictation that worked.
        Assert.Equal(new LastDictation.Choice.Paste(0),
                     LastDictation.Choose([Entry(HistoryPruning.Kind.Finished), Entry(HistoryPruning.Kind.Failed)]));
    }
}
