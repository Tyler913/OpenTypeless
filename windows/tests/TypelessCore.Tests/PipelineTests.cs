using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>
/// Fake OpenRouter: answers each STT request with the chunk's sample count so ordering can be checked,
/// and injects failures on demand.
/// </summary>
public sealed class MockOpenRouter : HttpMessageHandler
{
    public static Func<HttpRequestMessage, byte[], (int Status, byte[] Body)>? Handler;
    /// <summary>Seconds to hold a response back, without blocking a thread, so other requests aren't held up.</summary>
    public static Func<byte[], double>? Delay;
    public static readonly object Lock = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var (status, data) = Handler!(request, body);
        var wait = Delay?.Invoke(body) ?? 0;
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(data), RequestMessage = request };
    }

    public static HttpClient Client() => new(new MockOpenRouter()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}

[CollectionDefinition("MockServer", DisableParallelization = true)]
public class MockServerCollection { }

[Collection("MockServer")]
public class PipelineTests
{
    private static short[] Speech(double seconds)
    {
        var n = AudioFormat.SampleCount(seconds);
        var output = new short[n];
        for (var i = 0; i < n; i++)
        {
            // Loud tone with a short pause every 7 seconds.
            var t = AudioFormat.Seconds(i);
            output[i] = t % 7 < 0.4 ? (short)0 : (short)(6000 * Math.Sin(i * 0.2));
        }
        return output;
    }

    private static (string Data, short[] Samples) Audio(byte[] body)
    {
        var json = JsonNode.Parse(body)!;
        var data = (string)json["input_audio"]!["data"]!;
        return (data, Wav.DecodeSamples(Convert.FromBase64String(data))!);
    }

    [Fact]
    public async Task LongRecordingSurvivesTransientFailures()
    {
        var attemptsPerChunk = new Dictionary<string, int>();
        MockOpenRouter.Handler = (_, body) =>
        {
            var (key, samples) = Audio(body);
            int attempt;
            lock (MockOpenRouter.Lock)
            {
                attemptsPerChunk[key] = attemptsPerChunk.GetValueOrDefault(key) + 1;
                attempt = attemptsPerChunk[key];
            }
            // Every chunk fails with 503 on its first attempt.
            if (attempt == 1) return (503, MockOpenRouter.Utf8("""{"error":{"message":"upstream timeout"}}"""));
            return (200, MockOpenRouter.Utf8(new JsonObject { ["text"] = $"[{samples.Length}]" }.ToJsonString()));
        };

        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        // Counts every sample and every request, so without trimming and speculative tails (tested on their own).
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), policy: new RetryPolicy(3, 0.01),
                                                 trimSilence: false, speculate: false);
        var audio = Speech(130);
        var block = AudioFormat.SampleCount(0.1);
        for (var i = 0; i < audio.Length; i += block)
        {
            pipeline.Append(audio.AsSpan(i, Math.Min(block, audio.Length - i)));
        }
        var text = await pipeline.Finish();

        // Every chunk's sample count shows up, in order, and they add up to the whole recording.
        var counts = text.Split(']', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s.TrimStart('[', ' '))).ToList();
        Assert.True(counts.Count >= 5);
        Assert.Equal(audio.Length, counts.Sum());
        Assert.All(attemptsPerChunk.Values, v => Assert.Equal(2, v));
    }

    [Fact]
    public async Task ProgressCountsChunksOnceTheRecordingHasEnded()
    {
        MockOpenRouter.Handler = (_, _) => (200, MockOpenRouter.Utf8("""{"text":"好"}"""));
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), policy: new RetryPolicy(2, 0.01));
        pipeline.Append(Speech(40));
        // The tail isn't handed out yet, so the total isn't known.
        Assert.Null(pipeline.TranscriptionProgress());
        await pipeline.Finish();
        var progress = pipeline.TranscriptionProgress();
        Assert.NotNull(progress);
        Assert.True(progress.Value.Total > 1);
        Assert.Equal(progress.Value.Total, progress.Value.Done);
    }

    [Fact]
    public async Task PermanentFailureKeepsPartialText()
    {
        var audio = Speech(40);
        var reference = new Chunker();
        var cut = reference.Append(audio);
        var firstChunkSize = cut.First().Samples.Length;
        var chunkCount = cut.Count + (reference.Finish() == null ? 0 : 1);
        MockOpenRouter.Handler = (_, body) =>
        {
            var (_, samples) = Audio(body);
            if (samples.Length == firstChunkSize) return (200, MockOpenRouter.Utf8("""{"text":"第一段"}"""));
            return (500, MockOpenRouter.Utf8("""{"error":{"message":"down"}}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        // Recognises the first chunk by its length, so it's sent untrimmed.
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), policy: new RetryPolicy(2, 0.01), trimSilence: false);
        pipeline.Append(audio);
        var failure = await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        Assert.Equal("第一段", failure.PartialText);
        Assert.True(chunkCount >= 2);
        Assert.Equal(Enumerable.Range(1, chunkCount - 1), failure.FailedChunks);
        Assert.Equal(new Dictionary<int, string> { [0] = "第一段" }, pipeline.CompletedTranscripts());
    }

    [Fact]
    public async Task AuthErrorIsNotRetried()
    {
        var calls = 0;
        MockOpenRouter.Handler = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return (401, MockOpenRouter.Utf8("""{"error":{"message":"No auth credentials found"}}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("bad"), MockOpenRouter.Client());
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), policy: new RetryPolicy(4, 0.01));
        pipeline.Append(Speech(5));
        await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        // Permanent errors are neither retried nor re-sent in the final round.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SilenceIsSkippedWithoutRequests()
    {
        var calls = 0;
        MockOpenRouter.Handler = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return (200, MockOpenRouter.Utf8("""{"text":"Thanks for watching!"}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client());
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"));
        pipeline.Append(Enumerable.Repeat((short)3, AudioFormat.SampleCount(4)).ToArray());
        Assert.Equal("", await pipeline.Finish());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RetryReusesSucceededChunks()
    {
        // The history "Re-transcribe" path: chunk 0 already has a transcript and must not be re-sent.
        var audio = Speech(40);
        var sent = new List<int>();
        MockOpenRouter.Handler = (_, body) =>
        {
            var (_, samples) = Audio(body);
            lock (MockOpenRouter.Lock) sent.Add(samples.Length);
            return (200, MockOpenRouter.Utf8("""{"text":"第二段"}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client());
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), preset: new Dictionary<int, string> { [0] = "第一段" });
        pipeline.Append(audio);
        Assert.Equal("第一段第二段", await pipeline.Finish());
        Assert.Single(sent);
    }

    [Fact]
    public async Task PolishStreamsAndSanitizes()
    {
        MockOpenRouter.Handler = (request, body) =>
        {
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath);
            var json = JsonNode.Parse(body)!;
            Assert.True((bool)json["stream"]!);
            const string sse = """
                : OPENROUTER PROCESSING

                data: {"choices":[{"delta":{"content":"我想做一个"}}]}

                data: {"choices":[{"delta":{"content":"语音输入软件。"},"finish_reason":"stop"}]}

                data: [DONE]


                """;
            return (200, MockOpenRouter.Utf8(sse));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client());
        var result = await client.Polish("嗯我想做一个呃语音输入软件", new PolishOptions("m"));
        Assert.Equal("我想做一个语音输入软件。", result.Text);
        Assert.False(result.Truncated);
    }

    private static ProviderEndpoint Endpoint(ProviderId id, string key = "k") =>
        new(id, new Uri(id == ProviderId.Custom ? "http://localhost:9000/v1" : id.DefaultBaseUrl()), key);

    [Fact]
    public async Task MultipartTranscriptionForOpenAICompatible()
    {
        MockOpenRouter.Handler = (request, body) =>
        {
            Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.StartsWith("multipart/form-data; boundary=", request.Content!.Headers.GetValues("Content-Type").Single());
            Assert.Equal("Bearer gsk_test", request.Headers.GetValues("Authorization").Single());
            var text = Encoding.UTF8.GetString(body);
            Assert.Contains("""name="file"; filename="audio.wav" """.TrimEnd(), text);
            Assert.Contains("name=\"model\"\r\n\r\nwhisper-large-v3-turbo\r\n", text);
            Assert.Contains("name=\"language\"\r\n\r\nzh\r\n", text);
            Assert.DoesNotContain("input_audio", text);
            return (200, MockOpenRouter.Utf8("""{"text":"你好"}"""));
        };
        var client = new ApiClient(Endpoint(ProviderId.Groq, "gsk_test"), MockOpenRouter.Client());
        var text = await client.Transcribe(Wav.Encode(new short[] { 1, 2, 3 }), new TranscriptionOptions("whisper-large-v3-turbo", "zh"), 5);
        Assert.Equal("你好", text);
    }

    [Fact]
    public async Task CustomEndpointWorksWithoutKey()
    {
        MockOpenRouter.Handler = (request, _) =>
        {
            Assert.False(request.Headers.Contains("Authorization"));
            return (200, MockOpenRouter.Utf8("""{"text":"local"}"""));
        };
        var client = new ApiClient(Endpoint(ProviderId.Custom, ""), MockOpenRouter.Client());
        Assert.Equal("local", await client.Transcribe(Wav.Encode(new short[] { 1 }), new TranscriptionOptions("w"), 5));
    }

    [Fact]
    public async Task MissingKeyIsReported()
    {
        var client = new ApiClient(Endpoint(ProviderId.OpenAI, " "), MockOpenRouter.Client());
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Transcribe([], new TranscriptionOptions("m"), 5));
        Assert.Equal(ApiException.MissingApiKey("OpenAI"), error);
    }

    [Fact]
    public void PolishBodyAdaptsToProvider()
    {
        var options = new PolishOptions("m");
        var openRouter = new ApiClient(Endpoint(ProviderId.OpenRouter)).PolishBody("x", options, true);
        Assert.True(openRouter.ContainsKey("provider") && openRouter.ContainsKey("temperature") && openRouter.ContainsKey("max_tokens"));
        // Clean-up waits on the first token: lowest latency first, slow streamers (under 50 tok/s) last.
        Assert.Equal("latency", (string?)openRouter["provider"]!["sort"]);
        Assert.Equal(50, (int)openRouter["provider"]!["preferred_min_throughput"]!["p50"]!);
        var openAI = new ApiClient(Endpoint(ProviderId.OpenAI)).PolishBody("x", options, true);
        Assert.False(openAI.ContainsKey("temperature") || openAI.ContainsKey("provider") || openAI.ContainsKey("reasoning"));
        var deepSeek = new ApiClient(Endpoint(ProviderId.DeepSeek)).PolishBody("x", options, true);
        Assert.Equal(0.2, (double)deepSeek["temperature"]!);
        Assert.False(deepSeek.ContainsKey("provider"));
        var bare = new ApiClient(Endpoint(ProviderId.OpenRouter)).PolishBody("x", options, false);
        Assert.Equal(new[] { "messages", "model", "stream" }, bare.Select(p => p.Key).Order());
    }

    [Fact]
    public void PolishAsksTheChosenHostFirst()
    {
        var client = new ApiClient(Endpoint(ProviderId.OpenRouter));
        var automatic = client.PolishBody("x", new PolishOptions("m"), true)["provider"]!.AsObject();
        Assert.Equal("latency", (string?)automatic["sort"]);
        Assert.False(automatic.ContainsKey("order"));
        var pinned = client.PolishBody("x", new PolishOptions("m", Host: "groq"), true)["provider"]!.AsObject();
        // The chosen host goes first, and the others stay behind it in case it's down.
        Assert.Equal(new[] { "groq" }, pinned["order"]!.AsArray().Select(n => (string?)n));
        Assert.True((bool)pinned["allow_fallbacks"]!);
        Assert.Equal("latency", (string?)pinned["sort"]);
        Assert.False(client.PolishBody("x", new PolishOptions("m", Host: " "), true)["provider"]!.AsObject().ContainsKey("order"));
        // Only OpenRouter routes between hosts.
        Assert.False(new ApiClient(Endpoint(ProviderId.Groq)).PolishBody("x", new PolishOptions("m", Host: "groq"), true).ContainsKey("provider"));
    }

    [Fact]
    public void HostsAreParsedCheapestFirst()
    {
        var json = """
            {"data":{"id":"openai/gpt-oss-120b","endpoints":[
              {"provider_name":"Groq","tag":"groq","pricing":{"prompt":"0.00000015"},"uptime_last_30m":100},
              {"provider_name":"DeepInfra","tag":"deepinfra/bf16","pricing":{"prompt":"0.000000037"},"uptime_last_30m":96.5},
              {"provider_name":"DeepInfra","tag":"deepinfra/bf16","pricing":{"prompt":"0.000000037"}},
              {"provider_name":"Odd","pricing":{"prompt":"0"}},
              {"provider_name":"Cerebras","tag":"cerebras/fp16","pricing":{}}
            ]}}
            """;
        var hosts = ApiClient.ParseHosts(MockOpenRouter.Utf8(json));
        Assert.Equal(new[] { "deepinfra/bf16", "groq", "cerebras/fp16" }, hosts.Select(h => h.Tag));
        Assert.Equal(new ModelHost("deepinfra/bf16", "DeepInfra", 0.000000037, 96.5), hosts[0]);
        Assert.Null(hosts[2].InputPrice);
        Assert.Empty(ApiClient.ParseHosts(MockOpenRouter.Utf8("{}")));
        Assert.Empty(ApiClient.ParseHosts(MockOpenRouter.Utf8("not json")));
    }

    [Fact]
    public async Task ListHostsAsksForTheModelsEndpoints()
    {
        MockOpenRouter.Handler = (request, _) =>
        {
            Assert.EndsWith("/models/openai/gpt-oss-120b/endpoints", request.RequestUri!.AbsolutePath);
            return (200, MockOpenRouter.Utf8("""{"data":{"endpoints":[{"provider_name":"Groq","tag":"groq"}]}}"""));
        };
        var client = new ApiClient(Endpoint(ProviderId.OpenRouter), MockOpenRouter.Client());
        Assert.Equal(new[] { "groq" }, (await client.ListHosts("openai/gpt-oss-120b:nitro")).Select(h => h.Tag));
        Assert.Empty(await new ApiClient(Endpoint(ProviderId.Groq), MockOpenRouter.Client()).ListHosts("a/b"));
    }

    [Fact]
    public async Task PolishRetriesWithoutRejectedParameter()
    {
        var bodies = new List<JsonNode>();
        MockOpenRouter.Handler = (_, body) =>
        {
            var json = JsonNode.Parse(body)!;
            bodies.Add(json);
            if (json["temperature"] != null)
            {
                return (400, MockOpenRouter.Utf8("""{"error":{"message":"Unsupported value: 'temperature' does not support 0.2"}}"""));
            }
            return (200, MockOpenRouter.Utf8("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"));
        };
        var client = new ApiClient(Endpoint(ProviderId.DeepSeek), MockOpenRouter.Client());
        var result = await client.Polish("x", new PolishOptions("m"));
        Assert.Equal("ok", result.Text);
        Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public async Task PolishIdleTimeoutIsRetryable()
    {
        // A stream that goes silent must end with a retryable error, not hang until the 240 s guard.
        var client = new ApiClient(ProviderEndpoint.OpenRouter("k"), new HttpClient(new StallingHandler()));
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Polish("x", new PolishOptions("m"), idleTimeout: 0.2, totalTimeout: 5));
        Assert.Equal(ApiErrorKind.Network, error.Kind);
        Assert.True(error.IsRetryable);
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });

        private sealed class StallingStream : Stream
        {
            private bool _sentFirst;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_sentFirst)
                {
                    _sentFirst = true;
                    var first = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n");
                    first.CopyTo(buffer);
                    return first.Length;
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }
    }

    [Fact]
    public void Localization()
    {
        var original = TypelessCore.Localization.Preference;
        try
        {
            TypelessCore.Localization.Preference = () => "en";
            Assert.Equal("English", L("中文", "English"));
            TypelessCore.Localization.Preference = () => "zh";
            Assert.Equal("中文", L("中文", "English"));
        }
        finally
        {
            TypelessCore.Localization.Preference = original;
        }
    }
}

/// <summary>
/// Requests ask for HTTP/2, as URLSession does on macOS, so hedged and retried requests share one connection; a plain
/// http:// server still gets HTTP/1.1.
/// </summary>
[Collection("MockServer")]
public class HttpVersionTests
{
    [Fact]
    public async Task EveryProviderRequestAsksForHttp2()
    {
        var versions = new List<(string Path, Version Version, HttpVersionPolicy Policy)>();
        MockOpenRouter.Delay = null;
        MockOpenRouter.Handler = (request, _) =>
        {
            lock (MockOpenRouter.Lock) versions.Add((request.RequestUri!.AbsolutePath, request.Version, request.VersionPolicy));
            return (200, MockOpenRouter.Utf8("""{"text":"ok","data":[{"id":"m","pricing":{"prompt":"0.000001","completion":"0.000002"}}]}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("k"), MockOpenRouter.Client());
        await client.Transcribe([0], new TranscriptionOptions("m"), 10);
        await client.ListModels();
        await client.ListHosts("a/b");
        await client.VerifyCredentials();
        await client.Preconnect();
        await PriceCatalog.Fetch(http: MockOpenRouter.Client());
        Assert.Equal(7, versions.Count);
        Assert.All(versions, v =>
        {
            Assert.Equal(HttpVersion.Version20, v.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, v.Policy);
        });
    }

    [Fact]
    public async Task APlainHttpServerIsAskedOverHttp11()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await AnswerOnce(listener);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AnswerOnce(TcpListener listener)
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            var received = new List<byte>();
            var buffer = new byte[65536];
            int headerEnd;
            while ((headerEnd = IndexOf(received, "\r\n\r\n"u8.ToArray())) < 0)
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0) throw new IOException("closed before the headers ended");
                received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
            var headers = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
            var length = headers.Split("\r\n").Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.Parse(parts[1].Trim())).FirstOrDefault();
            // The whole body before answering, so the client isn't cut off mid-upload.
            while (received.Count < headerEnd + 4 + length)
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
            const string body = """{"text":"local"}""";
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            return headers.Split("\r\n")[0];
        });
        // The app's own HTTP stack, against a local server without TLS (e.g. a self-hosted Whisper).
        var client = new ApiClient(new ProviderEndpoint(ProviderId.Custom, new Uri($"http://127.0.0.1:{port}/v1"), ""));
        Assert.Equal("local", await client.Transcribe(Wav.Encode([1, 2, 3]), new TranscriptionOptions("w"), 10));
        Assert.Equal("POST /v1/audio/transcriptions HTTP/1.1", await served.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static int IndexOf(List<byte> data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }
}
