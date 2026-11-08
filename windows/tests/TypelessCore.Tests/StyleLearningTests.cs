using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>
/// Learning the speaker's writing style: which texts it learns from, the sample sent, the guard on the answer, how the
/// note joins the clean-up's preferences, and when it runs on its own.
/// </summary>
public class StyleLearningTests
{
    /// <summary>testdata/style-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "style-cases.json")))!.AsObject();

    /// <summary>Kept as the file holds them, unusable ones included: the sample has to skip those itself.</summary>
    private static List<StyleLearning.Edit> Edits(JsonNode? node) =>
        (node as JsonArray ?? []).Select(e => new StyleLearning.Edit((string)e!["before"]!, (string)e["after"]!)).ToList();

    /// <summary>The macOS app sends the same prompt: the fixture holds its hash.</summary>
    [Fact]
    public void PromptIsTheSameOnBothPlatforms()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StyleLearning.SystemPrompt))).ToLowerInvariant();
        Assert.Equal((string)Cases()["stylePromptSHA256"]!, hash);
    }

    [Fact]
    public void GuardFollowsSharedCases()
    {
        foreach (var node in Cases()["guard"]!.AsArray())
        {
            var id = (string)node!["id"]!;
            var verdict = StyleLearning.Check((string)node["answer"]!);
            Assert.True(verdict.Note == (string?)node["note"], id);
            Assert.True(verdict.Rejected?.RawValue() == (string?)node["reason"], id);
        }
    }

    [Fact]
    public void EligibilityFollowsSharedCases()
    {
        foreach (var node in Cases()["eligibility"]!.AsArray())
        {
            var eligible = StyleLearning.IsEligible((string)node!["status"]!, (string?)node["mode"], (bool)node["isFile"]!, (string)node["text"]!);
            Assert.True(eligible == (bool)node["expect"]!, (string)node["id"]!);
        }
    }

    [Fact]
    public void EditsFollowSharedCases()
    {
        foreach (var node in Cases()["edits"]!.AsArray())
        {
            var edit = StyleLearning.Edit.Make((string)node!["before"]!, (string)node["after"]!);
            var expected = node["expect"];
            Assert.True(edit?.Before == (string?)expected?["before"], (string)node["id"]!);
            Assert.True(edit?.After == (string?)expected?["after"], (string)node["id"]!);
        }
    }

    [Fact]
    public void SamplesFollowSharedCases()
    {
        foreach (var node in Cases()["samples"]!.AsArray())
        {
            var texts = node!["texts"]!.AsArray().Select(t => (string)t!).ToList();
            var sample = StyleLearning.Sample(texts, Edits(node["edits"]));
            Assert.True(sample == (string?)node["expect"], (string)node["id"]!);
        }
    }

    [Fact]
    public void CompositionFollowsSharedCases()
    {
        foreach (var node in Cases()["composition"]!.AsArray())
        {
            var composed = StyleLearning.Compose((string)node!["global"]!, (string)node["note"]!);
            Assert.True(composed == (string)node["expect"]!, (string)node["id"]!);
        }
    }

    [Fact]
    public void AutoLearningFollowsSharedCases()
    {
        foreach (var node in Cases()["due"]!.AsArray())
        {
            var due = StyleLearning.IsDue((int)node!["newDictations"]!, (string)node["note"]!, (string)node["learnedNote"]!);
            Assert.True(due == (bool)node["expect"]!, (string)node["id"]!);
        }
    }

    /// <summary>The note sits between the global preferences and an app profile's, which has the last word.</summary>
    [Fact]
    public void AnAppProfileStillComesLast()
    {
        var global = new DictationSettings(true, false, "", StyleLearning.Compose("Never use bullet lists", "- Casual tone."));
        var profile = new AppProfile { Id = "A", App = "outlook.exe", Name = "Outlook", Instructions = "Formal tone" };
        Assert.Equal("Never use bullet lists\n" + StyleLearning.PreferencesLead + "\n- Casual tone.\nFormal tone",
                     global.Applying(profile).ExtraInstructions);
    }

    /// <summary>The tuned prompt is untouched: the note only goes into the speaker's own section after it.</summary>
    [Fact]
    public void TheNoteGoesIntoTheSpeakersOwnSection()
    {
        var prompt = Prompts.PolishSystemPrompt([], StyleLearning.Compose("", "- Casual tone."));
        Assert.StartsWith(Prompts.BasePolishPrompt, prompt);
        Assert.EndsWith("## Additional preferences from the speaker\n" + StyleLearning.PreferencesLead + "\n- Casual tone.", prompt);
    }

    [Fact]
    public void NewestCorrectionsAreKept()
    {
        var edits = new List<StyleLearning.Edit>();
        for (var i = 0; i < StyleLearning.MaxEdits + 5; i++) edits = StyleLearning.Adding(StyleLearning.Edit.Make($"before {i}", $"after {i}")!, edits);
        Assert.Equal(StyleLearning.MaxEdits, edits.Count);
        Assert.Equal($"after {StyleLearning.MaxEdits + 4}", edits[0].After);
        Assert.Equal("after 5", edits[^1].After);
        // Saved and read back; a damaged file is no corrections at all.
        Assert.Equal(edits, StyleLearning.Edit.ListFromJson(StyleLearning.Edit.ListToJson(edits)));
        Assert.Empty(StyleLearning.Edit.ListFromJson("not json"));
    }

    /// <summary>One request, not streamed, with a clean-up's tuning but the note's messages and room.</summary>
    [Fact]
    public void RequestBody()
    {
        var client = new ApiClient(new ProviderEndpoint(ProviderId.OpenAI, new Uri("https://api.openai.com/v1"), "k"));
        var body = client.StyleBody("<texts>\n<text>Hi</text>\n</texts>", new PolishOptions("gpt-5-mini"), includeOptional: true);
        var messages = body["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", (string)messages[0]!["role"]!);
        Assert.Equal(StyleLearning.SystemPrompt, (string)messages[0]!["content"]!);
        Assert.Equal("user", (string)messages[1]!["role"]!);
        Assert.Equal("<texts>\n<text>Hi</text>\n</texts>", (string)messages[1]!["content"]!);
        Assert.False((bool)body["stream"]!);
        Assert.False(body.ContainsKey("stream_options"));
        Assert.Equal("gpt-5-mini", (string)body["model"]!);

        var anthropic = new ApiClient(new ProviderEndpoint(ProviderId.Anthropic, new Uri("https://api.anthropic.com/v1"), "k"));
        var bare = anthropic.StyleBody("x", new PolishOptions("claude"), includeOptional: false);
        Assert.Equal(StyleLearning.MaxTokens, (int)bare["max_tokens"]!);
    }
}
