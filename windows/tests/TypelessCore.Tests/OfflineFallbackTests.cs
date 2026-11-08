using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>
/// On-device recognition standing in for the provider while the network is down: the shared decisions, how errors are
/// told apart, and the pipeline going over to it.
/// </summary>
public class OfflineFallbackTests
{
    /// <summary>testdata/offline-fallback-cases.json, shared with the macOS tests so both apps go over at the same moments.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "offline-fallback-cases.json")))!.AsObject();

    /// <summary>The error a case names.</summary>
    private static ApiException Error(string name)
    {
        if (Enum.TryParse<NetworkFailure>(name, ignoreCase: true, out var failure)) return ApiException.Network(name, offline: failure);
        if (name.StartsWith("http", StringComparison.Ordinal)) return ApiException.Http(int.Parse(name[4..]), "");
        return name switch
        {
            "network" => ApiException.Network("TLS"),
            "timeout" => ApiException.TimedOut(30),
            "badResponse" => ApiException.BadResponse(""),
            "cancelled" => ApiException.Cancelled(),
            "missingKey" => ApiException.MissingApiKey("OpenRouter"),
            _ => throw new ArgumentException("unknown error " + name),
        };
    }

    private static Dictionary<int, string> Texts(JsonNode? node) =>
        node!.AsObject().ToDictionary(pair => int.Parse(pair.Key), pair => (string)pair.Value!);

    private static List<int>? Indices(JsonNode? node) => node?.AsArray().Select(n => (int)n!).ToList();

    // MARK: Shared cases

    [Fact]
    public void SkipsTheProviderAsTheSharedCasesSay()
    {
        foreach (var node in Cases()["skipsProvider"]!.AsArray())
        {
            var skips = OfflineFallback.SkipsProvider((bool)node!["switched"]!, (bool)node["networkDown"]!, (bool)node["ready"]!);
            Assert.True(skips == (bool)node["skips"]!, node.ToJsonString());
        }
    }

    [Fact]
    public void TakesOverAsTheSharedCasesSay()
    {
        foreach (var node in Cases()["takesOver"]!.AsArray())
        {
            var takes = OfflineFallback.TakesOver(Error((string)node!["error"]!), retriesLeft: (bool)node["retriesLeft"]!,
                                                  networkDown: (bool)node["networkDown"]!, ready: (bool)node["ready"]!);
            Assert.True(takes == (bool)node["takesOver"]!, node.ToJsonString());
        }
    }

    [Fact]
    public void LocalHostsAsTheSharedCasesSay()
    {
        foreach (var node in Cases()["localHosts"]!.AsArray())
        {
            Assert.True(OfflineFallback.IsLocalHost((string)node!["host"]!) == (bool)node["local"]!, node.ToJsonString());
        }
    }

    [Fact]
    public void RetriesReuseAsTheSharedCasesSay()
    {
        foreach (var node in Cases()["reused"]!.AsArray())
        {
            var (texts, onDevice) = OfflineFallback.Reused(Texts(node!["texts"]), Indices(node["onDevice"]), (bool)node["usesProvider"]!);
            var description = node.ToJsonString();
            Assert.True(Texts(node["reusedTexts"]).OrderBy(t => t.Key).SequenceEqual(texts.OrderBy(t => t.Key)), description);
            var expected = Indices(node["reusedOnDevice"]);
            Assert.True(expected is null ? onDevice is null : onDevice is not null && expected.SequenceEqual(onDevice), description);
        }
    }

    // MARK: Errors

    [Fact]
    public void ConnectionErrorsTellTheNetworkApartFromTheProvider()
    {
        static NetworkFailure? Of(Exception error) => ApiException.From(error).Offline;
        Assert.Equal(NetworkFailure.NoConnection, Of(new HttpRequestException("x", new SocketException((int)SocketError.NetworkUnreachable))));
        Assert.Equal(NetworkFailure.NoConnection, Of(new HttpRequestException("x", new SocketException((int)SocketError.NetworkDown))));
        Assert.Equal(NetworkFailure.HostUnreachable, Of(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal(NetworkFailure.HostUnreachable, Of(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Equal(NetworkFailure.HostUnreachable, Of(new HttpRequestException(HttpRequestError.NameResolutionError, "no such host")));
        Assert.Equal(NetworkFailure.ConnectionLost, Of(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));
        Assert.Equal(NetworkFailure.ConnectionLost, Of(new HttpRequestException(HttpRequestError.ResponseEnded, "ended")));
        Assert.Null(Of(new HttpRequestException("x", new SocketException((int)SocketError.TimedOut))));
        Assert.Null(Of(new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS")));
        Assert.Null(Of(new TimeoutException()));
        Assert.Null(ApiException.Http(503, "").Offline);

        // Still a network error, retried and described as before.
        var offline = ApiException.From(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal(ApiErrorKind.Network, offline.Kind);
        Assert.True(offline.IsRetryable);
        Assert.False(offline.RefusesEveryRequest);
        Assert.StartsWith(L("网络错误：", "Network error: "), offline.Message);
    }

    [Fact]
    public async Task APolicyStopsEarlyWhenToldTo()
    {
        var policy = new RetryPolicy(MaxAttempts: 4, BaseDelay: 0.001, StopsEarly: failed => failed.Offline == NetworkFailure.NoConnection);
        var tries = 0;
        await Assert.ThrowsAsync<ApiException>(() => policy.Run<string>((_, _) =>
        {
            tries++;
            throw ApiException.Network("offline", offline: NetworkFailure.NoConnection);
        }));
        Assert.Equal(1, tries);
        // Anything else is retried as before.
        var others = 0;
        await Assert.ThrowsAsync<ApiException>(() => policy.Run<string>((_, _) =>
        {
            others++;
            throw ApiException.Network("lost", offline: NetworkFailure.ConnectionLost);
        }));
        Assert.Equal(4, others);
    }

    // MARK: Pipeline

    /// <summary>
    /// A provider that fails with a scripted error (by request number, from 1) and answers "cloud" otherwise, through the
    /// retry policy it's given, as <see cref="ApiClient"/> does.
    /// </summary>
    private sealed class ScriptedProvider(Func<int, ApiException?> failure) : ISpeechTranscriber
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        public Task<TranscriptionResult> TranscribeDetailedWithRetry(short[] samples, TranscriptionOptions options, RetryPolicy? policy = null,
                                                                     Action<int, ApiException>? onRetry = null,
                                                                     CancellationToken cancellationToken = default) =>
            (policy ?? new RetryPolicy()).Run((_, _) =>
            {
                var number = Interlocked.Increment(ref _requests);
                if (failure(number) is { } error) throw error;
                return Task.FromResult(new TranscriptionResult("cloud", new RequestUsage(AudioSeconds: AudioFormat.Seconds(samples.Length), Cost: 0.01)));
            }, onRetry, cancellationToken);
    }

    /// <summary>A model that answers "local".</summary>
    private sealed class LocalModel : ILocalSpeechModel
    {
        public int Recognitions;

        public string Recognize(float[] samples, string language)
        {
            Interlocked.Increment(ref Recognitions);
            return " local ";
        }

        public void Dispose() { }
    }

    private static TranscriptionPipeline Pipeline(ScriptedProvider provider, LocalModel model, Func<bool> networkDown, bool ready = true) =>
        new(provider, new TranscriptionOptions("m", "en"), policy: new RetryPolicy(MaxAttempts: 3, BaseDelay: 0.001), speculate: false,
            offline: new OfflineFallbackRoute(new OnDeviceTranscriber(() => model), OnDeviceTranscriber.Options("en"), () => ready, networkDown));

    private static short[] Tone(double seconds) => ChunkerTests.Signal(seconds, []);

    private static void Feed(TranscriptionPipeline pipeline, short[] audio)
    {
        var block = AudioFormat.SampleCount(0.1);
        for (var i = 0; i < audio.Length; i += block) pipeline.Append(audio.AsSpan(i, Math.Min(block, audio.Length - i)));
    }

    [Fact]
    public async Task WhenTheNetworkIsKnownDownNothingIsSent()
    {
        var provider = new ScriptedProvider(_ => null);
        var model = new LocalModel();
        var pipeline = Pipeline(provider, model, () => true);
        Feed(pipeline, Tone(63));
        var text = await pipeline.Finish();
        var chunks = pipeline.CompletedTranscripts();
        Assert.True(chunks.Count >= 3, $"{chunks.Count} chunks");
        Assert.All(chunks.Values, chunk => Assert.Equal("local", chunk));
        Assert.Equal(TranscriptJoiner.Join(chunks.OrderBy(c => c.Key).Select(c => c.Value)), text);
        Assert.Equal(0, provider.Requests);
        Assert.Equal(chunks.Keys.Order().ToList(), pipeline.OfflineChunks());
        // Free: nothing for the provider's price to count.
        Assert.Empty(pipeline.Usages());
        Assert.Equal((0.0, 0), CostEstimator.Transcriptions(pipeline.Usages(), new ModelPrice(PerMinute: 0.006), preferReported: false));
    }

    [Fact]
    public async Task ADictationGoesOverOnceTheConnectionIsGone()
    {
        // The first chunk is transcribed; then the connection is gone (the system hasn't said so yet), and the provider
        // isn't asked again.
        var gone = new StrongBox<bool>(false);
        var provider = new ScriptedProvider(_ => gone.Value ? ApiException.Network("offline", offline: NetworkFailure.NoConnection) : null);
        var pipeline = Pipeline(provider, new LocalModel(), () => false);
        Feed(pipeline, Tone(29));
        for (var i = 0; i < 1000 && pipeline.ChunkCounts().Answered < 1; i++) await Task.Delay(10);
        Assert.Equal(1, pipeline.ChunkCounts().Answered);
        gone.Value = true;
        // One more chunk is cut from this; it fails over before the last one is handed out.
        Feed(pipeline, Tone(34));
        for (var i = 0; i < 1000 && pipeline.ChunkCounts().Answered < 2; i++) await Task.Delay(10);
        await pipeline.Finish();
        var chunks = pipeline.CompletedTranscripts();
        Assert.Equal("cloud", chunks[0]);
        Assert.True(chunks.Count >= 3, $"{chunks.Count} chunks");
        Assert.All(chunks.Where(c => c.Key > 0), chunk => Assert.Equal("local", chunk.Value));
        // No retries: one request that failed, and none for the chunks after it.
        Assert.Equal(2, provider.Requests);
        Assert.Equal(chunks.Keys.Where(k => k > 0).Order().ToList(), pipeline.OfflineChunks());
        Assert.Single(pipeline.Usages());
    }

    [Fact]
    public async Task ADroppedConnectionIsRetriedBeforeGoingOver()
    {
        // Dropped once (a server turning an upload down does that too): the retry gets through.
        var flaky = new ScriptedProvider(n => n == 1 ? ApiException.Network("lost", offline: NetworkFailure.ConnectionLost) : null);
        var model = new LocalModel();
        var first = Pipeline(flaky, model, () => false);
        first.Append(Tone(3));
        Assert.Equal("cloud", await first.Finish());
        Assert.Empty(first.OfflineChunks());
        Assert.Equal(0, model.Recognitions);

        // Dropped every time: once the retries are used up, the model on this PC takes over.
        var lost = new ScriptedProvider(_ => ApiException.Network("lost", offline: NetworkFailure.ConnectionLost));
        var second = Pipeline(lost, new LocalModel(), () => false);
        second.Append(Tone(3));
        Assert.Equal("local", await second.Finish());
        Assert.Equal(3, lost.Requests);
        Assert.Equal([0], second.OfflineChunks());
    }

    [Fact]
    public async Task TheProvidersOwnFailuresKeepTheirBehaviour()
    {
        var refusing = new ScriptedProvider(_ => ApiException.Http(500, "boom"));
        var model = new LocalModel();
        var pipeline = Pipeline(refusing, model, () => false);
        pipeline.Append(Tone(3));
        await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        // Retried, then once more in the last round, and never recognised on this PC.
        Assert.Equal(6, refusing.Requests);
        Assert.Equal(0, model.Recognitions);
    }

    [Fact]
    public async Task WithoutTheModelNothingChanges()
    {
        var offline = new ScriptedProvider(_ => ApiException.Network("offline", offline: NetworkFailure.NoConnection));
        var model = new LocalModel();
        var pipeline = Pipeline(offline, model, () => true, ready: false);
        pipeline.Append(Tone(3));
        await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        // The usual retries and last round, as without the setting.
        Assert.Equal(6, offline.Requests);
        Assert.Equal(0, model.Recognitions);
        Assert.Empty(pipeline.OfflineChunks());
    }

    [Fact]
    public async Task WithoutTheSettingNothingChanges()
    {
        var offline = new ScriptedProvider(_ => ApiException.Network("offline", offline: NetworkFailure.NoConnection));
        var pipeline = new TranscriptionPipeline(offline, new TranscriptionOptions("m"), policy: new RetryPolicy(MaxAttempts: 3, BaseDelay: 0.001),
                                                 speculate: false);
        pipeline.Append(Tone(3));
        await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        Assert.Equal(6, offline.Requests);
    }
}
