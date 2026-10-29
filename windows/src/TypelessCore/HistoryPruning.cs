namespace TypelessCore;

/// <summary>
/// What History deletes. Every dictation is saved with its recording (about 1.9 MB a minute of 16 kHz WAV) so nothing
/// is lost to a failure, which comes to tens of MB a day for someone who dictates all day. The retention setting goes
/// by age; on top of it the recordings together are held under <see cref="AudioBudget"/>, so "1 year" or "Forever"
/// can't fill the disk.
/// <list type="bullet">
/// <item>A finished dictation older than the retention setting loses its recording; its text stays among the newest
/// <see cref="MinimumTextRecords"/>, and older ones are removed entirely.</item>
/// <item>A failed one keeps its recording so it can be re-sent, but not forever: it expires the same way once it is
/// older than the retention setting and at least <see cref="FailedMinimumAge"/>.</item>
/// <item>A cancelled one goes a day after it was made, whatever the setting (see <see cref="CancelPolicy"/>).</item>
/// <item>While the recordings still come to more than <see cref="AudioBudget"/>, the oldest finished and cancelled
/// dictations lose theirs (the text stays), but never the newest <see cref="NewestKeptWithAudio"/> nor one still being
/// recorded or processed.</item>
/// </list>
/// </summary>
public static class HistoryPruning
{
    /// <summary>About nine hours of recordings.</summary>
    public const long AudioBudget = 1_000_000_000;
    public const int NewestKeptWithAudio = 10;
    public const int MinimumTextRecords = 200;
    public static readonly TimeSpan FailedMinimumAge = TimeSpan.FromDays(30);

    public enum Kind
    {
        /// <summary>Still being recorded or processed (a retry included).</summary>
        InProgress,
        /// <summary>Done, or inserted without its clean-up.</summary>
        Finished,
        Failed,
        Cancelled,
    }

    /// <param name="AudioBytes">The size of its recording; 0 when there is none.</param>
    public readonly record struct Entry(Kind Kind, DateTimeOffset Date, long AudioBytes);

    public enum Action
    {
        Keep,
        /// <summary>Delete the recording, keep the text.</summary>
        RemoveAudio,
        /// <summary>Delete the whole dictation.</summary>
        Remove,
    }

    /// <summary>How long a failed dictation is kept, given how long the retention setting keeps finished ones (null = forever).</summary>
    public static TimeSpan? FailedMaxAge(TimeSpan? maxAge) => maxAge is { } age ? (age > FailedMinimumAge ? age : FailedMinimumAge) : null;

    /// <summary>What to do with each of <paramref name="entries"/> (newest first, as History lists them), in the same order.</summary>
    public static Action[] Plan(IReadOnlyList<Entry> entries, TimeSpan? maxAge, DateTimeOffset now, long audioBudget = AudioBudget)
    {
        var actions = new Action[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            TimeSpan? limit;
            switch (entry.Kind)
            {
                case Kind.InProgress:
                    continue;
                case Kind.Cancelled:
                    if (CancelPolicy.IsExpired(entry.Date, now)) actions[index] = Action.Remove;
                    continue;
                case Kind.Finished:
                    limit = maxAge;
                    break;
                default:
                    limit = FailedMaxAge(maxAge);
                    break;
            }
            if (limit is not { } age || now - entry.Date < age) continue;
            if (index >= MinimumTextRecords) actions[index] = Action.Remove;
            else if (entry.AudioBytes > 0) actions[index] = Action.RemoveAudio;
        }

        // Then the size cap, oldest first, over the recordings still kept.
        var total = 0L;
        for (var index = 0; index < entries.Count; index++)
        {
            if (actions[index] == Action.Keep) total += entries[index].AudioBytes;
        }
        for (var index = entries.Count - 1; total > audioBudget && index >= NewestKeptWithAudio; index--)
        {
            var entry = entries[index];
            if (actions[index] == Action.Keep && entry.AudioBytes > 0 && entry.Kind is Kind.Finished or Kind.Cancelled)
            {
                actions[index] = Action.RemoveAudio;
                total -= entry.AudioBytes;
            }
        }
        return actions;
    }
}
