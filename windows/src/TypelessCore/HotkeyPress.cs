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
