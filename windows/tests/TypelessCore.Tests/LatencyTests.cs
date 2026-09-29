using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>
/// What shortens the wait after the key is released: trimming silence, the speculative tail, warm connections.
/// Shares <see cref="MockOpenRouter"/> with the pipeline tests, so it runs in the same serialized collection.
/// </summary>
[Collection("MockServer")]
public class LatencyTests
{
    /// <summary>testdata/voice-activity-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "voice-activity-cases.json")))!.AsObject();

    /// <summary>The audio a shared case describes (see the file's comment).</summary>
    private static short[] Signal(JsonNode segments)
    {
        var samples = new List<short>();
        long state = 1;
        foreach (var segment in segments.AsArray())
        {
            var count = AudioFormat.SampleCount((double)segment!["seconds"]!);
            var amplitude = segment["amplitude"] is { } a ? (double)a : 0;
            for (var i = 0; i < count; i++)
            {
                switch ((string)segment["kind"]!)
                {
                    case "tone":
                        samples.Add((short)(amplitude * Math.Sin(i * 0.2)));
                        break;
                    case "noise":
                        state = (state * 1103515245 + 12345) % 2147483648;
                        samples.Add((short)(amplitude * (2.0 * state / 2147483648 - 1)));
                        break;
                    default:
                        samples.Add(0);
                        break;
                }
            }
        }
        return samples.ToArray();
    }

    [Fact]
    public void TrimmingMatchesSharedCases()
    {
        foreach (var item in Cases()["trim"]!.AsArray())
        {
            var (start, end) = VoiceActivity.SpeechBounds(Signal(item!["segments"]!));
            Assert.True(((int)item["start"]!, (int)item["end"]!) == (start, end), $"{item["name"]}: got {start}…{end}");
        }
    }

    [Fact]
    public void PausesMatchSharedCases()
    {
        foreach (var item in Cases()["pauses"]!.AsArray())
        {
            var name = (string)item!["name"]!;
            var tracker = new PauseTracker();
            var audio = Signal(item["segments"]!);
            // Delivered in uneven blocks, as a microphone does.
            for (var i = 0; i < audio.Length; i += 1234) tracker.Append(audio.AsSpan(i, Math.Min(1234, audio.Length - i)));
            Assert.Equal(audio.Length, tracker.SampleCount);
            Assert.True((int?)item["lastSpeechEnd"] == tracker.LastSpeechEnd(), $"{name}: last speech ended at {tracker.LastSpeechEnd()}");
            foreach (var check in item["speechAfter"]!.AsArray())
            {
                var actual = tracker.SpeechAfter((int)check!["sample"]!, (bool)check["includePartial"]!);
                Assert.True((bool)check["expected"]! == actual, $"{name}: speech after {check["sample"]} = {actual}");
            }
        }
    }

    [Fact]
    public void ForgettingCutAudioIgnoresItsSpeech()
    {
        var tracker = new PauseTracker();
        tracker.Append(Tone(1));
        tracker.Append(new short[AudioFormat.SampleCount(1)]);
        tracker.Forget(AudioFormat.SampleCount(1.2));
        Assert.Null(tracker.LastSpeechEnd());
        Assert.Equal(AudioFormat.SampleCount(2), tracker.SampleCount);
    }

    private static short[] Tone(double seconds) =>
        Enumerable.Range(0, AudioFormat.SampleCount(seconds)).Select(i => (short)(6000 * Math.Sin(i * 0.2))).ToArray();

    private static short[] Silence(double seconds) => new short[AudioFormat.SampleCount(seconds)];

    private static short[] Concat(params short[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Feeds audio in 0.1 s blocks, like the microphone.</summary>
    private static void Feed(TranscriptionPipeline pipeline, short[] audio)
    {
        var block = AudioFormat.SampleCount(0.1);
        for (var i = 0; i < audio.Length; i += block) pipeline.Append(audio.AsSpan(i, Math.Min(block, audio.Length - i)));
    }

    /// <summary>Answers each transcription with the number of samples it was sent, and counts the requests.</summary>
    private static List<int> AnswerWithSampleCounts(double delay = 0)
    {
        var sent = new List<int>();
        MockOpenRouter.Handler = (_, body) =>
        {
            var data = (string)JsonNode.Parse(body)!["input_audio"]!["data"]!;
            var samples = Wav.DecodeSamples(Convert.FromBase64String(data))!;
            lock (MockOpenRouter.Lock) sent.Add(samples.Length);
            return (200, MockOpenRouter.Utf8(new JsonObject { ["text"] = $"[{samples.Length}]" }.ToJsonString()));
        };
        MockOpenRouter.Delay = _ => delay;
        return sent;
    }

    private static ApiClient Client() => new(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client());

    [Fact]
    public async Task SilenceIsTrimmedBeforeSending()
    {
        var sent = AnswerWithSampleCounts();
        try
        {
            var audio = Concat(Silence(1), Tone(2), Silence(1));
            var pipeline = new TranscriptionPipeline(Client(), new TranscriptionOptions("m"), speculate: false);
            pipeline.Append(audio);
            await pipeline.Finish();
            var (start, end) = VoiceActivity.SpeechBounds(audio);
            Assert.Equal([end - start], sent);
            Assert.True(end - start < audio.Length - AudioFormat.SampleCount(1));
        }
        finally
        {
            MockOpenRouter.Delay = null;
        }
    }

    [Fact]
    public async Task SpeculativeTailAnswersWhenTheSpeakerPausedBeforeRelease()
    {
        // Every request takes 0.6 s; the speaker stops talking, waits, then lets go of the key.
        var sent = AnswerWithSampleCounts(delay: 0.6);
        try
        {
            var announced = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pipeline = new TranscriptionPipeline(Client(), new TranscriptionOptions("m"), onSpeculation: text => announced.TrySetResult(text));
            Feed(pipeline, Concat(Tone(3), Silence(0.6)));
            var speculative = await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var clock = Stopwatch.StartNew();
            var text = await pipeline.Finish();
            // Nothing was left to send: the text was there at once, not a request (0.6 s) later.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(0.4), $"took {clock.Elapsed.TotalSeconds:0.00} s");
            Assert.True(pipeline.TailWasSpeculative);
            Assert.Equal(speculative, text);
            Assert.Single(sent);
        }
        finally
        {
            MockOpenRouter.Delay = null;
        }
    }

    [Fact]
    public async Task ReleaseWhileTheSpeculationIsOutWaitsForIt()
    {
        var sent = AnswerWithSampleCounts(delay: 0.5);
        try
        {
            var pipeline = new TranscriptionPipeline(Client(), new TranscriptionOptions("m"));
            Feed(pipeline, Concat(Tone(3), Silence(0.4)));
            var text = await pipeline.Finish();
            Assert.True(pipeline.TailWasSpeculative);
            Assert.Single(sent);
            Assert.Equal($"[{sent[0]}]", text);
        }
        finally
        {
            MockOpenRouter.Delay = null;
        }
    }

    [Fact]
    public async Task SpeakingAgainDropsTheSpeculation()
    {
        var sent = AnswerWithSampleCounts(delay: 0.2);
        try
        {
            var announcements = new List<string?>();
            var pipeline = new TranscriptionPipeline(Client(), new TranscriptionOptions("m"),
                                                     onSpeculation: text => { lock (announcements) announcements.Add(text); });
            Feed(pipeline, Concat(Tone(2), Silence(0.5)));
            // Long enough for the speculation to come back and be announced.
            await Task.Delay(700);
            Feed(pipeline, Tone(1));
            var text = await pipeline.Finish();

            // The tail was sent again, with the words after the pause.
            Assert.False(pipeline.TailWasSpeculative);
            Assert.Equal(2, sent.Count);
            Assert.True(sent[1] > sent[0] + AudioFormat.SampleCount(0.9));
            Assert.Equal($"[{sent[1]}]", text);
            lock (announcements) Assert.Equal([$"[{sent[0]}]", null], announcements);
        }
        finally
        {
            MockOpenRouter.Delay = null;
        }
    }

    [Fact]
    public async Task SpeculationCoversEarlierChunks()
    {
        var sent = AnswerWithSampleCounts();
        var announced = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new TranscriptionPipeline(Client(), new TranscriptionOptions("m"),
                                                 onSpeculation: text => { if (text != null && text.Contains("][")) announced.TrySetResult(text); });
        // 34 s of speech is cut once (at a pause, 18–28 s in); the rest is the tail.
        var speech = Enumerable.Range(0, AudioFormat.SampleCount(34))
            .Select(i => AudioFormat.Seconds(i) % 7 < 0.2 ? (short)0 : (short)(6000 * Math.Sin(i * 0.2))).ToArray();
        Feed(pipeline, Concat(speech, Silence(0.5)));
        var speculative = await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(speculative, await pipeline.Finish());
        Assert.True(pipeline.TailWasSpeculative);
    }

    [Fact]
    public async Task NoSpeculationWithoutSpeech()
    {
        var sent = AnswerWithSampleCounts();
        var pipeline = new TranscriptionPipeline(Client(), new TranscriptionOptions("m"));
        Feed(pipeline, Silence(2));
        Assert.Equal("", await pipeline.Finish());
        Assert.Empty(sent);
        Assert.False(pipeline.TailWasSpeculative);
    }

    [Fact]
    public async Task CancelStopsTheSpeculation()
    {
        AnswerWithSampleCounts(delay: 5);
        try
        {
            var pipeline = new TranscriptionPipeline(Client(), new TranscriptionOptions("m"));
            Feed(pipeline, Concat(Tone(2), Silence(0.5)));
            pipeline.Cancel();
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<Exception>(() => pipeline.Finish());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            MockOpenRouter.Delay = null;
        }
    }

    [Fact]
    public async Task PreconnectWarmsEachHostOnceWithoutTheKey()
    {
        var requests = new List<(HttpMethod Method, string Url, bool HasKey)>();
        MockOpenRouter.Handler = (request, _) =>
        {
            lock (MockOpenRouter.Lock) requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Contains("Authorization")));
            return (404, []);
        };
        var now = 100.0;
        var preconnector = new Preconnector(minimumInterval: 20, clock: () => now);
        var http = MockOpenRouter.Client();
        var openRouter = new ApiClient(ProviderEndpoint.OpenRouter("k"), http);
        var groq = new ApiClient(new ProviderEndpoint(ProviderId.Groq, new Uri(ProviderId.Groq.DefaultBaseUrl()), "g"), http);

        // Speech-to-text and clean-up on the same host share one connection; a missing route is skipped.
        await Task.WhenAll(preconnector.Warm([openRouter, groq, openRouter, null]));
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal(HttpMethod.Head, r.Method));
        Assert.All(requests, r => Assert.False(r.HasKey));
        Assert.Contains(requests, r => r.Url == "https://openrouter.ai/api/v1");

        now += 5; // warmed moments ago
        Assert.Empty(preconnector.Warm([openRouter, groq]));
        now += 20;
        await Task.WhenAll(preconnector.Warm([openRouter]));
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task PreconnectNeverThrows()
    {
        MockOpenRouter.Handler = (_, _) => throw new HttpRequestException("no network");
        await new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client()).Preconnect();
    }

    [Fact]
    public void BackupCleanUpStartsInTheSlowTail() =>
        // gemini-3.1-flash-lite's first token: 0.41 s at the median, 0.49 s at p90 (eval/README.md).
        Assert.InRange(HedgedPolish.DefaultHedgeDelay, 0.5, 0.6);
}
