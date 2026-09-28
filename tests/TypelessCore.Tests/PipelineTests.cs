using System.Net;
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
    public static readonly object Lock = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var (status, data) = Handler!(request, body);
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
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), policy: new RetryPolicy(3, 0.01));
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
    public async Task PermanentFailureKeepsPartialText()
    {
        var audio = Speech(40);
        var firstChunkSize = new Chunker().Append(audio).First().Samples.Length;
        MockOpenRouter.Handler = (_, body) =>
        {
            var (_, samples) = Audio(body);
            if (samples.Length == firstChunkSize) return (200, MockOpenRouter.Utf8("""{"text":"第一段"}"""));
            return (500, MockOpenRouter.Utf8("""{"error":{"message":"down"}}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), policy: new RetryPolicy(2, 0.01));
        pipeline.Append(audio);
        var failure = await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        Assert.Equal("第一段", failure.PartialText);
        Assert.Equal([1], failure.FailedChunks);
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
        var openAI = new ApiClient(Endpoint(ProviderId.OpenAI)).PolishBody("x", options, true);
        Assert.False(openAI.ContainsKey("temperature") || openAI.ContainsKey("provider") || openAI.ContainsKey("reasoning"));
        var deepSeek = new ApiClient(Endpoint(ProviderId.DeepSeek)).PolishBody("x", options, true);
        Assert.Equal(0.2, (double)deepSeek["temperature"]!);
        Assert.False(deepSeek.ContainsKey("provider"));
        var bare = new ApiClient(Endpoint(ProviderId.OpenRouter)).PolishBody("x", options, false);
        Assert.Equal(new[] { "messages", "model", "stream" }, bare.Select(p => p.Key).Order());
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
