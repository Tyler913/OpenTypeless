using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypelessCore;

/// <summary>Talks to OpenRouter or any OpenAI-compatible server for speech-to-text, chat and model lists.</summary>
public sealed partial class ApiClient : ISpeechTranscriber
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

    /// <summary>
    /// A client for a file being transcribed, with connections of its own, fewer per host (see
    /// <see cref="FileTranscription.MaxConnectionsPerHost"/>): a dictation's requests never wait behind a file's.
    /// </summary>
    public static ApiClient ForFiles(ProviderEndpoint endpoint) => new(endpoint, FileHttp);

    internal static readonly HttpClient FileHttp = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = FileTranscription.MaxConnectionsPerHost,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    internal static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal bool IsOpenRouter => Endpoint.Id == ProviderId.OpenRouter;

    internal Uri Url(string path) => new(Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + path);

    /// <summary>
    /// Every request to a provider: HTTP/2 where the server speaks it, as URLSession does on macOS, so a chunk's hedged
    /// pair, a cancelled request and its retry, and the speech-to-text and clean-up requests to one host share a single
    /// multiplexed connection instead of each opening its own (an HttpRequestMessage defaults to HTTP/1.1). A plain
    /// http:// server, such as one running locally, gets HTTP/1.1.
    /// </summary>
    internal static HttpRequestMessage NewRequest(HttpMethod method, Uri url) => new(method, url)
    {
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    /// <param name="query">Parameters for the URL, in order (a name may repeat).</param>
    internal HttpRequestMessage Request(string path, HttpContent? content = null, IReadOnlyList<(string Name, string Value)>? query = null)
    {
        var key = Endpoint.ApiKey.Trim();
        if (key.Length == 0 && Endpoint.Id.RequiresKey()) throw ApiException.MissingApiKey(Endpoint.Id.DisplayName());
        var url = Url(path);
        if (query is { Count: > 0 })
        {
            url = new Uri(url.AbsoluteUri + "?" + string.Join("&", query.Select(q => Uri.EscapeDataString(q.Name) + "=" + Uri.EscapeDataString(q.Value))));
        }
        var request = NewRequest(HttpMethod.Post, url);
        request.Content = content;
        if (key.Length > 0)
        {
            var (field, value) = Endpoint.Id.Authorization(key);
            request.Headers.TryAddWithoutValidation(field, value);
        }
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
                // Deepgram.
                if (TryString(obj["err_msg"], out var deepgram)) return deepgram;
                if (obj["detail"] is JsonValue dv && dv.TryGetValue<string>(out var detail)) return detail;
                // ElevenLabs: {"detail": {"status": "invalid_api_key", "message": "…"}}.
                if (obj["detail"] is JsonObject detailObject && TryString(detailObject["message"], out var detailMessage)) return detailMessage;
                // A validation error (ElevenLabs, Mistral): {"detail": [{"loc": ["body", "field"], "msg": "…"}]}, the field
                // named so a rejected optional parameter can be recognised.
                if (obj["detail"] is JsonArray { Count: > 0 } details)
                {
                    var parts = new List<string>();
                    foreach (var item in details)
                    {
                        if (item is not JsonObject entry || !TryString(entry["msg"], out var msg)) continue;
                        var location = entry["loc"] is JsonArray loc
                            ? string.Join(".", loc.Select(n => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : n?.ToJsonString() ?? ""))
                            : "";
                        parts.Add(location.Length == 0 ? msg : $"{location}: {msg}");
                    }
                    if (parts.Count > 0) return string.Join("; ", parts);
                }
            }
        }
        catch (JsonException) { }
        return Utf8Prefix(data, 300) ?? "unknown error";
    }

    /// <summary>
    /// The status a failed response is classified by: its own, except where the body says the account is out of credit
    /// under another status (ElevenLabs answers 401 <c>quota_exceeded</c>), which is a 402 so it reads as billing.
    /// </summary>
    internal static int HttpStatus(int status, ReadOnlySpan<byte> data)
    {
        if (status is not (401 or 403 or 429)) return status;
        try
        {
            if (JsonNode.Parse(data.ToArray()) is JsonObject obj && obj["detail"] is JsonObject detail
                && TryString(detail["status"], out var code) && code is "quota_exceeded" or "payment_required" or "insufficient_credits")
            {
                return 402;
            }
        }
        catch (JsonException) { }
        return status;
    }

    /// <summary>
    /// The HTTP status an error body carries in <c>error.code</c> (OpenRouter puts 401, 402, 429… there, also inside a 200
    /// response or a stream), so the error is classified like that status and e.g. a 402 isn't retried. Null for string
    /// codes and anything else.
    /// </summary>
    internal static int? ErrorStatus(ReadOnlySpan<byte> data)
    {
        try
        {
            if (JsonNode.Parse(data.ToArray()) is JsonObject obj && obj["error"] is JsonObject error
                && error["code"] is JsonValue code && code.GetValueKind() == JsonValueKind.Number
                && code.TryGetValue<int>(out var status) && status is >= 400 and <= 599)
            {
                return status;
            }
        }
        catch (JsonException) { }
        return null;
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

/// <param name="Vocabulary">The user's vocabulary, passed to models that take spelling hints (see <see cref="VocabularyHints"/>).</param>
public sealed record TranscriptionOptions(string Model, string? Language = null, IReadOnlyList<string>? Vocabulary = null);

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
        try
        {
            return await TranscribeOnce(wav, options, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException error) when (error.Kind == ApiErrorKind.Http && error.Status is 400 or 422 && HasVocabularyHints(options))
        {
            // A provider that stopped taking the hints mustn't stop dictation: the same request without them. (A field that
            // fails validation is a 422 at ElevenLabs.)
            return await TranscribeOnce(wav, options with { Vocabulary = null }, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    internal bool HasVocabularyHints(TranscriptionOptions options) => Endpoint.Id.SttFormat() == SttFormat.OpenRouterJson
        ? VocabularyHints.OpenRouterProvider(options.Model, options.Vocabulary) != null
        : VocabularyHints.DirectHints(Endpoint.Id, options.Model, options.Vocabulary) != null;

    /// <summary>The transcription request for one chunk, with the vocabulary hints this model takes.</summary>
    internal HttpRequestMessage TranscriptionRequest(byte[] wav, TranscriptionOptions options)
    {
        var format = Endpoint.Id.SttFormat();
        var language = string.IsNullOrWhiteSpace(options.Language) ? null : options.Language.Trim();
        switch (format)
        {
            case SttFormat.OpenRouterJson:
            {
                var body = new JsonObject
                {
                    ["model"] = options.Model,
                    ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" },
                };
                if (language != null) body["language"] = language;
                if (VocabularyHints.OpenRouterProvider(options.Model, options.Vocabulary) is { } provider) body["provider"] = provider;
                return Request(format.RequestPath(), JsonContent(body));
            }
            case SttFormat.Deepgram:
            {
                // The audio is the body; everything else goes in the query. Punctuation and formatting as typed text has
                // them; the language when one is set, else detected (Deepgram assumes English).
                var query = new List<(string, string)> { ("model", options.Model), ("smart_format", "true"), ("punctuate", "true") };
                query.Add(language != null ? ("language", language) : ("detect_language", "true"));
                if (VocabularyHints.DirectHints(Endpoint.Id, options.Model, options.Vocabulary) is { Style: VocabularyHints.Style.Keyterm } hints)
                {
                    query.AddRange(hints.TermList.Select(t => ("keyterm", t)));
                }
                var audio = new ByteArrayContent(wav);
                audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                return Request(format.RequestPath(), audio, query);
            }
            default:
            {
                var hints = VocabularyHints.DirectHints(Endpoint.Id, options.Model, options.Vocabulary);
                var form = format == SttFormat.ElevenLabs
                    ? MultipartForm.Scribe(wav, options, hints)
                    : MultipartForm.Transcription(wav, options, hints);
                var content = new ByteArrayContent(form.Body);
                content.Headers.TryAddWithoutValidation("Content-Type", form.ContentType);
                return Request(format.RequestPath(), content);
            }
        }
    }

    private async Task<TranscriptionResult> TranscribeOnce(byte[] wav, TranscriptionOptions options, double timeout,
                                                           CancellationToken cancellationToken)
    {
        var request = TranscriptionRequest(wav, options); // throws a missing-key error before any network activity
        var (status, data) = await Timeouts.WithTimeout(timeout, async ct =>
        {
            using (request)
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
            {
                return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            }
        }, cancellationToken).ConfigureAwait(false);

        if (!IsSuccess(status)) throw ApiException.Http(HttpStatus((int)status, data), ErrorMessage(data));
        return ParseTranscription(data, Endpoint.Id.SttFormat());
    }

    /// <summary>
    /// A successful transcription response: <c>text</c> (and <c>usage</c>) for every format but Deepgram's, whose
    /// transcript is the first alternative of the first channel and whose length is <c>metadata.duration</c>.
    /// </summary>
    internal static TranscriptionResult ParseTranscription(byte[] data, SttFormat format)
    {
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { }
        if (obj is null)
        {
            // `response_format=text` servers reply with the bare transcript.
            var text = Utf8Prefix(data, int.MaxValue);
            if (format == SttFormat.Multipart && text != null && !text.StartsWith('<')) return new TranscriptionResult(text, null);
            throw ApiException.BadResponse(Utf8Prefix(data, 200) ?? "");
        }
        if (obj.ContainsKey("error") || obj.ContainsKey("err_code"))
        {
            // Some upstream failures arrive as 200 + error body.
            throw ApiException.Http(ErrorStatus(data) ?? 502, ErrorMessage(data));
        }
        if (format == SttFormat.Deepgram)
        {
            if (((obj["results"] as JsonObject)?["channels"] as JsonArray)?.FirstOrDefault() is JsonObject channel
                && channel["alternatives"] is JsonArray { Count: > 0 } alternatives && alternatives[0] is JsonObject first
                && TryString(first["transcript"], out var said))
            {
                var seconds = RequestUsage.Number((obj["metadata"] as JsonObject)?["duration"]);
                return new TranscriptionResult(said, seconds is { } length ? new RequestUsage(AudioSeconds: length) : null);
            }
            throw ApiException.BadResponse("missing \"results.channels[0].alternatives[0].transcript\"");
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
    /// Only <c>file</c>, <c>model</c>, and (optionally) <c>language</c> and the vocabulary hints, which go only where
    /// documented (see <see cref="VocabularyHints"/>): some OpenAI-compatible servers reject any parameter they don't know.
    /// </summary>
    internal static MultipartForm Transcription(byte[] wav, TranscriptionOptions options, string? prompt = null) =>
        Transcription(wav, options, string.IsNullOrEmpty(prompt) ? null : new VocabularyHints.Direct(VocabularyHints.Style.Prompt, Prompt: prompt));

    internal static MultipartForm Transcription(byte[] wav, TranscriptionOptions options, VocabularyHints.Direct? hints)
    {
        var form = new MultipartForm();
        form.AddFile("file", "audio.wav", "audio/wav", wav);
        form.AddField("model", options.Model);
        if (!string.IsNullOrEmpty(options.Language)) form.AddField("language", options.Language);
        if (hints is { Style: VocabularyHints.Style.Prompt, Prompt: { Length: > 0 } prompt })
        {
            form.AddField("prompt", prompt);
        }
        else if (hints is { Style: VocabularyHints.Style.ContextBias })
        {
            // Mistral: the field once per term.
            foreach (var term in hints.TermList) form.AddField("context_bias", term);
        }
        form.Finish();
        return form;
    }

    /// <summary>
    /// ElevenLabs Scribe: <c>model_id</c> and <c>file</c>, the language when one is set (else it's detected), no
    /// sound-event tags such as "(laughter)" in the text, and the vocabulary as <c>keyterms</c>, a field per term.
    /// </summary>
    internal static MultipartForm Scribe(byte[] wav, TranscriptionOptions options, VocabularyHints.Direct? hints)
    {
        var form = new MultipartForm();
        form.AddField("model_id", options.Model);
        form.AddFile("file", "audio.wav", "audio/wav", wav);
        if (!string.IsNullOrEmpty(options.Language)) form.AddField("language_code", options.Language);
        form.AddField("tag_audio_events", "false");
        if (hints is { Style: VocabularyHints.Style.Keyterms })
        {
            foreach (var term in hints.TermList) form.AddField("keyterms", term);
        }
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
            return new[] { "whisper", "transcribe", "asr", "sensevoice", "stt", "speech", "paraformer", "telespeech", "voxtral" }.Any(id.Contains);
        }
    }
}

/// <summary>One company serving a model on OpenRouter.</summary>
/// <param name="Tag">What a request names it by in <c>provider.order</c>, e.g. "groq" or "deepinfra/fp8".</param>
/// <param name="InputPrice">Input price, USD per token (per second of audio for speech models).</param>
/// <param name="Uptime">Share of requests that succeeded over the last 30 minutes, 0–100.</param>
public sealed record ModelHost(string Tag, string Name, double? InputPrice = null, double? Uptime = null);

public sealed partial class ApiClient
{
    /// <summary>The hosts serving an OpenRouter model, cheapest first, from GET /models/{id}/endpoints.</summary>
    public async Task<List<ModelHost>> ListHosts(string model, CancellationToken cancellationToken = default)
    {
        // A variant suffix (":nitro", ":free") isn't part of the path.
        var id = model.Trim().Split(':')[0];
        if (!IsOpenRouter || !id.Contains('/')) return [];
        using var request = NewRequest(HttpMethod.Get, Url("models/" + id + "/endpoints"));
        var (status, data) = await Send(request, 20, cancellationToken).ConfigureAwait(false);
        // An ID OpenRouter doesn't know: nothing to choose from.
        if ((int)status == 404) return [];
        if (!IsSuccess(status)) throw ApiException.Http((int)status, ErrorMessage(data));
        return ParseHosts(data);
    }

    internal static List<ModelHost> ParseHosts(byte[] data)
    {
        JsonArray? rows;
        try
        {
            rows = ((JsonNode.Parse(data) as JsonObject)?["data"] as JsonObject)?["endpoints"] as JsonArray;
        }
        catch (JsonException)
        {
            return [];
        }
        if (rows is null) return [];
        var seen = new HashSet<string>();
        var hosts = new List<ModelHost>();
        foreach (var node in rows)
        {
            if (node is not JsonObject row || !TryString(row["tag"], out var tag) || tag.Length == 0 || !seen.Add(tag)) continue;
            double? price = TryString((row["pricing"] as JsonObject)?["prompt"], out var text)
                && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
            double? uptime = row["uptime_last_30m"] is JsonValue value && value.TryGetValue<double>(out var u) ? u : null;
            hosts.Add(new ModelHost(tag, TryString(row["provider_name"], out var name) ? name : tag, price, uptime));
        }
        return hosts.OrderBy(h => h.InputPrice ?? double.PositiveInfinity).ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.Tag, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// GET /models. On OpenRouter, <c>outputModality: "transcription"</c> lists STT models. Deepgram and ElevenLabs list no
    /// speech-to-text models this way: nothing to choose from (the model is typed in).
    /// </summary>
    public async Task<List<ModelInfo>> ListModels(string? outputModality = null, CancellationToken cancellationToken = default)
    {
        if (!Endpoint.Id.ListsModels()) return [];
        using var request = ModelListRequest(outputModality);
        return ParseModels(await Get(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The GET for the model list: OpenRouter's speech models by their output, every Claude model in one page.</summary>
    internal HttpRequestMessage ModelListRequest(string? outputModality = null)
    {
        var url = Url("models");
        if (outputModality != null && IsOpenRouter) url = new Uri(url + "?output_modalities=" + Uri.EscapeDataString(outputModality));
        else if (Endpoint.Id == ProviderId.Anthropic) url = new Uri(url + "?limit=1000");
        return GetRequest(url);
    }

    /// <summary>A GET with the key in the header this provider reads it from.</summary>
    internal HttpRequestMessage GetRequest(Uri url)
    {
        var request = NewRequest(HttpMethod.Get, url);
        foreach (var (field, value) in Endpoint.Id.ListHeaders(Endpoint.ApiKey.Trim())) request.Headers.TryAddWithoutValidation(field, value);
        return request;
    }

    /// <summary>The body of a successful GET; any other status is an HTTP error.</summary>
    private async Task<byte[]> Get(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (status, data) = await Send(request, 20, cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status)) throw ApiException.Http(HttpStatus((int)status, data), ErrorMessage(data));
        return data;
    }

    /// <summary>
    /// <c>{"data": [{"id": …}]}</c>; Gemini's IDs come as "models/gemini-…", which requests name without the prefix, and
    /// Anthropic's name is its <c>display_name</c>.
    /// </summary>
    internal static List<ModelInfo> ParseModels(byte[] data)
    {
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
            if (id.StartsWith("models/", StringComparison.Ordinal)) id = id["models/".Length..];
            var reasoning = m["reasoning"] as JsonObject;
            models.Add(new ModelInfo(
                id,
                TryString(m["name"], out var name) ? name : TryString(m["display_name"], out var displayName) ? displayName : id,
                Strings(m["supported_parameters"]),
                reasoning?["mandatory"] is JsonValue mandatory && mandatory.TryGetValue<bool>(out var b) && b,
                Strings(reasoning?["supported_efforts"])));
        }
        return models;
    }

    /// <summary>
    /// Verifies the key/URL work: OpenRouter's /models is public, so check its key endpoint instead; Deepgram and
    /// ElevenLabs have no model list to ask for, so their own account endpoints (see <see cref="ProviderIdExtensions.KeyCheckPath"/>).
    /// </summary>
    public async Task<string> VerifyCredentials(CancellationToken cancellationToken = default)
    {
        if (IsOpenRouter)
        {
            using var request = NewRequest(HttpMethod.Get, Url("key"));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Endpoint.ApiKey.Trim());
            var (status, data) = await Send(request, 15, cancellationToken).ConfigureAwait(false);
            if (!IsSuccess(status)) throw ApiException.Http((int)status, ErrorMessage(data));
            return L("Key 有效", "Key is valid");
        }
        if (Endpoint.ApiKey.Trim().Length == 0 && Endpoint.Id.RequiresKey()) throw ApiException.MissingApiKey(Endpoint.Id.DisplayName());
        if (!Endpoint.Id.ListsModels())
        {
            using var check = GetRequest(Url(Endpoint.Id.KeyCheckPath()));
            await Get(check, cancellationToken).ConfigureAwait(false);
            return L("Key 有效", "Key is valid");
        }
        try
        {
            using var request = ModelListRequest();
            var models = ParseModels(await Get(request, cancellationToken).ConfigureAwait(false));
            return L($"连接正常，{models.Count} 个模型可用", $"Connected — {models.Count} models available");
        }
        catch (ApiException error) when (error.Kind == ApiErrorKind.Http && error.Status == 404 && Endpoint.Id.ModelListMayBeMissing())
        {
            // The server answered and took the key (a bad one is a 401); it just has no list.
            return L("连接正常", "Connected");
        }
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
