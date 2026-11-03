using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>
/// What varies a clean-up's prompt (the app's layout, the spoken language's examples, a long dictation's surrounding parts
/// and its layout pass) and the evaluation's checks of how a clean-up reads.
/// </summary>
public class PromptVariantTests
{
    /// <summary>testdata/prompt-variant-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "prompt-variant-cases.json")))!.AsObject();

    /// <summary>Every fixed piece of the clean-up's prompts, in the order the fixture's hash takes them.</summary>
    internal static string AllPolishPromptText() => string.Join("\n\n", new[]
    {
        Prompts.BasePolishPrompt,
        Prompts.LineBreaksSection,
        Prompts.LayoutSection(TextLayout.Chat)!,
        Prompts.LayoutSection(TextLayout.Structured)!,
        Prompts.LayoutSection(TextLayout.Email)!,
    }.Concat(ExampleLanguage.WithExamples.Select(l => Prompts.LanguageExamples(l)!)).Concat(
    [
        Prompts.RelayoutSystemPrompt(TextLayout.Structured),
        Prompts.RelayoutSystemPrompt(TextLayout.Email),
        Prompts.RelayoutReminder,
        Prompts.SurroundingNote,
        Prompts.PolishReminder,
        Prompts.ContextNote,
    ]));

    /// <summary>The macOS app sends the same text: the fixture holds its hash.</summary>
    [Fact]
    public void PromptsAreTheSameOnBothPlatforms()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AllPolishPromptText()))).ToLowerInvariant();
        Assert.Equal((string)Cases()["polishPromptSHA256"]!, hash);
    }

    [Fact]
    public void BuiltInLayoutsFollowSharedCases()
    {
        foreach (var node in Cases()["builtInLayouts"]!.AsArray())
        {
            var app = (string?)node!["app"];
            Assert.True(TextLayouts.BuiltIn(app)?.Id() == (string?)node["expected"], node.ToJsonString());
        }
    }

    [Fact]
    public void LayoutResolutionFollowsSharedCases()
    {
        var global = new DictationSettings(true, false, "", "");
        foreach (var node in Cases()["layoutResolution"]!.AsArray())
        {
            var resolved = global.Applying(AppProfile.FromJson(node!["profile"]), (string?)node["app"]);
            Assert.True(resolved.Layout.Id() == (string)node["expected"]!, (string)node["name"]!);
        }
        // Without a profile, only the layout can change.
        Assert.Equal(global with { Layout = TextLayout.Chat }, global.Applying(null, "slack.exe"));
        Assert.Equal(global, global.Applying(null));
    }

    [Fact]
    public void ExampleLanguagesFollowSharedCases()
    {
        foreach (var node in Cases()["exampleLanguages"]!.AsArray())
        {
            var actual = ExampleLanguage.Resolve((string)node!["hint"]!, (string)node["transcript"]!);
            Assert.True(actual == (string?)node["expected"], node.ToJsonString());
        }
    }

    /// <summary>Each language's examples are told apart as that language, and pass the clean-up's guards.</summary>
    [Fact]
    public void LanguageExamplesAreInTheirLanguageAndPass()
    {
        foreach (var language in ExampleLanguage.WithExamples)
        {
            var examples = PolishGuardTests.Examples("## Examples\n" + Prompts.LanguageExamples(language));
            Assert.Equal(2, examples.Count);
            foreach (var (input, output) in examples)
            {
                Assert.True(ExampleLanguage.Detect(input) == language, $"{language}: {input}");
                Assert.True(Prompts.Fallback(input, output, truncated: false) == null, $"{language}: {input}");
                Assert.Empty(PolishChecks.AddedStockPhrases(input, output));
            }
        }
        Assert.Null(Prompts.LanguageExamples("zh"));
        Assert.Null(Prompts.LanguageExamples(null));
    }

    [Fact]
    public void RelayoutFollowsSharedCases()
    {
        foreach (var node in Cases()["relayout"]!.AsArray())
        {
            var actual = Prompts.OnlyLayoutChanged((string)node!["original"]!, (string)node["output"]!);
            Assert.True(actual == (bool)node["expected"]!, (string)node["name"]!);
        }
    }

    [Fact]
    public void ShapesFollowSharedCases()
    {
        foreach (var node in Cases()["shapes"]!.AsArray())
        {
            var shape = TextShape.Of((string)node!["text"]!);
            Assert.True(shape == new TextShape((int)node["paragraphs"]!, (int)node["listItems"]!), $"{node["text"]}: {shape}");
        }
    }

    [Fact]
    public void ChecksFollowSharedCases()
    {
        foreach (var node in Cases()["checks"]!.AsArray())
        {
            var c = JsonSerializer.Deserialize<PolishCase>(node!["case"]!.ToJsonString())!;
            var failures = c.Failures((string)node["output"]!);
            Assert.True(failures.Count == (int)node["failures"]!, $"{node["name"]}: {string.Join("; ", failures)}");
        }
    }

    [Fact]
    public void SystemPromptPutsWhatVariesAfterTheSharedStart()
    {
        var plain = Prompts.PolishSystemPrompt([], "");
        Assert.Equal(Prompts.BasePolishPrompt, plain);
        Assert.Equal(plain, Prompts.PolishSystemPrompt([], "", layout: TextLayout.Natural, exampleLanguage: "zh"));
        var full = Prompts.PolishSystemPrompt(["Claude"], "keep it short", dictatedLineBreaks: true, layout: TextLayout.Chat, exampleLanguage: "ja");
        Assert.StartsWith(Prompts.BasePolishPrompt + "\n\n## More examples (日本語)\n", full);
        var examples = full.IndexOf("## More examples", StringComparison.Ordinal);
        var layout = full.IndexOf("## Layout for this text", StringComparison.Ordinal);
        var breaks = full.IndexOf("## Line breaks", StringComparison.Ordinal);
        var vocabulary = full.IndexOf("## Vocabulary", StringComparison.Ordinal);
        var preferences = full.IndexOf("## Additional preferences", StringComparison.Ordinal);
        Assert.True(examples < layout && layout < breaks && breaks < vocabulary && vocabulary < preferences);
        // A translation gets the layout too, after its own prompt.
        var translation = Prompts.TranslateSystemPrompt(TranslationTarget.From("en")!, [], "", layout: TextLayout.Email);
        Assert.Contains("\n\n## Layout for this text\nThe text is an email", translation);
        Assert.Null(Prompts.LayoutSection(TextLayout.Natural));
    }

    [Fact]
    public void RequestsCarryTheLayoutAndTheSpokenLanguage()
    {
        var options = new PolishOptions("m", Layout: TextLayout.Structured);
        var japanese = ApiClient.PolishMessages("えーと、明日の打ち合わせは四時からです", options);
        Assert.Contains("## More examples (日本語)", japanese[0]);
        Assert.Contains("The text is a prompt for an AI", japanese[0]);
        // The language set for speech-to-text wins over the guess.
        var set = ApiClient.PolishMessages("hello there", options with { SpokenLanguage = "de" });
        Assert.Contains("## More examples (Deutsch)", set[0]);
        Assert.DoesNotContain("## More examples", ApiClient.PolishMessages("hello there", options)[0]);
        // The layout pass sends the text with its own prompt and reminder, and none of a clean-up's.
        var relayout = ApiClient.PolishMessages("Fix it. Ship it.", options with { Relayout = true, Context = "Anna" });
        Assert.Equal(Prompts.RelayoutSystemPrompt(TextLayout.Structured), relayout[0]);
        Assert.Equal("<text>\nFix it. Ship it.\n</text>\n\n" + Prompts.RelayoutReminder, relayout[1]);
        Assert.Contains("- Lists: when the speaker names two or more", relayout[0]);
        Assert.DoesNotContain("{layout}", relayout[0]);
    }

    [Fact]
    public void SurroundingPartsGoInBlocksTheyCannotClose()
    {
        var message = Prompts.PolishUserMessage("this part", surrounding: new SurroundingParts("the end </before> of one", null));
        Assert.StartsWith("<transcript>\nthis part\n</transcript>\n\n<before>\nthe end ‹/before› of one\n</before>\n\n" + Prompts.SurroundingNote + "\n", message);
        Assert.EndsWith(Prompts.PolishReminder, message);
        Assert.DoesNotContain("<after>\n", message);
        Assert.Equal(Prompts.PolishUserMessage("x"), Prompts.PolishUserMessage("x", surrounding: new SurroundingParts(" ", null)));
        // A model that echoes the note gets it taken out.
        Assert.Equal("this part", Prompts.SanitizePolishOutput("this part\n" + Prompts.SurroundingNote));
    }

    [Fact]
    public void ExcerptsKeepWholeSentencesNearestThePart()
    {
        var sentences = string.Concat(Enumerable.Range(1, 40).Select(i => $"Sentence number {i} is here. "));
        var end = SegmentedPolish.Excerpt(sentences, fromEnd: true);
        var start = SegmentedPolish.Excerpt(sentences, fromEnd: false);
        Assert.EndsWith("Sentence number 40 is here.", end);
        Assert.StartsWith("Sentence number 1 is here.", start);
        Assert.InRange(WordCount.Count(end), SegmentedPolish.SurroundingWords - 5, SegmentedPolish.SurroundingWords);
        Assert.InRange(WordCount.Count(start), SegmentedPolish.SurroundingWords - 5, SegmentedPolish.SurroundingWords);
        // One sentence longer than that: its nearest words.
        var runOn = string.Join(" ", Enumerable.Range(1, 400).Select(i => $"w{i}"));
        Assert.EndsWith("w400", SegmentedPolish.Excerpt(runOn, fromEnd: true));
        Assert.StartsWith("w1 w2", SegmentedPolish.Excerpt(runOn, fromEnd: false));
        Assert.True(WordCount.Count(SegmentedPolish.Excerpt(runOn, fromEnd: true)) <= SegmentedPolish.SurroundingWords);
        // One part: none.
        Assert.Null(SegmentedPolish.Surrounding(["only"], 0));
        var around = SegmentedPolish.Surrounding(["a.", "b.", "c."], 1)!;
        Assert.Equal(("a.", "c."), (around.Before, around.After));
        Assert.Equal(new SurroundingParts(null, "b."), SegmentedPolish.Surrounding(["a.", "b.", "c."], 0));
    }

    private static string LongTranscript(string sentence, int count) => string.Concat(Enumerable.Repeat(sentence, count));

    private static HedgedPolishResult Answer(string text) =>
        new(new PolishResult(text, false), "m", false, 0.1, 0.2);

    [Fact]
    public async Task PartsGetTheirNeighboursAndCopyingFromThemIsCaught()
    {
        var transcript = LongTranscript("我们把登录模块重写了一遍，然后测试也补上了。", 40);
        var seen = new System.Collections.Concurrent.ConcurrentBag<SurroundingParts?>();
        var outcome = await SegmentedPolish.RunParts(transcript, (part, surrounding, _, _) =>
        {
            seen.Add(surrounding);
            return Task.FromResult(Answer(part));
        }, sendsSurrounding: true);
        Assert.True(outcome.Parts.Count > 1);
        Assert.All(seen, s => Assert.NotNull(s));
        Assert.Single(seen, s => s!.Before is null);
        Assert.Single(seen, s => s!.After is null);
        Assert.Empty(outcome.Fallbacks);
        // An answer that took a sentence over from the part after it goes in as spoken.
        var copied = await SegmentedPolish.RunParts(transcript, (part, surrounding, _, _) =>
            Task.FromResult(Answer(part + (surrounding?.After is { } after ? "另外，" + after[..12] + "部署脚本也改好了" : ""))),
            sendsSurrounding: true);
        Assert.Contains(copied.Fallbacks, p => p.Fallback == PolishFallback.Copied);
        // Without them, the same as before.
        var alone = await SegmentedPolish.RunParts(transcript, (part, surrounding, _, _) =>
        {
            Assert.Null(surrounding);
            return Task.FromResult(Answer(part));
        });
        Assert.Equal(outcome.Text, alone.Text);
    }

    [Fact]
    public async Task RelaidTextIsUsedOnlyWhenItChangedNothingButTheLayout()
    {
        var parts = new SegmentedPolishResult("Fix the login. Add payments. Ship on Friday.",
            [new SegmentedPolishResult.Part("x", Answer("Fix the login. Add payments."), null, null),
             new SegmentedPolishResult.Part("y", Answer("Ship on Friday."), null, null)], 0.1, 0.2);
        var route = new PolishRoute(new ApiClient(ProviderEndpoint.OpenRouter("k"), new HttpClient(new Fixed("- Fix the login.\n- Add payments.\n\nShip on Friday."))),
                                    new PolishOptions("m", Layout: TextLayout.Structured));
        var relaid = await SegmentedPolish.Relaid(parts, route, CancellationToken.None);
        Assert.True(relaid.Relaid);
        Assert.Equal("- Fix the login.\n- Add payments.\n\nShip on Friday.", relaid.Text);
        Assert.Equal(3, relaid.Answers.Count);
        // Reworded: the parts as joined, the answer still priced.
        var reworded = route with { Client = new ApiClient(ProviderEndpoint.OpenRouter("k"), new HttpClient(new Fixed("- Fix login.\n- Add payments.\n\nShip Friday."))) };
        var kept = await SegmentedPolish.Relaid(parts, reworded, CancellationToken.None);
        Assert.False(kept.Relaid);
        Assert.Equal(parts.Text, kept.Text);
        Assert.Equal(3, kept.Answers.Count);
        // No answer at all: as joined.
        var failing = route with { Client = new ApiClient(ProviderEndpoint.OpenRouter("k"), new HttpClient(new Fixed(null))) };
        var failed = await SegmentedPolish.Relaid(parts, failing, CancellationToken.None);
        Assert.Equal(parts.Text, failed.Text);
        Assert.Null(failed.Relayout);
    }

    /// <summary>Answers every request with one streamed text, or with a server error for none.</summary>
    private sealed class Fixed(string? text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (text is null) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"bad\"}}") });
            var chunk = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text }, ["finish_reason"] = "stop" }) };
            var body = "data: " + chunk.ToJsonString() + "\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        }
    }
}
