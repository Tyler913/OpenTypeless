namespace TypelessCore.Tests;

using M = ProcessingProgress.Milestone;

public class ProcessingProgressTests
{
    /// <summary>Runs the animation for <paramref name="seconds"/> at 60 frames a second.</summary>
    private static double Run(ProcessingProgress progress, double seconds)
    {
        for (var i = 0; i < (int)(seconds * 60); i++) progress.Tick(1.0 / 60);
        return progress.Shown;
    }

    [Fact]
    public void StartsSlightlyFilledAndCreepsWithoutReachingTheTranscript()
    {
        var progress = new ProcessingProgress(polishes: true);
        var early = Run(progress, 0.5);
        Assert.True(early >= ProcessingProgress.StartsAt * 0.9);
        var late = Run(progress, 30);
        Assert.True(late > early);
        // However long speech-to-text takes, the bar waits for it.
        Assert.True(late < ProcessingProgress.TranscribedAt);
    }

    [Fact]
    public void FollowsTheMilestonesInOrder()
    {
        var progress = new ProcessingProgress(polishes: true);
        progress.Reach(new M.Transcribing(1, 2));
        var half = Run(progress, 1);
        Assert.InRange(half, 0.2, ProcessingProgress.TranscribedAt);
        progress.Reach(new M.Transcribed());
        Assert.True(Run(progress, 1) >= ProcessingProgress.TranscribedAt * 0.98);
        progress.Reach(new M.Polishing(5, 10));
        var streaming = Run(progress, 1);
        Assert.InRange(streaming, ProcessingProgress.PolishStartsAt, ProcessingProgress.PolishEndsAt);
        progress.Reach(new M.Delivered());
        Assert.True(Run(progress, 1) > 0.99);
    }

    [Fact]
    public void NeverMovesBack()
    {
        var progress = new ProcessingProgress(polishes: true);
        progress.Reach(new M.Transcribing(3, 4));
        var last = Run(progress, 2);
        // A failed chunk sent again, or a second clean-up attempt.
        progress.Reach(new M.Transcribing(2, 4));
        progress.Reach(new M.Polishing(0, 10));
        for (var i = 0; i < 120; i++)
        {
            var now = progress.Tick(1.0 / 60);
            Assert.True(now >= last);
            last = now;
        }
    }

    [Fact]
    public void WithoutCleanUpTheTranscriptFillsMostOfTheBar()
    {
        var progress = new ProcessingProgress(polishes: false);
        progress.Reach(new M.Transcribed());
        Assert.InRange(Run(progress, 1), 0.85, 0.999);
    }

    [Fact]
    public void OddInputsStayInRange()
    {
        var progress = new ProcessingProgress(polishes: true);
        progress.Reach(new M.Transcribing(7, 0));
        progress.Reach(new M.Polishing(500, 3));
        Assert.True(Run(progress, 5) <= ProcessingProgress.PolishEndsAt);
        progress.Tick(double.NaN);
        progress.Tick(-1);
        progress.Reach(new M.Delivered());
        Assert.True(Run(progress, 5) <= 1);
    }
}
