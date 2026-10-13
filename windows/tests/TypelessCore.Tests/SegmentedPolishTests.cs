using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

public class PolishGuardTests
{

    [Fact]
    public void DroppedWordThresholdStartsAt150AndKeepsAtLeast55Percent()
    {
        Assert.False(Prompts.DroppedTooMuch(new string('我', 149), "我"));
        Assert.True(Prompts.DroppedTooMuch(new string('我', 150), new string('我', 82)));
        Assert.False(Prompts.DroppedTooMuch(new string('我', 150), new string('我', 83)));
    }

    [Fact]
    public void Exactly55PercentIsNotASummary() =>
        Assert.False(Prompts.DroppedTooMuch(new string('我', 200), new string('我', 110)));

    [Fact]
    public void StrayingRequiresSubstitutionsNotJustDeletions()
    {
        Assert.False(Prompts.StrayedFromTranscript("abcdefghi", ""));
        Assert.True(Prompts.StrayedFromTranscript("abcdefghij", " \n!?"));
        Assert.False(Prompts.StrayedFromTranscript("ABCDEFGHIJ", "a b c d e f g h i j!"));
        var input = new string('a', 40);
        Assert.False(Prompts.StrayedFromTranscript(input, "aaa"));
        Assert.False(Prompts.StrayedFromTranscript(input, "aaaaaaabbb")); // exactly 30% changed
        Assert.True(Prompts.StrayedFromTranscript(input, "aaaaaabbbb"));
    }

    [Fact]
    public void FallbackPrecedenceWhenGuardsOverlap()
    {
        var input = new string('我', 150);
        var summary = new string('你', 20);
        Assert.True(Prompts.DroppedTooMuch(input, summary));
        Assert.True(Prompts.StrayedFromTranscript(input, summary));
        Assert.Equal(PolishFallback.Summarised, Prompts.Fallback(input, summary, truncated: false));
        Assert.Equal(PolishFallback.Truncated, Prompts.Fallback(input, summary, truncated: true));
        var answer = new string('你', 500);
        Assert.True(Prompts.LooksLikeAnAnswer(input, answer));
        Assert.True(Prompts.StrayedFromTranscript(input, answer));
        Assert.Equal(PolishFallback.Answered, Prompts.Fallback(input, answer, truncated: false));
        Assert.Equal(PolishFallback.Truncated, Prompts.Fallback(input, answer, truncated: true));
    }

    /// <summary>
    /// testdata/polish-guard-cases.json, shared with the macOS tests so both apps agree: the heaviest legitimate
    /// clean-ups of every eval/ case pass, answers, refusals, translations and summaries don't.
    /// </summary>
    private static JsonArray Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "polish-guard-cases.json")))!
            ["cases"]!.AsArray();

    [Fact]
    public void SharedCases()
    {
        var cases = Cases();
        Assert.True(cases.Count > 50);
        foreach (var node in cases)
        {
            var expected = (string?)node!["expect"];
            var actual = Prompts.Fallback((string)node["input"]!, (string)node["output"]!, truncated: false, (string?)node["context"])
                ?.ToString().ToLowerInvariant();
            Assert.True(expected == actual, $"{node["id"]}: expected {expected ?? "null"}, got {actual ?? "null"}");
        }
    }

    /// <summary>The prompt's own examples are clean-ups by definition.</summary>
    [Fact]
    public void PromptExamplesPass()
    {
        var examples = Examples(Prompts.BasePolishPrompt);
        Assert.True(examples.Count >= 10);
        foreach (var (input, output) in examples) Assert.True(Prompts.Fallback(input, output, truncated: false) == null, input);
    }

    [Fact]
    public void ShrinkingOnlyCountsForLongTranscripts()
    {
        var longText = string.Concat(Enumerable.Repeat("我们把登录模块重写了一遍。", 15)); // 165 words
        Assert.True(Prompts.DroppedTooMuch(longText, longText[..80]));
        Assert.False(Prompts.DroppedTooMuch(longText, longText[..120]));
        // A short sentence that is mostly fillers may lose most of its words.
        Assert.False(Prompts.DroppedTooMuch("嗯嗯嗯那个就是说呃呃好的收到", "好的，收到。"));
    }

    [Fact]
    public void TooShortToJudge()
    {
        Assert.False(Prompts.StrayedFromTranscript("写首诗", "床前明月光"));
        Assert.True(Prompts.StrayedFromTranscript("帮我把这句话翻译成英文：今天天气很好", "The weather is nice today."));
    }

    [Fact]
    public void TruncationComesFirst() => Assert.Equal(PolishFallback.Truncated, Prompts.Fallback("好的收到", "好的", truncated: true));

    internal static List<(string Input, string Output)> Examples(string prompt)
    {
        var lines = prompt.Split("## Examples")[^1].Split('\n');
        var pairs = new List<(string, string)>();
        string? input = null;
        var output = new List<string>();
        void Flush()
        {
            if (input != null) pairs.Add((input, string.Join("\n", output).Trim()));
            output.Clear();
        }
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("<transcript>", StringComparison.Ordinal) && line.EndsWith("</transcript>", StringComparison.Ordinal))
            {
                Flush();
                input = line["<transcript>".Length..^"</transcript>".Length];
            }
            else if (input != null)
            {
                output.Add(line);
            }
        }
        Flush();
        return pairs;
    }
}

public class SegmentedPolishTests
{

    [Fact]
    public void SplitBoundaryAndEmptyInput()
    {
        Assert.Equal([""], SegmentedPolish.Parts(""));
        var single = "  " + new string('我', 400) + "\n";
        Assert.False(SegmentedPolish.NeedsParts(single));
        Assert.Equal([single], SegmentedPolish.Parts(single));
        var longText = new string('我', 401);
        var parts = SegmentedPolish.Parts(" \n" + longText + "\n ");
        Assert.Equal([201, 200], parts.Select(WordCount.Count));
        Assert.Equal(longText, string.Concat(parts));
    }

    [Fact]
    public void SplitKeepsClosingPunctuationAndNumericClauses()
    {
        const string sentences = "“第一句！”  （第二句？）\n第三句。";
        Assert.Equal(["“第一句！”  ", "（第二句？）\n", "第三句。"], SegmentedPolish.Split(sentences, SegmentedPolish.IsSentenceEnd));
        const string clauses = "At 10:30, use 1,000 items: version 3.5, please";
        Assert.Equal(["At 10:30, ", "use 1,000 items: ", "version 3.5, ", "please"], SegmentedPolish.Split(clauses, SegmentedPolish.IsClauseEnd));
    }

    [Fact]
    public void BlankPartsDoNotLoseParagraphBoundaries()
    {
        Assert.Equal("Intro.\n\none\ntwo\n\nNext. Last.", SegmentedPolish.Join(["  ", "Intro.", "", "one\ntwo", " \n ", "Next.", "Last."]));
        Assert.Equal("", SegmentedPolish.Join(["", " \n"]));
        Assert.Equal("오늘은 좋아요.", SegmentedPolish.Join(["오늘은", "좋아요."]));
    }

    private static string Chinese(int sentences) =>
        string.Concat(Enumerable.Range(1, sentences).Select(i => $"我们今天讨论了第{i}个问题，主要是关于缓存和数据库的性能。"));

    private static HedgedPolishResult Answer(string text) =>
        new(new PolishResult(text, false, new RequestUsage(10, 5)), "m", UsedBackup: false, FirstTokenSeconds: 0.1, TotalSeconds: 0.2);

    [Fact]
    public void ShortTranscriptGoesInOneRequestUntouched()
    {
        const string transcript = "  嗯我们周三下午三点开会，不对不对，是周四 ";
        Assert.Equal([transcript], SegmentedPolish.Parts(transcript));
    }

    [Fact]
    public void LongTranscriptIsSplitAtSentenceEnds()
    {
        var transcript = Chinese(30);
        var parts = SegmentedPolish.Parts(transcript);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.EndsWith("。", p));
        Assert.All(parts, p => Assert.InRange(WordCount.Count(p), 150, SegmentedPolish.SingleRequestLimit));
        Assert.Equal(transcript, SegmentedPolish.Join(parts));
    }

    [Fact]
    public void EnglishKeepsNumbersWholeAndJoinsWithSpaces()
    {
        var transcript = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"This is sentence number {i}, and it covers version 3.5 for 1,000 users."));
        var parts = SegmentedPolish.Parts(transcript);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.True(p.EndsWith("users.", StringComparison.Ordinal) && p.StartsWith("This", StringComparison.Ordinal)));
        Assert.Equal(transcript, SegmentedPolish.Join(parts));
    }

    [Fact]
    public void UnpunctuatedRunOnIsStillSplit()
    {
        var transcript = string.Concat(Enumerable.Repeat("我们测试一下这个功能", 70));
        var parts = SegmentedPolish.Parts(transcript);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.True(WordCount.Count(p) <= SegmentedPolish.SingleRequestLimit));
        Assert.Equal(transcript, string.Concat(parts));
    }

    [Fact]
    public void ListsStayAParagraphApart()
    {
        Assert.Equal("帮我改一下：\n\n1. 标题。\n2. 按钮。\n\n另外打包太大了。", SegmentedPolish.Join(["帮我改一下：\n\n1. 标题。\n2. 按钮。", "另外打包太大了。"]));
        Assert.Equal("第一段。第二段。", SegmentedPolish.Join(["第一段。", "第二段。"]));
        Assert.Equal("Done. Next.", SegmentedPolish.Join(["Done.", "Next."]));
    }

    [Fact]
    public async Task PartsAreCleanedInParallelAndChecked()
    {
        var transcript = Chinese(30);
        var parts = SegmentedPolish.Parts(transcript);
        var partials = new ConcurrentQueue<string>();
        var outcome = await SegmentedPolish.Run(transcript, async (part, onPartial, _) =>
        {
            // The second part comes back summarised; the others cleaned up (here: the filler-free text as is).
            var text = part.Contains("第11个") ? "讨论了缓存和数据库的性能问题。" : part;
            onPartial?.Invoke(text);
            await Task.Delay(50);
            return Answer(text);
        }, partials.Enqueue);
        Assert.Equal(new PolishFallback?[] { null, PolishFallback.Summarised, null }, outcome.Parts.Select(p => p.Fallback));
        // The summarised part goes in as spoken, between the parts that were cleaned up.
        Assert.Equal(SegmentedPolish.Join(parts), outcome.Text);
        // Every answer is billed, the rejected one too.
        Assert.Equal(3, outcome.Answers.Count);
        Assert.Equal(outcome.Parts.Sum(p => p.Answer!.Result.Text.Length), partials.Last().Length);
    }

    [Fact]
    public async Task FailedPartGoesInAsSpokenUnlessAllFail()
    {
        var transcript = Chinese(30);
        var outcome = await SegmentedPolish.Run(transcript, (part, _, _) =>
            part.Contains("第1个") ? throw ApiException.Http(503, "down") : Task.FromResult(Answer(part.Replace("主要是", ""))));
        Assert.Equal(new PolishFallback?[] { PolishFallback.Failed, null, null }, outcome.Parts.Select(p => p.Fallback));
        Assert.Equal(503, outcome.Parts[0].Error!.Status);
        Assert.StartsWith(SegmentedPolish.Parts(transcript)[0], outcome.Text);
        Assert.Equal(2, outcome.Answers.Count);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            SegmentedPolish.Run(transcript, (_, _, _) => throw ApiException.Http(401, "bad key")));
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task ShortTranscriptIsCheckedToo()
    {
        var outcome = await SegmentedPolish.Run("what's the capital of France", (_, _, _) => Task.FromResult(Answer("Paris.")));
        Assert.Equal(new PolishFallback?[] { PolishFallback.Unrelated }, outcome.Parts.Select(p => p.Fallback));
        Assert.Equal("what's the capital of France", outcome.Text);
    }
}
