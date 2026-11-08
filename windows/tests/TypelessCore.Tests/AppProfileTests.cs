using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>Which app profile a dictation picks up, what it changes, and how profiles are saved.</summary>
public class AppProfileTests
{
    /// <summary>testdata/app-profile-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "app-profile-cases.json")))!.AsObject();

    [Fact]
    public void MatchingFollowsSharedCases()
    {
        foreach (var node in Cases()["matching"]!.AsArray())
        {
            var profiles = node!["apps"]!.AsArray().Select((app, i) => new AppProfile { Id = i.ToString(), App = (string)app!, Name = (string)app! }).ToList();
            var app = (string?)node["app"];
            var expected = (int?)node["expected"];
            Assert.True(AppProfile.Matching(app, profiles)?.Id == expected?.ToString(), node.ToJsonString());
        }
    }

    /// <summary>The global settings at <paramref name="strength"/>, as the app makes them.</summary>
    private static DictationSettings GlobalSettings(string strength, bool pressEnter = false, string language = "", string extraInstructions = "") =>
        new DictationSettings(true, pressEnter, language, extraInstructions).WithStrength(PolishStrengths.From(strength)!.Value);

    [Fact]
    public void ResolutionFollowsSharedCases()
    {
        foreach (var node in Cases()["resolution"]!.AsArray())
        {
            var g = node!["global"]!;
            var global = GlobalSettings((string)g["strength"]!, (bool)g["pressEnter"]!, (string)g["language"]!, (string)g["extraInstructions"]!);
            var profile = AppProfile.FromJson(node["profile"]);
            var resolved = global.Applying(profile);
            var e = node["expected"]!;
            var name = (string)node["name"]!;
            Assert.True(resolved.EffectiveStrength.Id() == (string)e["strength"]!, name);
            Assert.True(resolved.PolishEnabled == ((string)e["strength"]! != "off"), name);
            Assert.True(resolved.PressEnter == (bool)e["pressEnter"]!, name);
            Assert.True(resolved.Language == (string)e["language"]!, name);
            Assert.True(resolved.ExtraInstructions == (string)e["extraInstructions"]!, name);
            Assert.True(resolved.ProfileId == (string?)e["profileID"], name);
            Assert.True(resolved.ProfileName == (string?)e["profileName"], name);
        }
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        List<AppProfile> profiles =
        [
            new() { Id = "A", App = "slack.exe", Name = "Slack", Instructions = "Short messages\nNo lists", Strength = PolishStrength.Off, PressEnter = true, Language = "", Layout = TextLayout.Natural },
            new() { Id = "B", App = "outlook.exe", Name = "Outlook" },
            new() { Id = "C", App = "olk.exe", Name = "Outlook", Strength = PolishStrength.Light },
        ];
        // Through text, as the settings file stores it.
        var json = AppProfile.ListToJson(profiles);
        var loaded = AppProfile.ListFromJson(JsonNode.Parse(json.ToJsonString()));
        Assert.Equal(profiles, loaded);
        // Unset overrides aren't written, so "follow the global setting" survives as such (and "" stays auto-detect).
        Assert.Equal("", (string?)json[0]!["language"]);
        Assert.False((bool?)json[0]!["polish"]);
        Assert.Equal("off", (string?)json[0]!["strength"]);
        Assert.Equal("natural", (string?)json[0]!["layout"]);
        Assert.Null(json[1]!["layout"]);
        Assert.Null(json[1]!["polish"]);
        Assert.Null(json[1]!["strength"]);
        // The switch older versions read goes with the strength: they clean up, at the only strength they know.
        Assert.True((bool?)json[2]!["polish"]);
        Assert.Equal("light", (string?)json[2]!["strength"]);
        Assert.Null(json[1]!["pressEnter"]);
        Assert.Null(json[1]!["language"]);
    }

    [Fact]
    public void StoredStrengthFollowsSharedCases()
    {
        foreach (var node in Cases()["stored"]!.AsArray())
        {
            var actual = PolishStrengths.Stored((bool?)node!["enabled"], (string?)node["level"]);
            Assert.True(actual?.Id() == (string?)node["expected"], (string)node["name"]!);
        }
    }

    /// <summary>Which strength a dictation gets from the global setting, the recommended settings and the app's profile.</summary>
    [Fact]
    public void StrengthsFollowSharedCases()
    {
        foreach (var node in Cases()["strengths"]!.AsArray())
        {
            var app = (string?)node!["app"];
            var profile = AppProfile.FromJson(node["profile"]);
            var recommendation = (bool)node["recommendations"]! ? RecommendedApps.CategoryFor(app) : null;
            var resolved = GlobalSettings((string)node["global"]!).Applying(profile, app, recommendation);
            var name = (string)node["name"]!;
            Assert.True(resolved.EffectiveStrength.Id() == (string)node["expected"]!, name);
            Assert.True(resolved.Recommendation?.Id() == (string?)node["recommendation"], name);
            // A recommendation never changes Enter or the language.
            Assert.True(resolved.PressEnter == (profile?.PressEnter ?? false) && resolved.Language == "", name);
        }
    }

    /// <summary>The same table as the macOS app's: every identifier in the fixture, and no other.</summary>
    [Fact]
    public void RecommendedAppsFollowSharedCases()
    {
        var table = Cases()["recommendedApps"]!.AsObject();
        foreach (var (app, kind) in table) Assert.True(RecommendedApps.CategoryFor(app)?.Id() == (string?)kind, app);
        Assert.Equal(table.Count, RecommendedApps.ByKey.Count);
        foreach (var app in Cases()["notRecommended"]!.AsArray()) Assert.Null(RecommendedApps.CategoryFor((string)app!));
        Assert.Null(RecommendedApps.CategoryFor(null));
        Assert.Equal(RecommendedApps.All.Length, RecommendedApps.All.Select(a => a.Name).Distinct().Count());
        Assert.Equal(PolishStrength.Light, AppCategory.Chat.Strength());
        Assert.Equal(PolishStrength.Light, AppCategory.Code.Strength());
        Assert.Equal(PolishStrength.Polished, AppCategory.Email.Strength());
    }

    [Fact]
    public void ReadsWhatOtherVersionsWrote()
    {
        // A minimal profile, one with a field this version doesn't know, and something that isn't a profile at all.
        var profiles = AppProfile.ListFromJson(JsonNode.Parse("""
            [{"id":"X","app":"com.apple.Safari"},{"id":"Y","app":"slack.exe","name":"Slack","pressEnter":true,"url":"x.com"},42]
            """));
        Assert.Equal(2, profiles.Count);
        Assert.Equal(new AppProfile { Id = "X", App = "com.apple.Safari", Name = "com.apple.Safari" }, profiles[0]);
        Assert.True(profiles[0].IsEmpty);
        Assert.True(profiles[1].PressEnter == true && profiles[1].Name == "Slack" && !profiles[1].IsEmpty);
        Assert.Empty(AppProfile.ListFromJson(JsonNode.Parse("""{"app":"code.exe"}""")));
        Assert.Empty(AppProfile.ListFromJson(null));
    }

    [Fact]
    public void ListsFollowSharedCases()
    {
        foreach (var node in Cases()["lists"]!.AsArray())
        {
            var name = (string)node!["name"]!;
            // Through text, as the settings file stores it.
            var profiles = AppProfile.WithUniqueIds(AppProfile.ListFromJson(JsonNode.Parse(node["saved"]!.ToJsonString())));
            Assert.True(profiles.Select(p => p.App).SequenceEqual(node["apps"]!.AsArray().Select(a => (string)a!)), name);
            var kept = node["keptIDs"]!.AsArray();
            for (var i = 0; i < profiles.Count; i++)
            {
                if ((string?)kept[i] is { } id) Assert.True(profiles[i].Id == id, name);
                else Assert.False(string.IsNullOrEmpty(profiles[i].Id), name);
            }
            Assert.True(profiles.Select(p => p.Id).Distinct().Count() == profiles.Count, $"{name}: IDs aren't unique");
        }
    }

    [Fact]
    public void RepairedIdsStayPutOnceSaved()
    {
        var saved = JsonNode.Parse("""[{"app":"slack.exe","name":"Slack"},{"id":"A","app":"code.exe"},{"id":"A","app":"notes.exe"}]""");
        // Reading what was saved never makes up an ID by itself, however often it's read; the repair happens once, on load.
        Assert.Equal("", AppProfile.ListFromJson(saved)[0].Id);
        Assert.Equal("", AppProfile.ListFromJson(saved)[0].Id);
        var loaded = AppProfile.WithUniqueIds(AppProfile.ListFromJson(saved));
        // Saved, then read again (as every access to the settings does): the same IDs, and nothing left to repair.
        var stored = JsonNode.Parse(AppProfile.ListToJson(loaded).ToJsonString());
        Assert.Equal(loaded, AppProfile.ListFromJson(stored));
        Assert.Equal(loaded, AppProfile.ListFromJson(stored));
        Assert.Equal(loaded, AppProfile.WithUniqueIds(AppProfile.ListFromJson(stored)));
        // So an edit and a Remove by ID find the profile they were made for.
        var edited = AppProfile.ListFromJson(stored);
        var slack = edited.FindIndex(p => p.Id == loaded[0].Id);
        edited[slack] = edited[slack] with { PressEnter = true };
        Assert.Equal(new[] { "slack.exe" }, edited.Where(p => p.PressEnter == true).Select(p => p.App));
        Assert.Equal(new[] { "slack.exe", "code.exe" }, edited.Where(p => p.Id != loaded[2].Id).Select(p => p.App));
    }

    [Fact]
    public void DeliveryFollowsSharedCases()
    {
        var global = new DictationSettings(true, false, "", "");
        foreach (var node in Cases()["delivery"]!.AsArray())
        {
            var profiles = node!["profiles"]!.AsArray()
                .Select(p => new AppProfile { Id = (string)p![0]!, App = (string)p[1]!, Name = (string)p[1]! }).ToList();
            var settings = global.Applying(AppProfile.Matching((string?)node["started"], profiles));
            Assert.True(settings.Suit((string?)node["delivered"], profiles) == (bool)node["expected"]!, (string)node["name"]!);
        }
    }

    [Fact]
    public void TranscriptReuseFollowsSharedCases()
    {
        foreach (var node in Cases()["reuse"]!.AsArray())
        {
            var settings = new DictationSettings(true, false, (string)node!["language"]!, "");
            var layout = (bool)node["sameLayout"]! ? Chunker.Layout : Chunker.Layout + 1;
            Assert.True(settings.ReusesTranscripts(layout, (string?)node["saved"]) == (bool)node["expected"]!, (string)node["name"]!);
        }
        // A record saved before chunk layouts were kept is never reused.
        Assert.False(new DictationSettings(true, false, "", "").ReusesTranscripts(null, ""));
    }

    [Fact]
    public void ProfileInstructionsReachThePromptAfterTheGlobalOnes()
    {
        var global = new DictationSettings(true, false, "", "Never use bullet lists");
        var resolved = global.Applying(new AppProfile { App = "outlook.exe", Name = "Outlook", Instructions = "Use a formal tone" });
        var prompt = Prompts.PolishSystemPrompt([], resolved.ExtraInstructions);
        Assert.StartsWith(Prompts.BasePolishPrompt, prompt);
        Assert.EndsWith("## Additional preferences from the speaker\nNever use bullet lists\nUse a formal tone", prompt);
        // The app itself is never named to the model.
        Assert.DoesNotContain("Outlook", prompt);
        Assert.DoesNotContain("outlook.exe", prompt);
    }
}
