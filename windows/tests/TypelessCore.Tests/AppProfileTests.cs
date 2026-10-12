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

    [Fact]
    public void ResolutionFollowsSharedCases()
    {
        foreach (var node in Cases()["resolution"]!.AsArray())
        {
            var g = node!["global"]!;
            var global = new DictationSettings((bool)g["polishEnabled"]!, (bool)g["pressEnter"]!, (string)g["language"]!, (string)g["extraInstructions"]!);
            var profile = AppProfile.FromJson(node["profile"]);
            var resolved = global.Applying(profile);
            var e = node["expected"]!;
            var name = (string)node["name"]!;
            Assert.True(resolved.PolishEnabled == (bool)e["polishEnabled"]!, name);
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
            new() { Id = "A", App = "slack.exe", Name = "Slack", Instructions = "Short messages\nNo lists", Polish = false, PressEnter = true, Language = "" },
            new() { Id = "B", App = "outlook.exe", Name = "Outlook" },
        ];
        // Through text, as the settings file stores it.
        var json = AppProfile.ListToJson(profiles);
        var loaded = AppProfile.ListFromJson(JsonNode.Parse(json.ToJsonString()));
        Assert.Equal(profiles, loaded);
        // Unset overrides aren't written, so "follow the global setting" survives as such (and "" stays auto-detect).
        Assert.Equal("", (string?)json[0]!["language"]);
        Assert.False((bool?)json[0]!["polish"]);
        Assert.Null(json[1]!["polish"]);
        Assert.Null(json[1]!["pressEnter"]);
        Assert.Null(json[1]!["language"]);
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
        // One saved without an ID gets one, so it can still be found again by a retry.
        Assert.False(string.IsNullOrEmpty(AppProfile.FromJson(JsonNode.Parse("""{"app":"code.exe"}"""))!.Id));
        Assert.Empty(AppProfile.ListFromJson(JsonNode.Parse("""{"app":"code.exe"}""")));
        Assert.Empty(AppProfile.ListFromJson(null));
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
