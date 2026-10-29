namespace TypelessCore;

/// <summary>
/// "Finish and send": the key that, pressed while a dictation is recording, ends it and, once the text is pasted, is
/// pressed in that app to send it. Enter on its own, with Shift or with Ctrl (Return, ⇧Return, ⌘Return on macOS), since
/// chat boxes differ in which one sends; or none, the default. Only for the dictation it ended, and only where its text
/// was pasted: whatever sends the text to the clipboard instead (another window in front, the session locking, Esc)
/// sends nothing. "Press Enter after inserting", which sends every dictation, is a separate setting.
///
/// There is deliberately no spoken "send it": a send can't be taken back, and words in a dictation are too easily meant
/// as part of the text.
/// </summary>
public enum SendKey
{
    Off,
    Enter,
    ShiftEnter,
    /// <summary>Ctrl + Enter on Windows, ⌘Return on macOS: the modifier the platform's shortcuts are made with.</summary>
    CommandEnter,
}

/// <summary>The platform's codes for the keys a send key is made of.</summary>
/// <param name="ReturnKeys">Enter, and Enter on the keypad where it has a code of its own (on Windows it doesn't).</param>
/// <param name="Command">Ctrl on Windows, ⌘ on macOS.</param>
public sealed record SendKeyCodes(IReadOnlySet<int> ReturnKeys, int Shift, int Command);

public static class SendKeys
{
    public static readonly SendKey[] All = Enum.GetValues<SendKey>();

    /// <summary>
    /// A send key this soon after the dictation hotkey went down, while it's still held, was pressed together with it as
    /// a key combination (Right Ctrl + Enter…), too soon for anything to have been said: it's left to the focused app, as
    /// it always was. The same as the shortest recording that isn't thrown away.
    /// </summary>
    public const double CombinationWindow = 0.4;

    /// <summary>
    /// Whether a send key at <paramref name="time"/> came with the hotkey pressed at <paramref name="hotkeyPressedAt"/>
    /// and still held (see <see cref="CombinationWindow"/>). Times as in <see cref="HotkeyPress"/>.
    /// </summary>
    public static bool IsCombination(double hotkeyPressedAt, double time) => time - hotkeyPressedAt < CombinationWindow;

    /// <summary>The name it's saved under, the same as on macOS.</summary>
    public static string RawValue(this SendKey key) => key switch
    {
        SendKey.Enter => "enter",
        SendKey.ShiftEnter => "shiftEnter",
        SendKey.CommandEnter => "commandEnter",
        _ => "off",
    };

    /// <summary>The saved choice; off for none, or for one this version can't read (saved by a newer one, or damaged).</summary>
    public static SendKey Parse(string? raw)
    {
        foreach (var key in All)
        {
            if (key.RawValue() == raw) return key;
        }
        return SendKey.Off;
    }

    /// <summary>The modifiers held with Enter, in the platform's own bits; null when off.</summary>
    public static int? Modifiers(this SendKey key, SendKeyCodes codes) => key switch
    {
        SendKey.Enter => 0,
        SendKey.ShiftEnter => codes.Shift,
        SendKey.CommandEnter => codes.Command,
        _ => null,
    };

    /// <summary>
    /// Whether a dictation hotkey that is a key combination on <paramref name="keyCode"/> takes this send key's presses: the
    /// hotkey's keys are read first, so that key (and, while the hotkey is held, any press of it) is the hotkey's, and the
    /// send key would never fire. Settings says so.
    /// </summary>
    public static bool IsTakenByHotkey(this SendKey key, int keyCode, bool isModifierOnly, SendKeyCodes codes) =>
        key != SendKey.Off && !isModifierOnly && codes.ReturnKeys.Contains(keyCode);

    /// <summary>
    /// Whether <paramref name="keyCode"/> going down with <paramref name="modifiers"/> held (only those shortcuts are made
    /// of) is this send key. <paramref name="hotkeyModifiers"/>: those of the dictation hotkey while it's held (none
    /// hands-free), which may be down as well and then don't count: Enter pressed while holding Right Alt (or right ⌘) to
    /// talk, or the Alt of Alt + Space, is still Enter. Any other modifier makes it another key: Alt + Enter is left to the
    /// focused app.
    /// </summary>
    public static bool Matches(this SendKey key, int keyCode, int modifiers, int hotkeyModifiers, SendKeyCodes codes)
    {
        if (key.Modifiers(codes) is not { } wanted || !codes.ReturnKeys.Contains(keyCode)) return false;
        return (modifiers & wanted) == wanted && (modifiers & ~wanted & ~hotkeyModifiers) == 0;
    }
}
