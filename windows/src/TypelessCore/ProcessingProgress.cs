namespace TypelessCore;

/// <summary>
/// How far a dictation has come once the key is released, for the bar that fills the HUD capsule.
///
/// Real events set where the bar stands: speech-to-text chunks coming back, the whole transcript, the clean-up's
/// first token and how much of it has streamed, and the result being delivered. Between two events the bar creeps
/// part of the way towards the next one, so a slow request still looks alive, but it never gets there on its own
/// and it only ever moves right.
/// </summary>
public sealed class ProcessingProgress
{
    public abstract record Milestone
    {
        /// <summary><paramref name="Done"/> of the <paramref name="Total"/> speech-to-text chunks are back.</summary>
        public sealed record Transcribing(int Done, int Total) : Milestone;
        /// <summary>The whole transcript is in.</summary>
        public sealed record Transcribed : Milestone;
        /// <summary>The clean-up is streaming: <paramref name="Received"/> characters of an answer about <paramref name="Expected"/> long.</summary>
        public sealed record Polishing(int Received, int Expected) : Milestone;
        /// <summary>The text is ready to paste: the bar fills.</summary>
        public sealed record Delivered : Milestone;
    }

    /// <summary>Where speech-to-text ends and the clean-up starts, as a share of the bar.</summary>
    internal const double TranscribedAt = 0.45;
    /// <summary>The clean-up's first token, and its last (the rest is left for the paste).</summary>
    internal const double PolishStartsAt = 0.6;
    internal const double PolishEndsAt = 0.95;
    /// <summary>The bar starts a little filled, so releasing the key visibly starts something.</summary>
    internal const double StartsAt = 0.05;
    /// <summary>Share of the gap to the next milestone the creep covers at most, and how fast (seconds to cover half of it).</summary>
    private const double CreepShare = 0.7;
    private const double CreepHalfLife = 1.5;
    /// <summary>Seconds for the bar to glide most of the way to a new position (instead of jumping).</summary>
    private const double Glide = 0.12;

    /// <summary>Whether a clean-up follows the transcript.</summary>
    public bool Polishes { get; }
    /// <summary>Where the last milestone put the bar.</summary>
    public double Reached { get; private set; }
    /// <summary>Where the next milestone will put it; the creep stays short of it.</summary>
    public double Next { get; private set; }
    /// <summary>What the bar shows now, 0…1.</summary>
    public double Shown { get; private set; }
    private double _sinceMilestone;

    public ProcessingProgress(bool polishes)
    {
        Polishes = polishes;
        Reached = StartsAt;
        Next = TranscriptEnd(polishes);
    }

    /// <summary>Moves the bar to a milestone.</summary>
    public void Reach(Milestone milestone)
    {
        var (value, following) = Position(milestone);
        // A milestone behind the bar (a chunk sent again, a retry round) changes nothing.
        if (!(value > Reached || following > Next)) return;
        // Carries on from where the creep had got to, if that's further.
        Reached = Math.Max(Reached, Math.Max(value, Goal));
        Next = Math.Max(Next, Math.Max(following, Reached));
        _sinceMilestone = 0;
    }

    /// <summary>Advances the animation by <paramref name="seconds"/> and returns where the bar is now.</summary>
    public double Tick(double seconds)
    {
        if (!(seconds > 0) || !double.IsFinite(seconds)) return Shown;
        _sinceMilestone += seconds;
        var step = 1 - Math.Exp(-seconds / Glide);
        Shown = Math.Min(1, Math.Max(Shown, Shown + (Goal - Shown) * step));
        return Shown;
    }

    /// <summary>Without a clean-up, speech-to-text takes most of the bar.</summary>
    private static double TranscriptEnd(bool polishes) => polishes ? TranscribedAt : 0.9;

    /// <summary>Where the bar is heading: the last milestone plus the creep towards the next.</summary>
    private double Goal
    {
        get
        {
            var creep = CreepShare * (1 - Math.Pow(0.5, _sinceMilestone / CreepHalfLife));
            return Math.Min(1, Reached + (Next - Reached) * creep);
        }
    }

    /// <summary>The bar position for a milestone, and for the milestone after it.</summary>
    private (double Value, double Following) Position(Milestone milestone)
    {
        var transcribed = TranscriptEnd(Polishes);
        switch (milestone)
        {
            case Milestone.Transcribing(var done, var total):
                total = Math.Max(1, total);
                done = Math.Clamp(done, 0, total);
                var band = transcribed - StartsAt;
                return (StartsAt + band * done / total, StartsAt + band * Math.Min(done + 1, total) / total);
            case Milestone.Transcribed:
                return (transcribed, Polishes ? PolishStartsAt : 1);
            case Milestone.Polishing(var received, var expected):
                var share = expected > 0 ? Math.Min(1, (double)Math.Max(0, received) / expected) : 0;
                return (PolishStartsAt + (PolishEndsAt - PolishStartsAt) * share, PolishEndsAt);
            default:
                return (1, 1);
        }
    }
}
