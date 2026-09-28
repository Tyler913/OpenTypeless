using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>Word counts, request usage and pricing, and the ledger behind the Home page.</summary>
[Collection("MockServer")]
public class UsageTests
{
    /// <summary>testdata/usage-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "usage-cases.json")))!.AsObject();

    private static ProviderEndpoint Endpoint(ProviderId id) => new(id, new Uri("https://example.com/v1"), "k");

    [Fact]
    public void WordCountsMatchSharedCases()
    {
        foreach (var node in Cases()["wordCounts"]!.AsArray())
        {
            var text = (string)node!["text"]!;
            Assert.True((int)node["words"]! == WordCount.Count(text), $"\"{text}\": expected {node["words"]}, got {WordCount.Count(text)}");
        }
    }

    [Fact]
    public void CostsMatchSharedCases()
    {
        foreach (var node in Cases()["costs"]!.AsArray())
        {
            var u = node!["usage"]!;
            var usage = new RequestUsage((int?)u["input"], (int?)u["output"], (double?)u["seconds"], (double?)u["cost"],
                                         Estimated: (bool?)u["estimated"] ?? false);
            var price = ModelPrice.FromJson(node["price"]);
            var preferReported = (bool)node["preferReported"]!;
            var cost = (string)node["kind"]! == "chat"
                ? CostEstimator.Chat(usage, price, preferReported)
                : CostEstimator.Transcription(usage, price, preferReported);
            var expected = (double?)node["expected"];
            if (expected is null) Assert.Null(cost);
            else Assert.True(cost is { } c && Math.Abs(c - expected.Value) < 1e-12, $"{node.ToJsonString()}: got {cost}");
        }
    }

    [Fact]
    public void TimeSavedMatchesSharedCases()
    {
        foreach (var node in Cases()["timeSaved"]!.AsArray())
        {
            var totals = new UsageTotals((int)node!["words"]!, 1, (double)node["seconds"]!, 0, 0, 0);
            var wpm = (double)node["typingWPM"]!;
            Assert.Equal((double)node["typingSeconds"]!, totals.TypingSeconds(wpm), 6);
            Assert.Equal((double)node["savedSeconds"]!, totals.SavedSeconds(wpm), 6);
            var speaking = (double?)node["speakingWPM"];
            if (speaking is null) Assert.Null(totals.SpeakingWordsPerMinute);
            else Assert.Equal(speaking.Value, totals.SpeakingWordsPerMinute!.Value, 6);
        }
    }

    [Fact]
    public void ParsesUsageFromTranscriptionsAndChat()
    {
        var stt = RequestUsage.Parse(JsonNode.Parse("""{"seconds":9.2,"total_tokens":113,"input_tokens":83,"output_tokens":30,"cost":0.000508}"""))!;
        Assert.Equal(new RequestUsage(83, 30, 9.2, 0.000508), stt);
        var chat = RequestUsage.Parse(JsonNode.Parse("""{"prompt_tokens":194,"completion_tokens":2,"total_tokens":196,"cost":0.95}"""))!;
        Assert.Equal(new RequestUsage(194, 2, null, 0.95), chat);
        Assert.Null(RequestUsage.Parse(JsonNode.Parse("""{"type":"duration"}""")));
        Assert.Null(RequestUsage.Parse(null));
        Assert.Null(RequestUsage.Parse(JsonNode.Parse("""{"cost":-1}""")));

        var events = new SseParser().Parse("""data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"cost":0.0001}}""");
        Assert.Contains(new SseEvent.Usage(new RequestUsage(10, 5, null, 0.0001)), events);
        Assert.Contains(new SseEvent.Finished("stop"), events);
    }

    [Fact]
    public void EstimatesTokensFromText()
    {
        Assert.Equal(0, TokenEstimate.Count(""));
        Assert.Equal(4, TokenEstimate.Count("你好世界"));
        Assert.Equal(3, TokenEstimate.Count("hello world")); // 11 characters / 4, rounded up
    }

    [Fact]
    public async Task TranscriptionReportsUsageAndAudioLength()
    {
        MockOpenRouter.Handler = (_, _) => (200, MockOpenRouter.Utf8("""{"text":"hi","usage":{"seconds":1.5,"cost":0.0002}}"""));
        var client = new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client());
        var samples = new short[AudioFormat.SampleCount(2)];
        var reported = await client.TranscribeDetailedWithRetry(samples, new TranscriptionOptions("m"));
        Assert.Equal(new RequestUsage(null, null, 1.5, 0.0002), reported.Usage);

        // A server that reports nothing still gives the audio length, so per-minute prices work.
        MockOpenRouter.Handler = (_, _) => (200, MockOpenRouter.Utf8("""{"text":"hi"}"""));
        var bare = await client.TranscribeDetailedWithRetry(samples, new TranscriptionOptions("m"));
        Assert.Equal(2, bare.Usage!.AudioSeconds!.Value, 3);
        Assert.True(bare.Usage.Estimated);
        Assert.Equal("hi", await client.TranscribeWithRetry(samples, new TranscriptionOptions("m")));
    }

    [Fact]
    public async Task PolishReportsStreamUsageOrEstimatesIt()
    {
        MockOpenRouter.Handler = (_, _) => (200, MockOpenRouter.Utf8(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":3,\"cost\":0.00042}}\n\n" +
            "data: [DONE]\n\n"));
        var openRouter = new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client());
        var reported = await openRouter.Polish("x", new PolishOptions("m"));
        Assert.Equal(new RequestUsage(900, 3, null, 0.00042), reported.Usage);

        var bodies = new List<JsonNode>();
        MockOpenRouter.Handler = (_, body) =>
        {
            bodies.Add(JsonNode.Parse(body)!);
            return (200, MockOpenRouter.Utf8("data: {\"choices\":[{\"delta\":{\"content\":\"好的\"}}]}\n\ndata: [DONE]\n\n"));
        };
        var custom = new ApiClient(Endpoint(ProviderId.Custom), MockOpenRouter.Client());
        var estimated = await custom.Polish("x", new PolishOptions("m"));
        Assert.True((bool)bodies[0]["stream_options"]!["include_usage"]!);
        Assert.True(estimated.Usage!.Estimated);
        Assert.Equal(2, estimated.Usage.OutputTokens);
        Assert.True(estimated.Usage.InputTokens > 500); // the system prompt is most of it
    }

    [Fact]
    public async Task PolishDropsStreamOptionsWhenRejected()
    {
        var bodies = new List<JsonNode>();
        MockOpenRouter.Handler = (_, body) =>
        {
            var json = JsonNode.Parse(body)!;
            bodies.Add(json);
            if (json["stream_options"] != null) return (400, MockOpenRouter.Utf8("""{"error":{"message":"Unrecognized request argument supplied: stream_options"}}"""));
            return (200, MockOpenRouter.Utf8("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"));
        };
        var result = await new ApiClient(Endpoint(ProviderId.OpenAI), MockOpenRouter.Client()).Polish("x", new PolishOptions("m"));
        Assert.Equal("ok", result.Text);
        Assert.Equal(2, bodies.Count);
        Assert.Null(bodies[1]["stream_options"]);
    }

    [Fact]
    public void OpenRouterBodyHasNoStreamOptions()
    {
        var body = new ApiClient(ProviderEndpoint.OpenRouter("k")).PolishBody("x", new PolishOptions("m"), true);
        Assert.Null(body["stream_options"]); // OpenRouter always reports usage; the parameter is deprecated there
    }

    [Fact]
    public async Task PipelineCollectsUsageOfEveryRequest()
    {
        MockOpenRouter.Handler = (_, _) => (200, MockOpenRouter.Utf8("""{"text":"part","usage":{"cost":0.001}}"""));
        var pipeline = new TranscriptionPipeline(new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client()), new TranscriptionOptions("m"));
        var samples = new short[AudioFormat.SampleCount(65)];
        for (var i = 0; i < samples.Length; i++) samples[i] = AudioFormat.Seconds(i) % 20 < 0.5 ? (short)0 : (short)(6000 * Math.Sin(i * 0.2));
        pipeline.Append(samples);
        await pipeline.Finish();
        var usages = pipeline.Usages();
        Assert.True(usages.Count >= 3);
        Assert.All(usages, u => Assert.Equal(0.001, u.Cost));
        Assert.Equal(65, usages.Sum(u => u.AudioSeconds ?? 0), 0);
    }

    [Fact]
    public void ParsesOpenRouterPrices()
    {
        var json = MockOpenRouter.Utf8("""
            {"data":[
              {"id":"google/gemini-3.8-flash","pricing":{"prompt":"0.00000075","completion":"0.00000375","web_search":"0.014"}},
              {"id":"openrouter/auto","pricing":{"prompt":"-1","completion":"-1"}},
              {"id":"free/model","pricing":{"prompt":"0","completion":"0"}},
              {"id":"broken"}
            ]}
            """);
        var models = PriceCatalog.ParseModels(json);
        Assert.Equal(2, models.Count);
        Assert.Equal(0.75, models["google/gemini-3.8-flash"].InputPerMillion!.Value, 9);
        Assert.Equal(3.75, models["google/gemini-3.8-flash"].OutputPerMillion!.Value, 9);
        Assert.Equal(0, models["free/model"].InputPerMillion);

        var catalog = new PriceCatalog(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), models);
        var restored = PriceCatalog.FromJson(catalog.ToJson())!;
        Assert.Equal(catalog.FetchedAt, restored.FetchedAt);
        Assert.Equal(catalog.Price("google/gemini-3.8-flash"), restored.Price("google/gemini-3.8-flash"));
        Assert.True(restored.IsOlderThan(TimeSpan.FromHours(6), catalog.FetchedAt.AddHours(7)));
        Assert.Null(PriceCatalog.FromJson("not json"));
    }

    [Fact]
    public async Task FetchesBothOpenRouterModelLists()
    {
        var urls = new List<string>();
        MockOpenRouter.Handler = (request, _) =>
        {
            urls.Add(request.RequestUri!.PathAndQuery);
            var id = request.RequestUri.Query.Contains("transcription") ? "stt/model" : "chat/model";
            return (200, MockOpenRouter.Utf8("{\"data\":[{\"id\":\"" + id + "\",\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0\"}}]}"));
        };
        var catalog = await PriceCatalog.Fetch(new Uri("https://openrouter.ai/api/v1"), MockOpenRouter.Client());
        Assert.Equal(["/api/v1/models", "/api/v1/models?output_modalities=transcription"], urls);
        Assert.NotNull(catalog.Price("stt/model"));
        Assert.NotNull(catalog.Price("chat/model"));
    }

    [Fact]
    public void ModelPriceRoundTripsAndKeys()
    {
        var price = new ModelPrice(0.5, 1.5, 0.006);
        Assert.Equal(price, ModelPrice.FromJson(price.ToJson()));
        Assert.Null(ModelPrice.FromJson(new ModelPrice().ToJson()));
        Assert.Equal("custom|my-model", ModelPrice.Key(ProviderId.Custom, " my-model "));
    }

    [Fact]
    public void LedgerTotalsByDayAndMonth()
    {
        var ledger = new UsageLedger();
        var today = new DateOnly(2026, 9, 28);
        ledger.AddDictation(today, 120, 40);
        ledger.AddDictation(today, 80, 20);
        ledger.AddCost(today, 0.001, 0.002, 0);
        ledger.AddDictation(new DateOnly(2026, 9, 1), 500, 100);
        ledger.AddCost(new DateOnly(2026, 9, 1), 0, 0, 2);
        ledger.AddDictation(new DateOnly(2026, 8, 31), 1000, 300);
        ledger.AddCost(new DateOnly(2026, 8, 31), 0, 0, 0); // nothing to record

        var day = ledger.Today(today);
        Assert.Equal((200, 2, 60.0), (day.Words, day.Dictations, day.SpeakingSeconds));
        Assert.Equal(0.003, day.Cost, 9);
        var month = ledger.Month(today);
        Assert.Equal((700, 3, 2), (month.Words, month.Dictations, month.Unpriced));
        Assert.Equal(1700, ledger.Totals().Words);
        Assert.Equal(3, ledger.Days.Count);

        var restored = UsageLedger.FromJson(ledger.ToJson());
        Assert.Equal(ledger.Totals(), restored.Totals());
        Assert.True(UsageLedger.FromJson("{broken").IsEmpty);
    }

    [Fact]
    public void LedgerSavesAndLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), "usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null(UsageLedger.Load(path));
            var ledger = new UsageLedger();
            ledger.AddDictation(new DateOnly(2026, 1, 2), 3, 1.5);
            ledger.Save(path);
            Assert.Equal(3, UsageLedger.Load(path)!.Totals().Words);
            Assert.Contains("\"2026-01-02\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Streaks()
    {
        var ledger = new UsageLedger();
        var today = new DateOnly(2026, 9, 28);
        Assert.Equal((0, 0), ledger.Streaks(today));
        foreach (var offset in new[] { 1, 2, 3, 10, 11, 12, 13, 14 }) ledger.AddDictation(today.AddDays(-offset), 10, 5);
        // Today has nothing yet, so the run up to yesterday still counts.
        Assert.Equal((3, 5), ledger.Streaks(today));
        ledger.AddDictation(today, 1, 1);
        Assert.Equal((4, 5), ledger.Streaks(today));
        Assert.Equal((0, 5), ledger.Streaks(today.AddDays(2)));
    }

    [Fact]
    public void HeatmapEndsWithThisWeekAndShadesByQuartile()
    {
        var ledger = new UsageLedger();
        var today = new DateOnly(2026, 9, 30); // a Wednesday
        ledger.AddDictation(today, 400, 10);
        ledger.AddDictation(today.AddDays(-1), 100, 10);
        ledger.AddDictation(today.AddDays(-2), 200, 10);
        ledger.AddDictation(today.AddDays(-3), 300, 10);
        ledger.AddDictation(today.AddDays(-100), 999, 10); // outside the grid

        var sundayFirst = ledger.Heatmap(today, 4, firstWeekday: 0);
        Assert.Equal(4, sundayFirst.Count);
        Assert.All(sundayFirst, week => Assert.Equal(7, week.Length));
        Assert.Equal(DayOfWeek.Sunday, sundayFirst[0][0].Date.DayOfWeek);
        var last = sundayFirst[^1];
        Assert.Equal(new DateOnly(2026, 9, 27), last[0].Date);
        Assert.Equal(today, last[3].Date);
        Assert.False(last[3].IsFuture);
        Assert.True(last[4].IsFuture);
        Assert.Equal([3, 2, 1, 4], last[0..4].Select(c => c.Level)); // 300, 200, 100, 400 words
        Assert.Equal(0, sundayFirst[0][0].Level);
        Assert.Equal((400, 1), (last[3].Words, last[3].Dictations)); // what hovering a day shows
        Assert.Equal(0, last[4].Dictations);

        var mondayFirst = ledger.Heatmap(today, 1, firstWeekday: 1);
        Assert.Equal(new DateOnly(2026, 9, 28), mondayFirst[0][0].Date);
        Assert.Equal(new DateOnly(2026, 10, 4), mondayFirst[0][6].Date);
    }

    [Fact]
    public void HeatmapLevels()
    {
        Assert.Equal([0, 0, 0], UsageLedger.Thresholds([0, 0]));
        var thresholds = UsageLedger.Thresholds([0, 10, 20, 30, 40, 50]);
        Assert.Equal([20, 30, 40], thresholds);
        Assert.Equal(0, UsageLedger.Level(0, thresholds));
        Assert.Equal(1, UsageLedger.Level(5, thresholds));
        Assert.Equal(4, UsageLedger.Level(41, thresholds));
        Assert.Equal(1, UsageLedger.Level(7, UsageLedger.Thresholds([7]))); // a single busy day is level 1…
    }
}

public class UsageFormatTests
{
    [Fact]
    public void Money()
    {
        Assert.Equal("$0", UsageFormat.Money(0));
        Assert.Equal("< $0.0001", UsageFormat.Money(0.00004));
        Assert.Equal("$0.0042", UsageFormat.Money(0.0042));
        Assert.Equal("$0.034", UsageFormat.Money(0.0341));
        Assert.Equal("$1.20", UsageFormat.Money(1.2));
        Assert.Equal("$1,234.50", UsageFormat.Money(1234.5));
        Assert.Equal("$0.75", UsageFormat.Rate(0.75));
        Assert.Equal("$0.006", UsageFormat.Rate(0.006));
        Assert.Equal("$15", UsageFormat.Rate(15));
        Assert.Equal("12,345", UsageFormat.Count(12345));
    }

    [Fact]
    public void TranscriptionsSumAndCountUnpriced()
    {
        RequestUsage[] usages = [new(AudioSeconds: 30, Cost: 0.001), new(AudioSeconds: 30), new(AudioSeconds: 60, Cost: 0.002)];
        var (cost, unpriced) = CostEstimator.Transcriptions(usages, null, preferReported: true);
        Assert.Equal(0.003, cost, 9);
        Assert.Equal(1, unpriced);
        var (perMinute, none) = CostEstimator.Transcriptions(usages, new ModelPrice(PerMinute: 0.006), preferReported: false);
        Assert.Equal(0.012, perMinute, 9);
        Assert.Equal(0, none);
    }
}

public class CancelPolicyTests
{
    [Fact]
    public void KeepsLongRecordingsForADay()
    {
        Assert.False(CancelPolicy.Keeps(9.9));
        Assert.True(CancelPolicy.Keeps(10));
        Assert.True(CancelPolicy.Keeps(125));
        var at = new DateTimeOffset(2026, 9, 28, 19, 14, 0, TimeSpan.Zero);
        Assert.Equal(at.AddDays(1), CancelPolicy.Expiry(at));
        Assert.False(CancelPolicy.IsExpired(at, at.AddHours(23.9)));
        Assert.True(CancelPolicy.IsExpired(at, at.AddHours(24)));
    }
}

[Collection("MockServer")]
public class ConnectionCheckTests
{
    [Fact]
    public async Task TimesSeveralRoundTrips()
    {
        var calls = 0;
        MockOpenRouter.Handler = (request, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.EndsWith("/key", request.RequestUri!.AbsolutePath);
            return (200, MockOpenRouter.Utf8("""{"data":{}}"""));
        };
        var check = await new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client()).CheckConnection();
        Assert.Equal(3, calls);
        Assert.StartsWith(L("Key 有效", "Key is valid"), check.Summary);
        Assert.EndsWith(" ms", check.Summary);
        Assert.True(check.Milliseconds >= 0);
    }

    [Fact]
    public async Task FailsOnABadKey()
    {
        MockOpenRouter.Handler = (_, _) => (401, MockOpenRouter.Utf8("""{"error":{"message":"No auth credentials found"}}"""));
        var error = await Assert.ThrowsAsync<ApiException>(() => new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client()).CheckConnection());
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public void MedianAndSpeed()
    {
        Assert.Equal(120, ConnectionCheck.Median([900, 120, 100]));
        Assert.Equal(150, ConnectionCheck.Median([100, 200]));
        Assert.Equal(0, ConnectionCheck.Median([]));
        Assert.Equal(ConnectionCheck.SpeedRating.Fast, new ConnectionCheck("", 299).Speed);
        Assert.Equal(ConnectionCheck.SpeedRating.Fine, new ConnectionCheck("", 300).Speed);
        Assert.Equal(ConnectionCheck.SpeedRating.Slow, new ConnectionCheck("", 1000).Speed);
        Assert.Equal("ok · 183 ms", new ConnectionCheck("ok", 182.6).Summary);
    }
}
