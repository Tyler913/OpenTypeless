using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>The monthly spending limit's reminders: when they're due, once a month each, and how what was shown is saved.</summary>
public class SpendLimitTests
{
    /// <summary>testdata/spend-limit-cases.json, shared with the macOS tests so both apps remind at the same points.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "spend-limit-cases.json")))!.AsObject();

    private static SpendLimit.Level? Level(JsonNode? node) => (string?)node is { } name ? SpendLimit.ParseLevel(name) : null;

    [Fact]
    public void LevelsMatchSharedCases()
    {
        foreach (var node in Cases()["levels"]!.AsArray())
        {
            var spent = (double)node!["spent"]!;
            var limit = (double)node["limit"]!;
            var description = node.ToJsonString();
            Assert.True(SpendLimit.LevelOf(spent, limit) == Level(node["level"]), description);
            Assert.True(Math.Abs(SpendLimit.Progress(spent, limit) - (double)node["progress"]!) < 1e-9, description);
            Assert.True(SpendLimit.Percent(spent, limit) == (int)node["percent"]!, description);
        }
    }

    [Fact]
    public void MonthKeysMatchSharedCases()
    {
        foreach (var node in Cases()["monthKeys"]!.AsArray())
        {
            var day = UsageLedger.ParseKey((string)node!["date"]!);
            Assert.NotNull(day);
            Assert.Equal((string)node["month"]!, SpendLimit.MonthKey(day.Value));
        }
    }

    [Fact]
    public void StoredStatesMatchSharedCases()
    {
        foreach (var node in Cases()["stored"]!.AsArray())
        {
            var state = SpendLimit.State.FromStored((string)node!["stored"]!);
            var description = node.ToJsonString();
            Assert.True(state.Month == (string)node["month"]!, description);
            Assert.True(state.Reminded == Level(node["reminded"]), description);
            Assert.True(state.Stored == (string)node["roundTrip"]!, description);
        }
    }

    [Fact]
    public void ChecksMatchSharedCases()
    {
        foreach (var node in Cases()["checks"]!.AsArray())
        {
            var outcome = SpendLimit.Check((double)node!["spentBefore"]!, (double)node["spent"]!, (double)node["limit"]!,
                                           (string)node["month"]!, SpendLimit.State.FromStored((string)node["state"]!));
            var name = (string)node["name"]!;
            Assert.True(outcome.Reminder == Level(node["reminder"]), $"{name}: got {outcome.Reminder}");
            Assert.True(outcome.State.Stored == (string)node["after"]!, $"{name}: got {outcome.State.Stored}");
        }
    }

    [Fact]
    public void SequencesMatchSharedCases()
    {
        foreach (var sequence in Cases()["sequences"]!.AsArray())
        {
            var name = (string)sequence!["name"]!;
            var state = SpendLimit.State.FromStored("");
            var index = 0;
            foreach (var step in sequence["steps"]!.AsArray())
            {
                index++;
                var outcome = SpendLimit.Check((double)step!["spentBefore"]!, (double)step["spent"]!, (double)step["limit"]!,
                                               (string)step["month"]!, state);
                Assert.True(outcome.Reminder == Level(step["reminder"]), $"{name}, step {index}: got {outcome.Reminder}");
                // Through the saved form, as the app keeps it between dictations.
                state = SpendLimit.State.FromStored(outcome.State.Stored);
            }
        }
    }

    [Fact]
    public void EachReminderComesOnceAMonthHoweverTheSpendGrows()
    {
        var state = SpendLimit.State.Empty;
        var shown = new List<SpendLimit.Level>();
        var spent = 0.0;
        // A hundred small dictations of a cent each against a $0.50 limit.
        for (var i = 0; i < 100; i++)
        {
            var before = spent;
            spent += 0.01;
            var outcome = SpendLimit.Check(before, spent, 0.5, "2026-11", state);
            if (outcome.Reminder is { } reminder) shown.Add(reminder);
            state = outcome.State;
        }
        Assert.Equal([SpendLimit.Level.Warning, SpendLimit.Level.Reached], shown);
        Assert.Equal(new SpendLimit.State("2026-11", SpendLimit.Level.Reached), state);
    }

    [Fact]
    public void LevelsAreOrdered()
    {
        Assert.True(SpendLimit.Level.None < SpendLimit.Level.Warning && SpendLimit.Level.Warning < SpendLimit.Level.Reached);
        foreach (var level in new[] { SpendLimit.Level.None, SpendLimit.Level.Warning, SpendLimit.Level.Reached })
        {
            Assert.Equal(level, SpendLimit.ParseLevel(level.Name()));
        }
    }
}
