using System.Diagnostics.CodeAnalysis;

namespace TypelessCore;

/// <summary>
/// How a press of the dictation hotkey is read: a hold is push-to-talk, a tap toggles hands-free mode, and another key
/// soon after the press makes it a key combination instead.
///
/// Times are the key events' own, in seconds on one clock that doesn't jump (Windows: the tick count; macOS:
/// <c>ProcessInfo.systemUptime</c>). The app may only get to the release late, e.g. while a Bluetooth microphone takes
/// a second to open, and that wait mustn't turn a hold into a tap (or a tap into a hold).
/// </summary>
public static class HotkeyPress
{
    /// <summary>A press shorter than this toggles hands-free mode instead of push-to-talk.</summary>
    public const double TapThreshold = 0.35;
    /// <summary>Another key this soon after the press makes it a combination (Right Ctrl + C…), which cancels the dictation.</summary>
    public const double ComboWindow = 1.0;
    /// <summary>A key event stamped longer ago than this (or in the future, or not at all) came from software whose clock can't be trusted.</summary>
    internal const double MaxEventAge = 10;

    /// <summary>Whether a press released at <paramref name="releasedAt"/> was a tap (hands-free) rather than a hold (push-to-talk).</summary>
    public static bool IsTap(double pressedAt, double releasedAt) => releasedAt - pressedAt < TapThreshold;

    /// <summary>Whether another key at <paramref name="otherKeyAt"/> came soon enough after the press to make it a combination.</summary>
    public static bool IsCombo(double pressedAt, double otherKeyAt) => otherKeyAt - pressedAt < ComboWindow;

    /// <summary>The event's own time, or <paramref name="now"/> when its stamp isn't believable: keys sent by other software may carry none.</summary>
    public static double EventTime(double stamp, double now) => stamp > 0 && stamp <= now && now - stamp < MaxEventAge ? stamp : now;

    /// <summary>
    /// Whether a key going down is kept from the focused app because a chord modifier's press was swallowed and it's still
    /// held. Let through, the key would reach the app without that modifier (a swallowed key never reaches the key
    /// state), as another shortcut altogether: with Ctrl the translation chord, Win + Ctrl + Q as Win + Q. Its auto-repeats
    /// and release go with it. Not a modifier (which only changes what's held), a chord key, or the repeat of a key pressed
    /// before the chord, whose press the app has seen and whose release it must see too.
    /// </summary>
    public static bool KeepsFromApp(int keyCode, bool repeat, bool isModifier, IReadOnlySet<int> chordKeys, bool chordModifierSwallowed) =>
        chordModifierSwallowed && !repeat && !isModifier && !chordKeys.Contains(keyCode);

    /// <summary>
    /// A low-level keyboard hook's event time (the 32-bit millisecond tick count, which wraps every 49.7 days) as
    /// seconds on the 64-bit tick clock <paramref name="nowTicks"/> (<see cref="Environment.TickCount64"/>) is read from.
    /// </summary>
    public static double TickTime(uint eventTicks, long nowTicks)
    {
        // How long ago the event was, from the low 32 bits of both: right across a wrap too.
        var age = unchecked((uint)nowTicks - eventTicks);
        return EventTime((nowTicks - age) / 1000.0, nowTicks / 1000.0);
    }
}

/// <summary>What another key, pressed while the dictation hotkey is held, does to the recording.</summary>
public abstract record OtherKeyOutcome<TMode>
{
    /// <summary>A registered chord: the recording goes on, in this mode.</summary>
    public sealed record SwitchMode(TMode Mode) : OtherKeyOutcome<TMode>;
    /// <summary>A key combination with the hotkey (Right Ctrl + C…): the dictation is cancelled.</summary>
    public sealed record Cancel : OtherKeyOutcome<TMode>;
    /// <summary>Nothing happens.</summary>
    public sealed record Ignore : OtherKeyOutcome<TMode>;
}

/// <summary>
/// Second keys that, pressed while the dictation hotkey is held, switch the recording's mode instead of making a key
/// combination that cancels it. Key codes are the platform's (see <see cref="KeyChord"/>); the app keeps the registered
/// keys from the focused app. With none registered, every other key is read as it always was.
/// </summary>
public sealed class ModeChords<TMode> where TMode : notnull
{
    private readonly Dictionary<int, TMode> _modes = new();

    public IReadOnlySet<int> Keys => _modes.Keys.ToHashSet();
    public bool IsEmpty => _modes.Count == 0;

    public void Register(int keyCode, TMode mode) => _modes[keyCode] = mode;

    public void Unregister(int keyCode) => _modes.Remove(keyCode);

    public bool TryGetMode(int keyCode, [MaybeNullWhen(false)] out TMode mode) => _modes.TryGetValue(keyCode, out mode);

    /// <summary>
    /// What <paramref name="keyCode"/>, pressed at <paramref name="at"/> while the hotkey pressed at
    /// <paramref name="pressedAt"/> is down, does. A chord works for as long as the hotkey is held; any other key only
    /// makes a combination soon after the press, and never once the recording is hands-free.
    /// </summary>
    public OtherKeyOutcome<TMode> OtherKey(int keyCode, double pressedAt, double at, bool handsFree)
    {
        if (_modes.TryGetValue(keyCode, out var mode)) return new OtherKeyOutcome<TMode>.SwitchMode(mode);
        return !handsFree && HotkeyPress.IsCombo(pressedAt, at)
            ? new OtherKeyOutcome<TMode>.Cancel()
            : new OtherKeyOutcome<TMode>.Ignore();
    }
}
