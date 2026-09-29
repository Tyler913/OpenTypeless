using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>
/// Fake chat server whose behaviour depends on the requested model: how long it waits before
/// answering, and whether it fails. One per test, so it can't race the MockOpenRouter tests.
/// </summary>
public sealed class MockChatServer : HttpMessageHandler
{
    public sealed record Behaviour(double Delay = 0, int Status = 200, string Text = "ok");

    private readonly Dictionary<string, Behaviour> _behaviours;
    private readonly List<string> _requested = new();

    public MockChatServer(Dictionary<string, Behaviour> behaviours) => _behaviours = behaviours;

    public List<string> Requested
    {
        get { lock (_requested) return _requested.ToList(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var model = (string?)JsonNode.Parse(body)?["model"] ?? "";
        lock (_requested) _requested.Add(model);
        var behaviour = _behaviours.GetValueOrDefault(model) ?? new Behaviour();
        await Task.Delay(TimeSpan.FromSeconds(behaviour.Delay), cancellationToken);
        var data = behaviour.Status == 200
            ? $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{behaviour.Text}\"}},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n"
            : """{"error":{"message":"upstream down"}}""";
        return new HttpResponseMessage((HttpStatusCode)behaviour.Status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(data)), RequestMessage = request };
    }

    public PolishRoute Route(string model) =>
        new(new ApiClient(ProviderEndpoint.OpenRouter("k"), new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }),
            new PolishOptions(model));
}

public class HedgedPolishTests
{
    [Fact]
    public async Task FastPrimaryNeverStartsBackup()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Text: "A"), ["backup"] = new(Text: "B") });
        var outcome = await HedgedPolish.Run("x", server.Route("primary"), server.Route("backup"), hedgeDelay: 0.5);
        Assert.Equal("A", outcome.Result.Text);
        Assert.False(outcome.UsedBackup);
        Assert.Equal(["primary"], server.Requested);
    }

    [Fact]
    public async Task SlowPrimaryIsOvertakenByBackup()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Delay: 2, Text: "A"), ["backup"] = new(Text: "B") });
        var outcome = await HedgedPolish.Run("x", server.Route("primary"), server.Route("backup"), hedgeDelay: 0.2);
        Assert.Equal("B", outcome.Result.Text);
        Assert.True(outcome.UsedBackup);
        Assert.Equal("backup", outcome.Model);
        Assert.True(outcome.TotalSeconds < 1.5);
    }

    [Fact]
    public async Task PartialTextComesOnlyFromTheStreamThatWon()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Delay: 2, Text: "A"), ["backup"] = new(Text: "B") });
        var partials = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var outcome = await HedgedPolish.Run("x", server.Route("primary"), server.Route("backup"), hedgeDelay: 0.2,
                                             onPartial: partials.Enqueue);
        Assert.Equal("B", outcome.Result.Text);
        Assert.Equal(["B"], partials);
    }

    [Fact]
    public async Task PrimaryFailureStartsBackupWithoutWaiting()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Status: 503), ["backup"] = new(Text: "B") });
        var outcome = await HedgedPolish.Run("x", server.Route("primary"), server.Route("backup"), hedgeDelay: 5);
        Assert.Equal("B", outcome.Result.Text);
        Assert.True(outcome.TotalSeconds < 2);
    }

    [Fact]
    public async Task SlowBackupDoesNotBeatPrimaryThatAnswersFirst()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Delay: 0.4, Text: "A"), ["backup"] = new(Delay: 2, Text: "B") });
        var outcome = await HedgedPolish.Run("x", server.Route("primary"), server.Route("backup"), hedgeDelay: 0.1);
        Assert.Equal("A", outcome.Result.Text);
        Assert.Equal(["backup", "primary"], server.Requested.Order());
    }

    [Fact]
    public async Task BothFailingThrowsThePrimaryError()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Status: 401), ["backup"] = new(Status: 503) });
        var error = await Assert.ThrowsAsync<ApiException>(() => HedgedPolish.Run("x", server.Route("primary"), server.Route("backup"), hedgeDelay: 0.1));
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task WorksWithoutBackup()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Delay: 0.3, Text: "A") });
        var outcome = await HedgedPolish.Run("x", server.Route("primary"), backup: null, hedgeDelay: 0.05);
        Assert.Equal("A", outcome.Result.Text);
    }

    [Fact]
    public async Task CancellingStopsBothRequests()
    {
        var server = new MockChatServer(new() { ["primary"] = new(Delay: 5), ["backup"] = new(Delay: 5) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(0.3));
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            HedgedPolish.Run("x", server.Route("primary"), server.Route("backup"), hedgeDelay: 0.1, cancellationToken: cancel.Token));
        Assert.True(ApiException.From(error).IsCancelled);
    }
}

public class PolishMetricsTests
{
    [Fact]
    public void TranslatedEnglishLowersRetention()
    {
        const string input = "有没有办法设置一个 shortcut，然后切换 dark mode";
        Assert.Equal(1, PolishMetrics.LatinRetention(input, "有没有办法设置一个 shortcut，切换 dark mode？"));
        Assert.Equal(0, PolishMetrics.LatinRetention(input, "有没有办法设置一个快捷键，切换深色模式？"));
        // Recognition fixes that join words still count as kept.
        Assert.Equal(1, PolishMetrics.LatinRetention("我用 swift UI 写的界面", "我用 SwiftUI 写的界面。"));
        Assert.Null(PolishMetrics.LatinRetention("Send it to Sarah", "Send it to Sarah."));
    }

    [Fact]
    public void SimilarityIgnoresPunctuationAndSpacing()
    {
        Assert.Equal(1, PolishMetrics.Similarity("用 SwiftUI 写", "用SwiftUI写。"));
        Assert.Equal(0.75, PolishMetrics.Similarity("abcd", "abcf"));
    }
}

public class CorrectionLearnerTests
{
    private static readonly HashSet<string> Common = ["logic", "there", "their", "hello", "send", "sent", "report", "the", "mode"];

    private static List<Correction> Learn(string original, string edited) =>
        CorrectionLearner.Corrections(original, edited, word => Common.Contains(word.ToLowerInvariant()));

    [Fact]
    public void LearnsMisspelledProductNames()
    {
        Assert.Equal([new Correction("TypeList", "Typeless")], Learn("我在用 TypeList 写东西", "我在用 Typeless 写东西"));
        Assert.Equal([new Correction("Logic", "Logitech")], Learn("Logic 鼠标的按键", "Logitech 鼠标的按键"));
        Assert.Equal([new Correction("Envious Sales", "EnviousSales")], Learn("try Envious Sales today", "try EnviousSales today"));
    }

    [Fact]
    public void LearnsChineseHomophones()
    {
        // Needs the OS's ICU for pinyin; without it Chinese fixes are simply never learned. Windows 10 1903+ always has it.
        if (OperatingSystem.IsWindows()) Assert.True(Transliteration.IsAvailable);
        if (!Transliteration.IsAvailable) return;
        Assert.Equal([new Correction("逻辑", "罗技")], Learn("我用的是逻辑鼠标", "我用的是罗技鼠标"));
    }

    /// <summary>Stands in for the system's word segmenter: known words whole, anything else a character at a time.</summary>
    private static IReadOnlyList<(int Start, int End)> Words(string text)
    {
        string[] known = ["罗技", "鼠标", "一个", "这个", "模型", "叫做", "打开", "文档", "高兴", "项目", "前端", "打算"];
        var words = new List<(int, int)>();
        for (var i = 0; i < text.Length;)
        {
            var word = known.FirstOrDefault(k => string.CompareOrdinal(text, i, k, 0, k.Length) == 0);
            var length = word?.Length ?? 1;
            if (!char.IsWhiteSpace(text[i])) words.Add((i, i + length));
            i += length;
        }
        return words;
    }

    private static List<Correction> LearnWords(string original, string edited) =>
        CorrectionLearner.Corrections(original, edited, word => Common.Contains(word.ToLowerInvariant()), Words);

    [Fact]
    public void OneWrongCharacterLearnsTheWholeWord()
    {
        if (!Transliteration.IsAvailable) return;
        Assert.Equal([new Correction("罗级", "罗技")], LearnWords("我买了一个罗级鼠标", "我买了一个罗技鼠标"));
        // Names the segmenter doesn't know come out as single characters (千|问); the neighbours are taken in.
        Assert.Equal([new Correction("千文", "千问")], LearnWords("这个模型叫做千文", "这个模型叫做千问"));
        Assert.Equal([new Correction("非书", "飞书")], LearnWords("打开非书文档", "打开飞书文档"));
        // A particle fixed is grammar, not a word.
        Assert.Empty(LearnWords("他高兴的跑了", "他高兴地跑了"));
        // Without a segmenter, one character stays too little to learn.
        Assert.Empty(Learn("我买了一个罗级鼠标", "我买了一个罗技鼠标"));
    }

    [Fact]
    public void LearnsEnglishThatSoundsAlike()
    {
        Assert.Equal([new Correction("Versel", "Vercel")], Learn("部署到 Versel 上", "部署到 Vercel 上"));
        Assert.True(CorrectionLearner.SoundsAlike("Cooper Netties", "Kubernetes")); // KPRNTS / KBRNTS
        if (!Transliteration.IsAvailable) return;
        Assert.Equal([new Correction("克劳德", "Claude")], Learn("我在用克劳德写代码", "我在用 Claude 写代码"));
        Assert.Equal([new Correction("杰森", "JSON")], Learn("返回一个杰森", "返回一个 JSON"));
        Assert.Equal([new Correction("瑞艾克特", "React")], Learn("这个项目的前端我打算用瑞艾克特来写", "这个项目的前端我打算用 React 来写"));
    }

    [Fact]
    public void ReviewSaysWhyAChangeWasNotLearned()
    {
        var review = CorrectionLearner.ReviewEdit("Send the report to Mike", "Send the report to Sarah");
        Assert.Equal(new[] { new CorrectionLearner.Change(new Correction("Mike", "Sarah"), "sounds different") }, review.Changes);
        Assert.Equal("rewrite", CorrectionLearner.ReviewEdit("Please send the quarterly numbers to the finance team by Friday",
                                                             "Can we talk about this tomorrow instead").Skipped);
    }

    [Fact]
    public void LearnsDistinctiveCasingOnly()
    {
        Assert.Equal([new Correction("swiftui", "SwiftUI")], Learn("写 swiftui 页面", "写 SwiftUI 页面"));
        Assert.Empty(Learn("hello there", "Hello there"));
    }

    [Fact]
    public void IgnoresEditsThatAreNotMishearings()
    {
        Assert.Empty(Learn("Send the report to Mike", "Send the report to Sarah"));  // different name
        Assert.Empty(Learn("大概 50 个", "大概 60 个"));                               // changed number
        Assert.Empty(Learn("大概五十个人", "大概六十个人"));                             // changed number, in Chinese
        Assert.Empty(Learn("I think their right", "I think there right"));           // ordinary words
        Assert.Empty(Learn("我们周三开会", "我们周四开会"));                           // one character
        Assert.Empty(Learn("这个功能不急", "这个功能不急，下个月再做"));                   // pure insertion
        Assert.Empty(Learn("Please send the quarterly numbers to the finance team by Friday",
                           "Can we talk about this tomorrow instead"));               // rewrite
    }

    [Fact]
    public void FindsTheDictatedTextInTheField()
    {
        const string before = "Hi team,\n我在用 TypeList 写东西\nThanks";
        Assert.Equal("我在用 Typeless 写东西",
                     CorrectionLearner.EditedRegion("我在用 TypeList 写东西", before, "Hi team,\n我在用 Typeless 写东西\nThanks"));
        // Windows edit controls report line breaks as CRLF.
        Assert.Equal("我在用 Typeless 写东西",
                     CorrectionLearner.EditedRegion("我在用 TypeList 写东西", before.Replace("\n", "\r\n"), "Hi team,\r\n我在用 Typeless 写东西\r\nThanks"));
        // Sent and cleared, or edited outside the dictated text: can't tell what changed.
        Assert.Null(CorrectionLearner.EditedRegion("我在用 TypeList 写东西", before, ""));
        Assert.Null(CorrectionLearner.EditedRegion("我在用 TypeList 写东西", before, "Hello team,\n我在用 TypeList 写东西\nThanks"));
    }

    [Fact]
    public void TokenizesTechnicalWords()
    {
        Assert.Equal(["用", "Node.js", "和", "C++", "写"], CorrectionLearner.Tokens("用 Node.js 和 C++ 写").Select(t => t.Text));
    }

    [Fact]
    public void PromptListsMisheardForms()
    {
        var prompt = Prompts.PolishSystemPrompt(["Typeless"], "", [new Correction("TypeList", "Typeless")]);
        Assert.Contains("spell them exactly like this: Typeless", prompt);
        Assert.Contains("“TypeList” → Typeless", prompt);
    }
}
