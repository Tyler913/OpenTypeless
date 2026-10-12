namespace TypelessCore.Tests;

/// <summary>What History deletes: retention by age, and the cap on the recordings' total size.</summary>
public class HistoryPruningTests
{

    [Fact]
    public void DefaultGigabyteBudgetIsInclusiveAndUsesWideTotals()
    {
        var entries = Enumerable.Range(0, 10).Select(_ => Entry(HistoryPruning.Kind.Finished, 0, 90_000_000)).ToList();
        entries.Add(Entry(HistoryPruning.Kind.Finished, 1, 100_000_000));
        Assert.All(HistoryPruning.Plan(entries, null, Now), action => Assert.Equal(HistoryPruning.Action.Keep, action));
        entries[10] = entries[10] with { AudioBytes = 100_000_001 };
        Assert.Equal([.. Repeat(HistoryPruning.Action.Keep, 10), HistoryPruning.Action.RemoveAudio], HistoryPruning.Plan(entries, null, Now));
        entries.Add(Entry(HistoryPruning.Kind.Finished, 2, 3_000_000_000));
        Assert.Equal([.. Repeat(HistoryPruning.Action.Keep, 10), .. Repeat(HistoryPruning.Action.RemoveAudio, 2)], HistoryPruning.Plan(entries, null, Now));
    }

    [Fact]
    public void FailedExpiryBoundaryAndMissingAudioStillRespectTextFloor()
    {
        var entries = Enumerable.Range(0, 201).Select(_ => Entry(HistoryPruning.Kind.Failed, 30, 0)).ToList();
        Assert.Equal([.. Repeat(HistoryPruning.Action.Keep, 200), HistoryPruning.Action.Remove], HistoryPruning.Plan(entries, TimeSpan.Zero, Now));
        entries[199] = entries[199] with { AudioBytes = 1 };
        entries[200] = entries[200] with { Date = Now - Month + TimeSpan.FromSeconds(1) };
        var actions = HistoryPruning.Plan(entries, TimeSpan.Zero, Now);
        Assert.Equal(HistoryPruning.Action.RemoveAudio, actions[199]);
        Assert.Equal(HistoryPruning.Action.Keep, actions[200]);
        entries[200] = Entry(HistoryPruning.Kind.InProgress, 365, 2_000_000_000);
        Assert.Equal(HistoryPruning.Action.Keep, HistoryPruning.Plan(entries, TimeSpan.Zero, Now)[200]);
        Assert.Empty(HistoryPruning.Plan([], TimeSpan.Zero, Now));
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 21, 13, 46, 40, TimeSpan.Zero);
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);

    private static HistoryPruning.Entry Entry(HistoryPruning.Kind kind, double daysAgo, long bytes = 1_000) =>
        new(kind, Now - TimeSpan.FromDays(daysAgo), bytes);

    private static HistoryPruning.Action[] Repeat(HistoryPruning.Action action, int count) => Enumerable.Repeat(action, count).ToArray();

    [Fact]
    public void FinishedDictationsLoseTheRecordingThenTheText()
    {
        var entries = new[] { Entry(HistoryPruning.Kind.Finished, 1), Entry(HistoryPruning.Kind.Finished, 31), Entry(HistoryPruning.Kind.Finished, 32, bytes: 0) };
        Assert.Equal([HistoryPruning.Action.Keep, HistoryPruning.Action.RemoveAudio, HistoryPruning.Action.Keep], HistoryPruning.Plan(entries, Month, Now));
        // Past the newest 200 an expired one goes entirely; "Forever" never expires anything by age.
        var many = Enumerable.Range(0, 201).Select(_ => Entry(HistoryPruning.Kind.Finished, 40)).ToList();
        var actions = HistoryPruning.Plan(many, Month, Now);
        Assert.Equal(HistoryPruning.Action.RemoveAudio, actions[199]);
        Assert.Equal(HistoryPruning.Action.Remove, actions[200]);
        Assert.All(HistoryPruning.Plan(many, null, Now), action => Assert.Equal(HistoryPruning.Action.Keep, action));
    }

    [Fact]
    public void CancelledDictationsGoAfterADayWhateverTheSetting()
    {
        var entries = new[] { Entry(HistoryPruning.Kind.Cancelled, 0.5), Entry(HistoryPruning.Kind.Cancelled, 1.1) };
        Assert.Equal([HistoryPruning.Action.Keep, HistoryPruning.Action.Remove], HistoryPruning.Plan(entries, null, Now));
    }

    [Fact]
    public void FailedDictationsExpireAfterAtLeastAMonth()
    {
        Assert.Equal(HistoryPruning.FailedMinimumAge, HistoryPruning.FailedMaxAge(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromDays(365), HistoryPruning.FailedMaxAge(TimeSpan.FromDays(365)));
        Assert.Null(HistoryPruning.FailedMaxAge(null));
        var entries = new[] { Entry(HistoryPruning.Kind.Failed, 29), Entry(HistoryPruning.Kind.Failed, 31) };
        // "Don't keep recordings" still leaves a failed one a month to be re-sent.
        Assert.Equal([HistoryPruning.Action.Keep, HistoryPruning.Action.RemoveAudio], HistoryPruning.Plan(entries, TimeSpan.Zero, Now));
        Assert.Equal([HistoryPruning.Action.Keep, HistoryPruning.Action.Keep], HistoryPruning.Plan(entries, TimeSpan.FromDays(365), Now));
        Assert.Equal([HistoryPruning.Action.Keep, HistoryPruning.Action.Keep], HistoryPruning.Plan(entries, null, Now));
        var old = Enumerable.Range(0, 201).Select(_ => Entry(HistoryPruning.Kind.Failed, 40)).ToList();
        Assert.Equal(HistoryPruning.Action.Remove, HistoryPruning.Plan(old, Month, Now)[200]);
    }

    [Fact]
    public void RecordingsOverTheBudgetGoOldestFirst()
    {
        // 15 dictations of 100 bytes against a 1,000-byte budget: the five oldest go, newest kept.
        var entries = Enumerable.Range(0, 15).Select(i => Entry(HistoryPruning.Kind.Finished, i * 0.01, bytes: 100)).ToList();
        Assert.Equal([.. Repeat(HistoryPruning.Action.Keep, 10), .. Repeat(HistoryPruning.Action.RemoveAudio, 5)],
                     HistoryPruning.Plan(entries, null, Now, audioBudget: 1_000));
        Assert.All(HistoryPruning.Plan(entries, null, Now, audioBudget: 1_500), action => Assert.Equal(HistoryPruning.Action.Keep, action));
    }

    [Fact]
    public void BudgetSparesTheNewestAndUnfinishedAndFailedDictations()
    {
        var entries = Enumerable.Range(0, 12).Select(i => Entry(HistoryPruning.Kind.Finished, i * 0.01, bytes: 500)).ToList();
        entries[0] = entries[0] with { Kind = HistoryPruning.Kind.InProgress };
        entries[10] = entries[10] with { Kind = HistoryPruning.Kind.Failed };
        entries[11] = entries[11] with { Kind = HistoryPruning.Kind.InProgress };
        // Past the newest ten nothing is finished, so nothing can go, however far over the budget.
        Assert.All(HistoryPruning.Plan(entries, null, Now, audioBudget: 100), action => Assert.Equal(HistoryPruning.Action.Keep, action));

        // A cancelled one can go too; recordings already gone don't count, nor does one that expires by age anyway.
        entries = [Entry(HistoryPruning.Kind.Finished, 0), .. Enumerable.Range(1, 9).Select(_ => Entry(HistoryPruning.Kind.Finished, 0.1, bytes: 0)),
                   Entry(HistoryPruning.Kind.Cancelled, 0.5, bytes: 600), Entry(HistoryPruning.Kind.Finished, 2, bytes: 600),
                   Entry(HistoryPruning.Kind.Finished, 40, bytes: 600)];
        HistoryPruning.Action[] Plan(long budget) => HistoryPruning.Plan(entries, Month, Now, budget);
        var newest = Repeat(HistoryPruning.Action.Keep, 10);
        Assert.Equal([.. newest, HistoryPruning.Action.Keep, HistoryPruning.Action.Keep, HistoryPruning.Action.RemoveAudio], Plan(2_200));
        Assert.Equal([.. newest, HistoryPruning.Action.Keep, HistoryPruning.Action.RemoveAudio, HistoryPruning.Action.RemoveAudio], Plan(1_700));
        Assert.Equal([.. newest, .. Repeat(HistoryPruning.Action.RemoveAudio, 3)], Plan(1_000));
        // The newest ten keep theirs even when they alone are over it.
        Assert.Equal([.. newest, .. Repeat(HistoryPruning.Action.RemoveAudio, 3)], Plan(500));
    }
}
