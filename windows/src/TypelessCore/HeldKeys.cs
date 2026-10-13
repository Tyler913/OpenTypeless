namespace TypelessCore;

/// <summary>
/// The keys physically down, as a low-level keyboard hook sees them. Such a hook gets no auto-repeat flag (macOS key
/// events carry one), and <c>GetAsyncKeyState</c> never hears of the presses it swallows, so both are told from the
/// hook's own events: a key-down for a key already down is an auto-repeat. A key seen down longer ago than
/// <see cref="TimeoutMs"/> without another key-down must have had its key-up missed (Windows drops a hook that ever times
/// out, without a word): keys repeat well within a second. Thread-safe.
/// </summary>
public sealed class HeldKeys
{
    public const uint TimeoutMs = 1500;

    /// <summary>Key → the millisecond tick count of its latest key-down.</summary>
    private readonly Dictionary<int, uint> _down = new();

    /// <summary>
    /// Notes <paramref name="key"/> going down or up at <paramref name="time"/> (the hook's 32-bit millisecond tick
    /// count, which wraps every 49.7 days); returns whether a key-down is an auto-repeat.
    /// </summary>
    public bool Note(int key, bool down, uint time)
    {
        lock (_down)
        {
            if (!down)
            {
                _down.Remove(key);
                return false;
            }
            var repeat = _down.TryGetValue(key, out var last) && unchecked(time - last) < TimeoutMs;
            _down[key] = time;
            return repeat;
        }
    }

    /// <summary>Whether <paramref name="key"/> is down at <paramref name="now"/> (the same tick count).</summary>
    public bool IsHeld(int key, uint now)
    {
        lock (_down) return _down.TryGetValue(key, out var last) && unchecked(now - last) < TimeoutMs;
    }
}
