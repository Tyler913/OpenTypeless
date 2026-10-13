using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>Translation mode: its guards, its prompt, its targets and chord, and the fallback to the clean-up.</summary>
public class TranslationTests
{
    /// <summary>testdata/translate-guard-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Fixture([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "translate-guard-cases.json")))!
            .AsObject();

    [Fact]
    public void SharedCases()
    {
        var cases = Fixture()["cases"]!.AsArray();
        Assert.True(cases.Count >= 15);
        foreach (var node in cases)
        {
            var target = TranslationTarget.From((string)node!["target"]!)!;
            var expected = (string?)node["expect"];
            var actual = Prompts.TranslationFallback((string)node["input"]!, (string)node["output"]!, truncated: false, target)
                ?.ToString().ToLowerInvariant();
            Assert.True(expected == actual, $"{node["id"]}: expected {expected ?? "null"}, got {actual ?? "null"}");
        }
    }

    /// <summary>The macOS app sends the same template: the fixture holds its hash.</summary>
    [Fact]
    public void PromptIsTheSameOnBothPlatforms()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Prompts.BaseTranslatePrompt))).ToLowerInvariant();
        Assert.Equal((string)Fixture()["translatePromptSHA256"]!, hash);
    }

    /// <summary>The prompt's own examples are translations by definition, into the language each group names.</summary>
    [Fact]
    public void PromptExamplesPass()
    {
        var count = 0;
        foreach (var section in Prompts.BaseTranslatePrompt.Split("## Examples into ").Skip(1))
        {
            var lines = section.Split('\n');
            var target = TranslationTarget.From(lines[0])!;
            Assert.NotNull(target.Language);
            foreach (var (input, output) in PolishGuardTests.Examples("## Examples\n" + string.Join("\n", lines.Skip(1))))
            {
                Assert.True(Prompts.TranslationFallback(input, output, truncated: false, target) == null, input);
                count++;
            }
        }
        Assert.True(count >= 5);
    }

    [Fact]
    public void TruncationComesFirstAndLengthIsGenerous()
    {
        var english = new TranslationTarget(TranslationLanguage.English);
        Assert.Equal(PolishFallback.Truncated, Prompts.TranslationFallback("好的收到", "OK", truncated: true, english));
        // A katakana-heavy Japanese translation is far longer than its English, and still fine.
        Assert.False(Prompts.TranslationLooksLikeAnAnswer("check the login page", "ログインページをもう一度確認してください"));
        // A short transcript isn't judged for what it drops; a long one kept to a few words was summarised.
        Assert.False(Prompts.TranslationDroppedTooMuch("嗯嗯那个就是说呃好的收到", "OK."));
    }

    [Fact]
    public void ScriptShareCountsWordsNotLetters()
    {
        // A Han character weighs as much as a whole Latin word, so terms kept in a Chinese translation don't outweigh it.
        Assert.Equal(4.0 / 7.0, ScriptShare.Share(WritingScript.Han, "用 SwiftUI 写一个 macOS app"), 9);
        Assert.Equal(4.0 / 6.0, ScriptShare.Share(WritingScript.Latin, "Tell 张伟 at 3 p.m."), 9);
        Assert.Equal(1.0, ScriptShare.Share(WritingScript.Cyrillic, "Встреча в 15:00"));
        Assert.Equal(1.0, ScriptShare.Share(WritingScript.Hangul, "123 — !")); // nothing to count
        // Accents written as their own character stay with their letter.
        Assert.Equal(1.0, ScriptShare.Share(WritingScript.Latin, "porta\u0301til"));
        Assert.True(ScriptShare.IsMostly("好的", WritingScript.Latin)); // too short to tell
    }

    [Fact]
    public void PromptConstruction()
    {
        var target = new TranslationTarget(TranslationLanguage.Chinese);
        var system = Prompts.TranslateSystemPrompt(target, ["Typeless", " "], "Keep it casual", [new Correction("type less", "Typeless")]);
        Assert.StartsWith("You turn a raw speech-to-text transcript into the text the speaker would have typed, written in Simplified Chinese.", system);
        Assert.DoesNotContain(Prompts.LanguagePlaceholder, system);
        Assert.Contains("## Vocabulary\nThese terms may appear; spell them exactly like this: Typeless", system);
        Assert.Contains("“type less” → Typeless", system);
        Assert.EndsWith("## Additional preferences from the speaker\nKeep it casual", system);

        var user = Prompts.TranslateUserMessage("send it to Sarah", target);
        Assert.StartsWith("<transcript>\nsend it to Sarah\n</transcript>\n\n(Translate the transcript above into Simplified Chinese", user);
        Assert.EndsWith("Output only the Simplified Chinese text.)", user);

        // The same options ask for a translation once they name a target, and for a clean-up without one.
        var options = new PolishOptions("m", ["Typeless"]);
        Assert.Equal(new[] { Prompts.PolishSystemPrompt(["Typeless"], ""), Prompts.PolishUserMessage("hi") }, ApiClient.PolishMessages("hi", options));
        var messages = ApiClient.PolishMessages("hi", options with { Translation = TranslationTarget.From("Italiano") });
        Assert.Contains("written in Italiano.", messages[0]);
        Assert.EndsWith("Output only the Italiano text.)", messages[1]);

        // A reminder echoed back is taken out, whichever was sent.
        var reminder = Prompts.Reminder(target);
        Assert.Equal("把报告发给 Sarah。", Prompts.SanitizePolishOutput("把报告发给 Sarah。\n\n" + reminder, reminder));
        Assert.Equal("Hi.", Prompts.SanitizePolishOutput("Hi.\n" + Prompts.PolishReminder));
    }

    [Fact]
    public void Targets()
    {
        Assert.Null(TranslationTarget.From("  "));
        Assert.Equal(TranslationLanguage.English, TranslationTarget.From(" en ")!.Language);
        Assert.Equal(TranslationLanguage.Japanese, TranslationTarget.From("日本語")!.Language);
        Assert.Equal("→ ZH", TranslationTarget.From("Simplified Chinese")!.Badge);
        Assert.Null(TranslationTarget.From("Italiano")!.Language);
        Assert.Equal("→ Italiano", TranslationTarget.From("Italiano")!.Badge);
        Assert.Null(TranslationTarget.From("Italiano")!.Script);
        Assert.Equal("한국어", new TranslationTarget(TranslationLanguage.Korean).DisplayName);
        Assert.Equal("Russian", new TranslationTarget(TranslationLanguage.Russian).PromptName);
        Assert.Equal(9, TranslationLanguages.All.Length);
        // Saved under the same names as on macOS.
        Assert.Equal(new[] { "en", "zh-Hans", "ja", "ko", "es", "fr", "de", "pt", "ru" }, TranslationLanguages.All.Select(l => l.Code()));
        Assert.Equal(new[] { "shift", "control", "alt" }, TranslationChords.All.Select(c => c.RawValue()));
        Assert.Equal(TranslationChord.Alt, TranslationChords.Parse("alt"));
        Assert.Null(TranslationChords.Parse("option"));
    }

    /// <summary>
    /// What History saves with a translation dictation reads back as the same target; a plain dictation (or one saved
    /// before translation existed) saves nothing and reads back as no translation.
    /// </summary>
    [Fact]
    public void RecordModeRoundTrip()
    {
        foreach (var target in TranslationLanguages.All.Select(l => new TranslationTarget(l)).Append(TranslationTarget.From("Italiano")!))
        {
            Assert.Equal(target, TranslationTarget.FromRecord(TranslationTarget.RecordMode, target.Id));
        }
        Assert.Null(TranslationTarget.FromRecord(null, null));
        Assert.Null(TranslationTarget.FromRecord("translate", null));
        Assert.Null(TranslationTarget.FromRecord("somethingNewer", "en"));
    }

    private enum Mode { Dictation, Translate }

    /// <summary>
    /// Holding the hotkey and pressing either Shift switches the recording to translation, for as long as the hotkey is
    /// held; any other key still makes a combination that cancels it.
    /// </summary>
    [Fact]
    public void ChordSwitchesToTranslationAndOtherKeysStillCancel()
    {
        Assert.True(TranslationChord.Shift.KeyCodes(0xA5).SetEquals([0xA0, 0xA1])); // Right Alt
        Assert.True(TranslationChord.Shift.KeyCodes(0xA1).SetEquals([0xA0])); // Right Shift is the hotkey
        Assert.True(TranslationChord.Alt.KeyCodes(null).SetEquals([0xA4, 0xA5]));
        Assert.True(TranslationChord.Control.KeyCodes(0xA3).SetEquals([0xA2]));

        var chords = new ModeChords<Mode>();
        foreach (var key in TranslationChord.Shift.KeyCodes(0xA5)) chords.Register(key, Mode.Translate);
        Assert.Equal(new OtherKeyOutcome<Mode>.SwitchMode(Mode.Translate), chords.OtherKey(0xA0, 10, 10.1, handsFree: false));
        Assert.Equal(new OtherKeyOutcome<Mode>.SwitchMode(Mode.Translate), chords.OtherKey(0xA1, 10, 25, handsFree: false));
        Assert.Equal(new OtherKeyOutcome<Mode>.Cancel(), chords.OtherKey(0x43, 10, 10.1, handsFree: false)); // Right Alt + C
        Assert.Equal(new OtherKeyOutcome<Mode>.Ignore(), chords.OtherKey(0x43, 10, 12, handsFree: false));
    }

    // MARK: Falling back to the clean-up

    private static HedgedPolishResult Answer(string text) =>
        new(new PolishResult(text, false, new RequestUsage(10, 5)), "m", UsedBackup: false, FirstTokenSeconds: 0.1, TotalSeconds: 0.2);

    private static SegmentedPolishResult Result(string[] texts, PolishFallback?[] fallbacks)
    {
        var parts = texts.Zip(fallbacks, (text, fallback) => new SegmentedPolishResult.Part("原文", Answer(text), fallback, null)).ToList();
        return new SegmentedPolishResult(SegmentedPolish.Join(parts.Select(p => p.Text)), parts, 0.1, 0.2);
    }

    [Fact]
    public async Task AUsableTranslationTakesOneRequest()
    {
        var calls = new ConcurrentQueue<string>();
        var outcome = await TranslatedPolish.Run(() =>
        {
            calls.Enqueue("translate");
            return Task.FromResult(Result(["Hello."], [null]));
        }, () =>
        {
            calls.Enqueue("clean-up");
            return Task.FromResult(Result(["你好。"], [null]));
        });
        Assert.True(outcome.Translated);
        Assert.Null(outcome.TranslationFallback);
        Assert.Null(outcome.CleanUp);
        Assert.Equal(new[] { "translate" }, calls);
    }

    [Fact]
    public async Task AnUnusableTranslationFallsBackToTheCleanUp()
    {
        // One part came back untranslated: the whole translation is set aside, not inserted half translated.
        var partly = await TranslatedPolish.Run(() => Task.FromResult(Result(["Hello.", "你好。"], [null, PolishFallback.Untranslated])),
                                                () => Task.FromResult(Result(["你好。"], [null])));
        Assert.False(partly.Translated);
        Assert.Equal(PolishFallback.Untranslated, partly.TranslationFallback);
        Assert.NotNull(partly.Translation); // billed all the same
        Assert.Equal("你好。", partly.CleanUp?.Text);

        var failed = await TranslatedPolish.Run(() => throw ApiException.Http(503, "down"), () => Task.FromResult(Result(["你好。"], [null])));
        Assert.Equal(PolishFallback.Failed, failed.TranslationFallback);
        Assert.Equal(503, failed.TranslationError!.Status);
        Assert.Equal("你好。", failed.CleanUp?.Text);

        // Clean-up off: the transcript goes in as spoken.
        var off = await TranslatedPolish.Run(() => Task.FromResult(Result(["你好。"], [PolishFallback.Untranslated])), null);
        Assert.True(!off.Translated && off.CleanUp == null && off.CleanUpError == null);

        var both = await TranslatedPolish.Run(() => throw ApiException.TimedOut(10), () => throw ApiException.Http(401, "bad key"));
        Assert.Null(both.CleanUp);
        Assert.Equal(401, both.CleanUpError!.Status);
    }

    [Fact]
    public async Task CancellationIsPassedOn()
    {
        var first = await Assert.ThrowsAsync<ApiException>(() =>
            TranslatedPolish.Run(() => throw ApiException.Cancelled(), () => Task.FromResult(Result(["x"], [null]))));
        Assert.True(first.IsCancelled);
        var second = await Assert.ThrowsAsync<ApiException>(() =>
            TranslatedPolish.Run(() => Task.FromResult(Result(["x"], [PolishFallback.Failed])), () => throw new OperationCanceledException()));
        Assert.True(second.IsCancelled);
    }

    /// <summary>SegmentedPolish checks a translation with the translation's guards: the clean-up's would reject it as unrelated.</summary>
    [Fact]
    public async Task SegmentedPolishChecksATranslationAsOne()
    {
        const string transcript = "嗯我们周三下午三点开会，不对不对，是周四，然后记得带电脑";
        const string english = "We're meeting on Thursday at 3 p.m. Remember to bring your laptop.";
        Assert.Equal(PolishFallback.Unrelated, Prompts.Fallback(transcript, english, truncated: false));
        var target = new TranslationTarget(TranslationLanguage.English);
        var translated = await SegmentedPolish.Run(transcript, (_, _, _) => Task.FromResult(Answer(english)), translation: target);
        Assert.Empty(translated.Fallbacks);
        Assert.Equal(english, translated.Text);
        var untranslated = await SegmentedPolish.Run(transcript, (_, _, _) => Task.FromResult(Answer("我们周四下午三点开会，记得带电脑。")),
                                                     translation: target);
        Assert.Equal(new PolishFallback?[] { PolishFallback.Untranslated }, untranslated.Parts.Select(p => p.Fallback));
    }
}
