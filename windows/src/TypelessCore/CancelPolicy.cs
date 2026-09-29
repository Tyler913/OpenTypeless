namespace TypelessCore;

/// <summary>
/// What Esc does to a dictation. A long recording is too much to lose to a stray key press, so from
/// <see cref="KeepAfterSeconds"/> on it is kept, transcribed but not cleaned up or inserted, and shown in History as
/// cancelled for <see cref="KeptFor"/>; shorter ones are thrown away as before.
/// </summary>
public static class CancelPolicy
{
    public const double KeepAfterSeconds = 10;
    public static readonly TimeSpan KeptFor = TimeSpan.FromHours(24);

    /// <summary>Whether cancelling a recording of this length keeps it.</summary>
    public static bool Keeps(double recordedSeconds) => recordedSeconds >= KeepAfterSeconds;

    /// <summary>When a cancelled dictation made at <paramref name="date"/> is removed.</summary>
    public static DateTimeOffset Expiry(DateTimeOffset date) => date + KeptFor;

    public static bool IsExpired(DateTimeOffset recordedAt, DateTimeOffset now) => now >= Expiry(recordedAt);
}
