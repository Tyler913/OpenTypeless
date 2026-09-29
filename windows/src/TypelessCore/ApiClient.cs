using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypelessCore;

/// <summary>Talks to OpenRouter or any OpenAI-compatible server for speech-to-text, chat and model lists.</summary>
public sealed partial class ApiClient
{
    public ProviderEndpoint Endpoint { get; }
    private readonly HttpClient _http;

    public ApiClient(ProviderEndpoint endpoint, HttpClient? http = null)
    {
        Endpoint = endpoint;
        _http = http ?? SharedHttp;
    }

    /// <summary>One client for the whole app so connections are reused across chunks.</summary>
    internal static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 6,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        // Hard caps are enforced per request by `Timeouts.WithTimeout` and idle timers.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    internal static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal bool IsOpenRouter => Endpoint.Id == ProviderId.OpenRouter;

    internal Uri Url(string path) => new(Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + path);

    internal HttpRequestMessage Request(string path, HttpContent? content = null)
    {
        var key = Endpoint.ApiKey.Trim();
        if (key.Length == 0 && Endpoint.Id.RequiresKey()) throw ApiException.MissingApiKey(Endpoint.Id.DisplayName());
        var request = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = content };
        if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (IsOpenRouter)
        {
            // OpenRouter app attribution headers (optional).
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/Tyler913/OpenTypeless");
            request.Headers.TryAddWithoutValidation("X-Title", "OpenTypeless");
        }
        return request;
    }

    internal static HttpContent JsonContent(JsonNode body)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, BodyJson));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    public static string ErrorMessage(ReadOnlySpan<byte> data)
    {
        try
        {
            if (JsonNode.Parse(data.ToArray()) is JsonObject obj)
            {
                if (obj["error"] is JsonObject error)
                {
                    var message = error["message"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : "unknown error";
                    // OpenRouter nests the upstream provider's message in metadata.raw.
                    if (error["metadata"] is JsonObject metadata && metadata["raw"] is JsonValue r && r.TryGetValue<string>(out var raw))
                    {
                        message += " — " + (raw.Length > 300 ? raw[..300] : raw);
                    }
                    return message;
                }
                if (obj["error"] is JsonValue e && e.TryGetValue<string>(out var errorText)) return errorText;
                if (obj["message"] is JsonValue mv && mv.TryGetValue<string>(out var messageText)) return messageText;
                if (obj["detail"] is JsonValue dv && dv.TryGetValue<string>(out var detail)) return detail;
            }
        }
        catch (JsonException) { }
        return Utf8Prefix(data, 300) ?? "unknown error";
    }

    internal static string? Utf8Prefix(ReadOnlySpan<byte> data, int maxBytes)
    {
        var slice = data.Length > maxBytes ? data[..maxBytes] : data;
        try
        {
            return new UTF8Encoding(false, true).GetString(slice);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    internal static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;
}

// MARK: - Speech to text

public sealed record TranscriptionOptions(string Model, string? Language = null);

/// <summary>A transcript and what producing it used, when the server said.</summary>
public sealed record TranscriptionResult(string Text, RequestUsage? Usage);

public sealed partial class ApiClient
{
    /// <summary>One STT call for a single chunk (no retries; see <see cref="TranscribeWithRetry"/>).</summary>
    public async Task<string> Transcribe(byte[] wav, TranscriptionOptions options, double timeout, CancellationToken cancellationToken = default) =>
        (await TranscribeDetailed(wav, options, timeout, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary><see cref="Transcribe"/>, plus the <c>usage</c> the server reported (OpenRouter includes the cost).</summary>
    public async Task<TranscriptionResult> TranscribeDetailed(byte[] wav, TranscriptionOptions options, double timeout,
                                                              CancellationToken cancellationToken = default)
    {
        HttpRequestMessage BuildRequest()
        {
            switch (Endpoint.Id.SttFormat())
            {
                case SttFormat.OpenRouterJson:
                    var body = new JsonObject
                    {
                        ["model"] = options.Model,
                        ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" },
                    };
                    if (!string.IsNullOrEmpty(options.Language)) body["language"] = options.Language;
                    return Request("audio/transcriptions", JsonContent(body));
                default:
                    var form = MultipartForm.Transcription(wav, options);
                    var content = new ByteArrayContent(form.Body);
                    content.Headers.TryAddWithoutValidation("Content-Type", form.ContentType);
                    return Request("audio/transcriptions", content);
            }
        }

        var request = BuildRequest(); // throws a missing-key error before any network activity
        var (status, data) = await Timeouts.WithTimeout(timeout, async ct =>
        {
            using (request)
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            }
        }, cancellationToken).ConfigureAwait(false);

        if (!IsSuccess(status)) throw ApiException.Http((int)status, ErrorMessage(data));

        JsonObject? obj = null;
        try { obj = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
        if (obj is null)
        {
            // `response_format=text` servers reply with the bare transcript.
            var text = Utf8Prefix(data, int.MaxValue);
            if (text != null && !text.StartsWith('<')) return new TranscriptionResult(text, null);
            throw ApiException.BadResponse(Utf8Prefix(data, 200) ?? "");
        }
        if (obj.ContainsKey("error"))
        {
            // Some upstream failures arrive as 200 + error body.
            throw ApiException.Http(502, ErrorMessage(data));
        }
        if (obj["text"] is JsonValue value && value.TryGetValue<string>(out var transcript))
        {
            return new TranscriptionResult(transcript, RequestUsage.Parse(obj["usage"]));
        }
        throw ApiException.BadResponse("missing \"text\" field");
    }

    /// <summary>
    /// Per-attempt timeout scales with chunk length but stays under the ~60 s upstream limit for typical
    /// chunks; retries cover transient provider/network failures.
    /// </summary>
    public async Task<string> TranscribeWithRetry(short[] samples, TranscriptionOptions options, RetryPolicy? policy = null,
                                                  Action<int, ApiException>? onRetry = null, CancellationToken cancellationToken = default) =>
        (await TranscribeDetailedWithRetry(samples, options, policy, onRetry, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary><see cref="TranscribeWithRetry"/>, plus the usage of the attempt that succeeded (audio length filled in).</summary>
    public async Task<TranscriptionResult> TranscribeDetailedWithRetry(short[] samples, TranscriptionOptions options, RetryPolicy? policy = null,
                                                                       Action<int, ApiException>? onRetry = null,
                                                                       CancellationToken cancellationToken = default)
    {
        var wav = Wav.Encode(samples);
        var seconds = AudioFormat.Seconds(samples.Length);
        var timeout = Math.Max(30, Math.Min(90, seconds * 2 + 15));
        var result = await (policy ?? new RetryPolicy()).Run((_, ct) => TranscribeDetailed(wav, options, timeout, ct), onRetry, cancellationToken)
            .ConfigureAwait(false);
        return result with { Usage = (result.Usage ?? new RequestUsage(Estimated: true)).WithAudioSeconds(seconds) };
    }
}

public sealed class MultipartForm
{
    public string Boundary { get; }
    private readonly MemoryStream _body = new();

    public MultipartForm(string? boundary = null)
    {
        Boundary = boundary ?? "OpenTypeless-" + Guid.NewGuid().ToString().ToUpperInvariant();
    }

    public string ContentType => $"multipart/form-data; boundary={Boundary}";
    public byte[] Body => _body.ToArray();

    public void AddField(string name, string value) =>
        Write($"--{Boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");

    public void AddFile(string name, string filename, string mimeType, byte[] data)
    {
        Write($"--{Boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\nContent-Type: {mimeType}\r\n\r\n");
        _body.Write(data);
        Write("\r\n");
    }

    public void Finish() => Write($"--{Boundary}--\r\n");

    private void Write(string text) => _body.Write(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Only <c>file</c>, <c>model</c> and (optionally) <c>language</c>: some OpenAI-compatible servers reject any
    /// parameter they don't know, so nothing else is sent.
    /// </summary>
    internal static MultipartForm Transcription(byte[] wav, TranscriptionOptions options)
    {
        var form = new MultipartForm();
        form.AddFile("file", "audio.wav", "audio/wav", wav);
        form.AddField("model", options.Model);
        if (!string.IsNullOrEmpty(options.Language)) form.AddField("language", options.Language);
        form.Finish();
        return form;
    }
}

// MARK: - Models

public sealed record ModelInfo(string Id, string Name, IReadOnlyList<string> SupportedParameters,
                               bool ReasoningMandatory = false, IReadOnlyList<string>? ReasoningEfforts = null)
{
    public ModelInfo(string id, string name) : this(id, name, []) { }

    public IReadOnlyList<string> Efforts => ReasoningEfforts ?? [];

    /// <summary>Best-effort guess for OpenAI-compatible servers that don't label model types.</summary>
    public bool LooksLikeSpeechModel
    {
        get
        {
            var id = Id.ToLowerInvariant();
            return new[] { "whisper", "transcribe", "asr", "sensevoice", "stt", "speech", "paraformer", "telespeech" }.Any(id.Contains);
        }
    }
}

public sealed partial class ApiClient
{
    /// <summary>GET /models. On OpenRouter, <c>outputModality: "transcription"</c> lists STT models.</summary>
    public async Task<List<ModelInfo>> ListModels(string? outputModality = null, CancellationToken cancellationToken = default)
    {
        var url = Url("models");
        if (outputModality != null && IsOpenRouter) url = new Uri(url + "?output_modalities=" + Uri.EscapeDataString(outputModality));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var key = Endpoint.ApiKey.Trim();
        if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        var (status, data) = await Send(request, 20, cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status)) throw ApiException.Http((int)status, ErrorMessage(data));

        JsonArray list;
        try
        {
            list = (JsonNode.Parse(data) as JsonObject)?["data"] as JsonArray ?? throw ApiException.BadResponse("models list");
        }
        catch (JsonException)
        {
            throw ApiException.BadResponse("models list");
        }
        var models = new List<ModelInfo>();
        foreach (var node in list)
        {
            if (node is not JsonObject m || !TryString(m["id"], out var id)) continue;
            var reasoning = m["reasoning"] as JsonObject;
            models.Add(new ModelInfo(
                id,
                TryString(m["name"], out var name) ? name : id,
                Strings(m["supported_parameters"]),
                reasoning?["mandatory"] is JsonValue mandatory && mandatory.TryGetValue<bool>(out var b) && b,
                Strings(reasoning?["supported_efforts"])));
        }
        return models;
    }

    /// <summary>Verifies the key/URL work: OpenRouter's /models is public, so check its key endpoint instead.</summary>
    public async Task<string> VerifyCredentials(CancellationToken cancellationToken = default)
    {
        if (IsOpenRouter)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url("key"));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Endpoint.ApiKey.Trim());
            var (status, data) = await Send(request, 15, cancellationToken).ConfigureAwait(false);
            if (!IsSuccess(status)) throw ApiException.Http((int)status, ErrorMessage(data));
            return L("Key 有效", "Key is valid");
        }
        var models = await ListModels(cancellationToken: cancellationToken).ConfigureAwait(false);
        return L($"连接正常，{models.Count} 个模型可用", $"Connected — {models.Count} models available");
    }

    /// <summary>
    /// <see cref="VerifyCredentials"/> a few times in a row, timing each round trip. The first one also pays for
    /// setting up the connection, so the median is what a request costs once the app is running.
    /// </summary>
    public async Task<ConnectionCheck> CheckConnection(int attempts = 3, CancellationToken cancellationToken = default)
    {
        var message = "";
        var times = new List<double>();
        for (var i = 0; i < Math.Max(1, attempts); i++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            message = await VerifyCredentials(cancellationToken).ConfigureAwait(false);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        return new ConnectionCheck(message, ConnectionCheck.Median(times));
    }

    /// <summary>A plain request whose timeout behaves like a lost connection (retryable network error).</summary>
    private async Task<(HttpStatusCode, byte[])> Send(HttpRequestMessage request, double seconds, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            using var response = await _http.SendAsync(request, timer.Token).ConfigureAwait(false);
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(timer.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ApiException.Network(L("连接超时", "connection timed out"));
        }
    }

    internal static bool TryString(JsonNode? node, out string value)
    {
        if (node is JsonValue v && v.TryGetValue(out string? s) && s != null)
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    internal static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => TryString(n, out var s) ? s : null).OfType<string>().ToList() : [];
}

/// <summary>A working connection and how long a round trip to the provider takes.</summary>
public sealed record ConnectionCheck(string Message, double Milliseconds)
{
    public enum SpeedRating { Fast, Fine, Slow }

    /// <summary>Under 300 ms is fast, under a second fine, anything longer slow.</summary>
    public SpeedRating Speed => Milliseconds < 300 ? SpeedRating.Fast : Milliseconds < 1000 ? SpeedRating.Fine : SpeedRating.Slow;

    /// <summary>"Key is valid · 182 ms"</summary>
    public string Summary => Message + $" · {(int)Math.Round(Milliseconds, MidpointRounding.AwayFromZero)} ms";

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
