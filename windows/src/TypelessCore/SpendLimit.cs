namespace TypelessCore;

/// <summary>
/// The monthly spending limit (set on the Models page, off by default): a reminder when this calendar month's spend, as
/// the Home page counts it, first reaches <see cref="WarningFraction"/> of the limit, and another when it reaches the
/// limit. Only a reminder: dictation never stops for it, and nothing is dropped. Each reminder comes at most once a
/// month; what has been shown is saved (<see cref="State"/>), a new month starts over, and a limit raised above what
/// has been spent arms its reminders again.
/// </summary>
public static class SpendLimit
{
    /// <summary>The share of the limit at which the first reminder comes.</summary>
    public const double WarningFraction = 0.8;

    /// <summary>How far the month's spend has gone towards the limit, in order.</summary>
    public enum Level
    {
        None = 0,
        /// <summary><see cref="WarningFraction"/> of the limit or more.</summary>
        Warning = 1,
        /// <summary>The limit or more.</summary>
        Reached = 2,
    }

    /// <summary>The name a level is saved under.</summary>
    public static string Name(this Level level) => level switch
    {
        Level.Warning => "warning",
        Level.Reached => "reached",
        _ => "none",
    };

    public static Level? ParseLevel(string name) => name switch
    {
        "none" => Level.None,
        "warning" => Level.Warning,
        "reached" => Level.Reached,
        _ => null,
    };

    /// <summary>Where <paramref name="spent"/> stands against <paramref name="limit"/>. None for a limit that isn't a positive number (no limit).</summary>
    public static Level LevelOf(double spent, double limit)
    {
        if (!double.IsFinite(limit) || limit <= 0 || double.IsNaN(spent)) return Level.None;
        if (spent >= limit) return Level.Reached;
        if (spent >= limit * WarningFraction) return Level.Warning;
        return Level.None;
    }

    /// <summary>The share of the limit spent, from 0 to 1, for a progress bar; 0 without a limit.</summary>
    public static double Progress(double spent, double limit)
    {
        if (!double.IsFinite(limit) || limit <= 0 || !double.IsFinite(spent) || spent <= 0) return 0;
        return Math.Min(1, spent / limit);
    }

    /// <summary>
    /// The share of the limit spent in whole percent, rounded down (so 79.9 % doesn't read 80 % before the reminder
    /// says so) and held at a million; 0 without a limit.
    /// </summary>
    public static int Percent(double spent, double limit)
    {
        if (!double.IsFinite(limit) || limit <= 0 || !double.IsFinite(spent) || spent <= 0) return 0;
        return (int)Math.Min(1_000_000, Math.Floor(spent / limit * 100));
    }

    /// <summary>The month a day falls in, as <see cref="State"/> keeps it: "2026-11".</summary>
    public static string MonthKey(DateOnly day) => UsageLedger.Key(day)[..7];

    /// <summary>Which reminder this month has had: saved with the settings, so each comes at most once a month.</summary>
    /// <param name="Month">"2026-11"; "" before any.</param>
    /// <param name="Reminded">The furthest reminder shown that month.</param>
    public readonly record struct State(string Month, Level Reminded)
    {
        public static State Empty => new("", Level.None);

        /// <summary>"2026-11:warning"; "" for none.</summary>
        public string Stored => Month.Length == 0 ? "" : Month + ":" + Reminded.Name();

        /// <summary>
        /// The state saved as <see cref="Stored"/>; anything else (nothing saved yet, or a value this version can't read)
        /// is none, which at worst shows a reminder again.
        /// </summary>
        public static State FromStored(string stored)
        {
            var parts = stored.Split(':');
            if (parts.Length != 2 || !IsMonthKey(parts[0]) || ParseLevel(parts[1]) is not { } level) return Empty;
            return new State(parts[0], level);
        }
    }

    /// <summary>"2026-11": four digits, a dash and a month from 01 to 12.</summary>
    internal static bool IsMonthKey(string text)
    {
        if (text.Length != 7 || text[4] != '-') return false;
        for (var i = 0; i < 7; i++)
        {
            if (i != 4 && text[i] is < '0' or > '9') return false;
        }
        var month = (text[5] - '0') * 10 + (text[6] - '0');
        return month is >= 1 and <= 12;
    }

    /// <summary>What <see cref="Check"/> found: the reminder to show now, if any, and the state to save.</summary>
    /// <param name="Reminder">Warning or Reached; null when no reminder is due.</param>
    public readonly record struct Outcome(Level? Reminder, State State);

    /// <summary>
    /// After a request's cost was added to the month: which reminder is due, if any, and the state to save in place of
    /// <paramref name="state"/>. <paramref name="spentBefore"/> is the month's spend before that cost and
    /// <paramref name="spent"/> after it, both in USD; <paramref name="month"/> is <see cref="MonthKey"/> of today.
    /// <para>
    /// A reminder from another month doesn't count. One the spend was below before this cost (with the limit as it is
    /// now, so after the limit was raised above it) is armed again. When a cost takes the spend past both thresholds at
    /// once, only the limit's reminder is shown.
    /// </para>
    /// </summary>
    public static Outcome Check(double spentBefore, double spent, double limit, string month, State state)
    {
        var reminded = state.Month == month ? state.Reminded : Level.None;
        reminded = (Level)Math.Min((int)reminded, (int)LevelOf(spentBefore, limit));
        var now = LevelOf(spent, limit);
        Level? reminder = now > reminded ? now : null;
        return new Outcome(reminder, new State(month, (Level)Math.Max((int)reminded, (int)now)));
    }
}
