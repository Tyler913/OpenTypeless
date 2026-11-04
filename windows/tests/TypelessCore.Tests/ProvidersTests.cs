using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>The provider table, the clean-up fields each provider gets, and the Deepgram and ElevenLabs request formats.</summary>
[Collection("MockServer")]
public class ProvidersTests
{
    /// <summary>testdata/provider-cases.json, shared with the macOS tests so both apps offer the same providers.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "provider-cases.json")))!
            .AsObject();

    private static readonly string[] Vocabulary = ["OpenTypeless", " Kwazara ", "", "opentypeless", "C++", "卓维克"];

    /// <summary>A recorded request: what the mock server was sent.</summary>
    private sealed record Sent(HttpMethod Method, Uri Url, Dictionary<string, string> Headers, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);
    }

    /// <summary>Answers every request with <paramref name="answer"/>, recording each one.</summary>
    private static List<Sent> Serve(Func<Sent, (int Status, string Body)> answer)
    {
        var sent = new List<Sent>();
        MockOpenRouter.Delay = null;
        MockOpenRouter.Handler = (request, body) =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers) headers[name] = string.Join(",", values);
            if (request.Content != null)
            {
                foreach (var (name, values) in request.Content.Headers) headers[name] = string.Join(",", values);
            }
            var record = new Sent(request.Method, request.RequestUri!, headers, body);
            lock (sent) sent.Add(record);
            var (status, text) = answer(record);
            return (status, MockOpenRouter.Utf8(text));
        };
        return sent;
    }

    private static ApiClient Client(ProviderId id, string key = "k") => new(ProviderEndpoint.Preset(id, key), MockOpenRouter.Client());

    // MARK: The table

    [Fact]
    public void EveryProviderMatchesTheSharedTable()
    {
        var providers = Cases()["providers"]!.AsArray();
        Assert.Equal(providers.Select(p => (string)p!["id"]!), ProviderIdExtensions.All.Select(p => p.RawValue()));
        foreach (var node in providers)
        {
            var c = node!.AsObject();
            var raw = (string)c["id"]!;
            var id = Assert.NotNull(ProviderIdExtensions.Parse(raw));
            Assert.Equal(raw, id.RawValue());
            Assert.Equal((string)c["baseURL"]!, id.DefaultBaseUrl());
            Assert.Equal((bool)c["stt"]!, id.SupportsStt());
            Assert.Equal((bool)c["chat"]!, id.SupportsChat());
            Assert.Equal(((string)c["sttFormat"]!).ToLowerInvariant(), id.SttFormat().ToString().ToLowerInvariant());
            Assert.Equal((bool)c["requiresKey"]!, id.RequiresKey());
            Assert.Equal((string)c["sttModel"]!, id.DefaultSttModel());
            Assert.Equal((string)c["chatModel"]!, id.DefaultChatModel());
            Assert.Equal((string)c["backupSttModel"]!, id.DefaultBackupSttModel());
            Assert.Equal((string)c["backupChatModel"]!, id.DefaultBackupChatModel());
            Assert.Equal((string?)c["keysURL"], id.KeysUrl());
            Assert.Equal((string)c["group"]!, id.Group().ToString().ToLowerInvariant());
            Assert.Equal((bool)c["temperature"]!, id.ChatSendsTemperature());
            Assert.Equal((bool)c["streamOptions"]!, id.ChatSendsStreamOptions());
            Assert.Equal((bool)c["minimisesReasoning"]!, id.MinimisesReasoning());
        }
    }

    [Fact]
    public void EveryProviderCanBeSetUp()
    {
        foreach (var id in ProviderIdExtensions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(id.DisplayName()));
            Assert.False(string.IsNullOrWhiteSpace(id.KeyPlaceholder()));
            Assert.True(id.SupportsStt() || id.SupportsChat(), id.RawValue());
            if (id == ProviderId.Custom) continue;
            Assert.True(Uri.TryCreate(id.DefaultBaseUrl(), UriKind.Absolute, out var url), id.RawValue());
            Assert.Equal(id.IsLocal() ? "http" : "https", url!.Scheme);
            if (id.IsLocal())
            {
                Assert.Equal("localhost", url.Host);
                Assert.Null(id.KeysUrl());
                continue;
            }
            // A provider in the cloud: a key, a page to get one from, and a model for each step it serves.
            Assert.True(id.RequiresKey(), id.RawValue());
            Assert.StartsWith("https://", id.KeysUrl());
            if (id.SupportsStt()) Assert.NotEqual("", id.DefaultSttModel());
            if (id.SupportsChat()) Assert.NotEqual("", id.DefaultChatModel());
            if (!id.SupportsStt()) Assert.Equal("", id.DefaultSttModel());
            if (!id.SupportsChat()) Assert.Equal("", id.DefaultChatModel());
        }
    }

    [Fact]
    public void SavedValuesKeepTheirMeaning()
    {
        // Settings and Credential Manager store the raw values; nothing that was saved before changes.
        Assert.Equal(new[] { "openrouter", "openai", "groq", "siliconflow", "deepseek", "custom" },
                     new[] { ProviderId.OpenRouter, ProviderId.OpenAI, ProviderId.Groq, ProviderId.SiliconFlow, ProviderId.DeepSeek, ProviderId.Custom }
                         .Select(p => p.RawValue()));
        Assert.Equal(5, (int)ProviderId.Custom);
        Assert.Null(ProviderIdExtensions.Parse("nosuchprovider"));
        Assert.Equal(ProviderIdExtensions.All.Length, ProviderIdExtensions.All.Select(p => p.RawValue()).Distinct().Count());
        Assert.Equal(ProviderIdExtensions.All.Length, Enum.GetValues<ProviderId>().Length);
    }

    // MARK: Clean-up

    [Fact]
    public void CleanUpTurnsReasoningOffTheProvidersOwnWay()
    {
        string[] fields = ["reasoning_effort", "thinking", "enable_thinking", "reasoning"];
        foreach (var node in Cases()["reasoning"]!.AsArray())
        {
            var id = ProviderIdExtensions.Parse((string)node!["provider"]!)!.Value;
            var model = (string)node["model"]!;
            var body = Client(id).PolishBody("x", new PolishOptions(model), includeOptional: true);
            var expected = node["fields"]!.AsObject();
            foreach (var field in fields)
            {
                Assert.Equal(expected[field]?.ToJsonString(), body[field]?.ToJsonString());
            }
            // The bare request sent after a 400 carries none of them.
            var bare = Client(id).PolishBody("x", new PolishOptions(model), includeOptional: false);
            Assert.All(fields, f => Assert.Null(bare[f]));
        }
    }

    [Fact]
    public void AnthropicAlwaysGetsALimitAndNoTemperature()
    {
        var full = Client(ProviderId.Anthropic).PolishBody("hello", new PolishOptions("claude-haiku-5-5"), includeOptional: true);
        Assert.Null(full["temperature"]);
        Assert.Equal(1024, (int)full["max_tokens"]!);
        Assert.NotNull(full["stream_options"]);
        var bare = Client(ProviderId.Anthropic).PolishBody("hello", new PolishOptions("claude-haiku-5-5"), includeOptional: false);
        Assert.Equal(1024, (int)bare["max_tokens"]!);
        // Elsewhere the bare request stays bare.
        Assert.Null(Client(ProviderId.Gemini).PolishBody("hello", new PolishOptions("gemini-3.8-flash"), includeOptional: false)["max_tokens"]);
        Assert.Null(Client(ProviderId.Mistral).PolishBody("hello", new PolishOptions("mistral-small-latest"), includeOptional: true)["stream_options"]);
    }

    [Fact]
    public async Task AModelThatCantStopThinkingGetsTheBareRequest()
    {
        var sent = Serve(request => request.Text.Contains("\"thinking\"")
            ? (400, """{"error":{"type":"invalid_request_error","message":"thinking.type: disabled is not supported for this model"}}""")
            : (200, "data: {\"choices\":[{\"delta\":{\"content\":\"Hello.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        var result = await Client(ProviderId.Anthropic, "sk-ant-test").Polish("hello", new PolishOptions("claude-opus-5-5"));
        Assert.Equal("Hello.", result.Text);
        Assert.Equal(2, sent.Count);
        Assert.Equal("https://api.anthropic.com/v1/chat/completions", sent[0].Url.AbsoluteUri);
        Assert.Equal("Bearer sk-ant-test", sent[0].Headers["Authorization"]);
        Assert.DoesNotContain("\"thinking\"", sent[1].Text);
        Assert.Contains("\"max_tokens\"", sent[1].Text);
    }

    // MARK: Deepgram

    [Fact]
    public async Task DeepgramGetsTheAudioAsTheBody()
    {
        var sent = Serve(_ => (200, """{"metadata":{"request_id":"r","duration":2.5},"results":{"channels":[{"alternatives":[{"transcript":"Hello, OpenTypeless.","confidence":0.99}]}]}}"""));
        var wav = Wav.Encode(new short[] { 1, 2, 3 });
        var result = await Client(ProviderId.Deepgram, "dg-key").TranscribeDetailed(wav, new TranscriptionOptions("nova-3", null, Vocabulary), 5);
        Assert.Equal("Hello, OpenTypeless.", result.Text);
        Assert.Equal(2.5, result.Usage!.AudioSeconds);
        var request = Assert.Single(sent);
        Assert.Equal("api.deepgram.com", request.Url.Host);
        Assert.Equal("/v1/listen", request.Url.AbsolutePath);
        Assert.Equal("Token dg-key", request.Headers["Authorization"]);
        Assert.Equal("audio/wav", request.Headers["Content-Type"]);
        Assert.Equal(wav, request.Body);
        Assert.Equal("?model=nova-3&smart_format=true&punctuate=true&detect_language=true"
                     + "&keyterm=OpenTypeless&keyterm=Kwazara&keyterm=C%2B%2B&keyterm=%E5%8D%93%E7%BB%B4%E5%85%8B", request.Url.Query);
    }

    [Fact]
    public async Task DeepgramTakesTheSpokenLanguageAndNoKeytermsBeforeNova3()
    {
        var sent = Serve(_ => (200, """{"results":{"channels":[{"alternatives":[{"transcript":"你好"}]}]}}"""));
        var result = await Client(ProviderId.Deepgram).TranscribeDetailed([0], new TranscriptionOptions("nova-2", "zh", Vocabulary), 5);
        Assert.Equal("你好", result.Text);
        Assert.Null(result.Usage);
        Assert.Equal("?model=nova-2&smart_format=true&punctuate=true&language=zh", Assert.Single(sent).Url.Query);
    }

    [Theory]
    [InlineData(401, """{"err_code":"INVALID_AUTH","err_msg":"Invalid credentials.","request_id":"r"}""", "Invalid credentials.", false, true)]
    [InlineData(402, """{"err_code":"ASR_PAYMENT_REQUIRED","err_msg":"Project does not have enough credits.","request_id":"r"}""", "Project does not have enough credits.", false, true)]
    [InlineData(403, """{"err_code":"INSUFFICIENT_PERMISSIONS","err_msg":"Project does not have access to the requested model."}""", "Project does not have access to the requested model.", false, true)]
    [InlineData(429, """{"err_code":"TOO_MANY_REQUESTS","err_msg":"Too many requests."}""", "Too many requests.", true, false)]
    [InlineData(503, "Service Unavailable", "Service Unavailable", true, false)]
    public async Task DeepgramErrorsKeepTheirMeaning(int status, string body, string message, bool retryable, bool refusesAll)
    {
        Serve(_ => (status, body));
        var error = await Assert.ThrowsAsync<ApiException>(() => Client(ProviderId.Deepgram).Transcribe([0], new TranscriptionOptions("nova-3"), 5));
        Assert.Equal(ApiException.Http(status, message), error);
        Assert.Equal(retryable, error.IsRetryable);
        Assert.Equal(refusesAll, error.RefusesEveryRequest);
    }

    [Fact]
    public async Task DeepgramWithoutATranscriptIsABadResponse()
    {
        Serve(_ => (200, """{"metadata":{},"results":{"channels":[]}}"""));
        var error = await Assert.ThrowsAsync<ApiException>(() => Client(ProviderId.Deepgram).Transcribe([0], new TranscriptionOptions("nova-3"), 5));
        Assert.Equal(ApiErrorKind.BadResponse, error.Kind);
        Assert.True(error.IsRetryable);
    }

    [Fact]
    public async Task ARejectedKeytermIsSentAgainWithout()
    {
        var sent = Serve(request => request.Url.Query.Contains("keyterm")
            ? (400, """{"err_code":"Bad Request","err_msg":"Invalid keyterm."}""")
            : (200, """{"results":{"channels":[{"alternatives":[{"transcript":"ok"}]}]}}"""));
        Assert.Equal("ok", await Client(ProviderId.Deepgram).Transcribe([0], new TranscriptionOptions("nova-3", null, Vocabulary), 5));
        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain("keyterm", sent[1].Url.Query);
    }

    [Fact]
    public void DeepgramKeytermsStayUnderItsTokenLimit()
    {
        var terms = Enumerable.Range(0, 100).Select(i => $"Kwazara product name number {i}").ToList();
        var kept = VocabularyHints.Keyterms(terms);
        Assert.InRange(kept.Count, 1, 99);
        Assert.Equal(terms.Take(kept.Count), kept);
        Assert.True(kept.Sum(TokenEstimate.Count) <= VocabularyHints.MaxKeytermTokens);
    }

    // MARK: ElevenLabs

    [Fact]
    public async Task ElevenLabsGetsAScribeForm()
    {
        var sent = Serve(_ => (200, """{"language_code":"eng","language_probability":0.98,"text":"Hello there.","words":[]}"""));
        var terms = new[] { "OpenTypeless", new string('x', 51), "one two three four five six", "a<b", "Kwazara Labs" };
        var text = await Client(ProviderId.ElevenLabs, "sk_test").Transcribe([1, 2], new TranscriptionOptions("scribe_v2", "en", terms), 5);
        Assert.Equal("Hello there.", text);
        var request = Assert.Single(sent);
        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", request.Url.AbsoluteUri);
        Assert.Equal("sk_test", request.Headers["xi-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.StartsWith("multipart/form-data; boundary=", request.Headers["Content-Type"]);
        var form = request.Text;
        Assert.Contains("name=\"model_id\"\r\n\r\nscribe_v2\r\n", form);
        Assert.Contains("name=\"file\"; filename=\"audio.wav\"", form);
        Assert.Contains("name=\"language_code\"\r\n\r\nen\r\n", form);
        Assert.Contains("name=\"tag_audio_events\"\r\n\r\nfalse\r\n", form);
        Assert.DoesNotContain("name=\"model\"", form);
        // Only the terms ElevenLabs takes: at most 50 characters and five words, none of <>{}[]\.
        Assert.Contains("name=\"keyterms\"\r\n\r\nOpenTypeless\r\n", form);
        Assert.Contains("name=\"keyterms\"\r\n\r\nKwazara Labs\r\n", form);
        Assert.Equal(2, form.Split("name=\"keyterms\"").Length - 1);
    }

    [Theory]
    [InlineData(401, """{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}""", 401, "Invalid API key")]
    [InlineData(401, """{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota."}}""", 402, "This request exceeds your quota.")]
    [InlineData(429, """{"detail":{"status":"too_many_concurrent_requests","message":"Too many concurrent requests."}}""", 429, "Too many concurrent requests.")]
    [InlineData(500, """{"detail":{"status":"internal_error","message":"Something went wrong"}}""", 500, "Something went wrong")]
    public async Task ElevenLabsErrorsKeepTheirMeaning(int status, string body, int classified, string message)
    {
        Serve(_ => (status, body));
        var error = await Assert.ThrowsAsync<ApiException>(() => Client(ProviderId.ElevenLabs).Transcribe([0], new TranscriptionOptions("scribe_v2"), 5));
        Assert.Equal(ApiException.Http(classified, message), error);
        Assert.Equal(classified is 429 or >= 500, error.IsRetryable);
    }

    [Fact]
    public async Task AKeytermThatFailsValidationIsSentAgainWithout()
    {
        var sent = Serve(request => request.Text.Contains("name=\"keyterms\"")
            ? (422, """{"detail":[{"loc":["body","keyterms",0],"msg":"Keyterm is not supported","type":"value_error"}]}""")
            : (200, """{"text":"ok","words":[]}"""));
        Assert.Equal("ok", await Client(ProviderId.ElevenLabs).Transcribe([0], new TranscriptionOptions("scribe_v2", null, Vocabulary), 5));
        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain("keyterms", sent[1].Text);
    }

    [Fact]
    public void ValidationErrorsNameTheField()
    {
        var message = ApiClient.ErrorMessage(MockOpenRouter.Utf8(
            """{"detail":[{"loc":["body","stream_options"],"msg":"Extra inputs are not permitted","type":"extra_forbidden"}]}"""));
        Assert.Equal("body.stream_options: Extra inputs are not permitted", message);
    }

    // MARK: Mistral, Zhipu and local servers

    [Fact]
    public async Task MistralGetsContextBiasForEachTerm()
    {
        var sent = Serve(_ => (200, """{"model":"voxtral-mini-transcribe-3","text":"Bonjour.","usage":{"prompt_audio_seconds":3,"prompt_tokens":3,"completion_tokens":5}}"""));
        var result = await Client(ProviderId.Mistral, "m-key").TranscribeDetailed([0], new TranscriptionOptions("voxtral-mini-transcribe-3", "fr", Vocabulary), 5);
        Assert.Equal("Bonjour.", result.Text);
        var request = Assert.Single(sent);
        Assert.Equal("https://api.mistral.ai/v1/audio/transcriptions", request.Url.AbsoluteUri);
        Assert.Equal("Bearer m-key", request.Headers["Authorization"]);
        Assert.Contains("name=\"context_bias\"\r\n\r\nOpenTypeless\r\n", request.Text);
        Assert.Contains("name=\"context_bias\"\r\n\r\n卓维克\r\n", request.Text);
        Assert.Equal(4, request.Text.Split("name=\"context_bias\"").Length - 1);
        Assert.DoesNotContain("name=\"prompt\"", request.Text);
    }

    [Fact]
    public async Task ZhipuAndLocalServersGetAPlainForm()
    {
        var sent = Serve(_ => (200, """{"id":"x","text":"你好"}"""));
        Assert.Equal("你好", await Client(ProviderId.Zhipu).Transcribe([0], new TranscriptionOptions("glm-asr-2512", null, Vocabulary), 5));
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4/audio/transcriptions", sent[0].Url.AbsoluteUri);
        Assert.DoesNotContain("context_bias", sent[0].Text);
        Assert.DoesNotContain("name=\"prompt\"", sent[0].Text);
        // Ollama needs no key, and gets none.
        Assert.Equal("你好", await Client(ProviderId.Ollama, "").Transcribe([0], new TranscriptionOptions("whisper"), 5));
        Assert.Equal("http://localhost:11434/v1/audio/transcriptions", sent[1].Url.AbsoluteUri);
        Assert.False(sent[1].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task ACloudProviderWithoutAKeyIsNotAsked()
    {
        var sent = Serve(_ => (200, "{}"));
        foreach (var id in new[] { ProviderId.Deepgram, ProviderId.ElevenLabs, ProviderId.Mistral })
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => Client(id, " ").Transcribe([0], new TranscriptionOptions(id.DefaultSttModel()), 5));
            Assert.Equal(ApiException.MissingApiKey(id.DisplayName()), error);
            await Assert.ThrowsAsync<ApiException>(() => Client(id, "").VerifyCredentials());
        }
        Assert.Empty(sent);
    }

    // MARK: Test button and model lists

    [Fact]
    public async Task TheKeyCheckAsksEachProviderItsOwnWay()
    {
        var sent = Serve(request => request.Url.AbsolutePath switch
        {
            "/v1/projects" => (200, """{"projects":[{"project_id":"p","name":"Default"}]}"""),
            "/v1/user" => (200, """{"subscription":{"tier":"free"}}"""),
            "/v1/models" => (200, """{"data":[{"id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5","type":"model"}],"has_more":false}"""),
            _ => (404, "Not Found"),
        });
        Assert.Equal("Key is valid", await Client(ProviderId.Deepgram, "dg").VerifyCredentials());
        Assert.Equal("Token dg", sent[0].Headers["Authorization"]);
        Assert.Equal("Key is valid", await Client(ProviderId.ElevenLabs, "el").VerifyCredentials());
        Assert.Equal("https://api.elevenlabs.io/v1/user", sent[1].Url.AbsoluteUri);
        Assert.Equal("el", sent[1].Headers["xi-api-key"]);
        Assert.Equal("Connected — 1 models available", await Client(ProviderId.Anthropic, "sk-ant").VerifyCredentials());
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", sent[2].Url.AbsoluteUri);
        Assert.Equal("sk-ant", sent[2].Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent[2].Headers["anthropic-version"]);
        Assert.False(sent[2].Headers.ContainsKey("Authorization"));
        Assert.All(sent, s => Assert.Equal(HttpMethod.Get, s.Method));
    }

    [Fact]
    public async Task VolcengineWithoutAModelListStillConnects()
    {
        Serve(_ => (404, """{"error":{"code":"NotFound","message":"The requested resource was not found."}}"""));
        Assert.Equal("Connected", await Client(ProviderId.Volcengine).VerifyCredentials());
        // Elsewhere a missing list is an error: the base URL is probably wrong.
        var error = await Assert.ThrowsAsync<ApiException>(() => Client(ProviderId.Moonshot).VerifyCredentials());
        Assert.Equal(404, error.Status);
        // A bad key is still a bad key.
        Serve(_ => (401, """{"error":{"code":"AuthenticationError","message":"The API key is invalid."}}"""));
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => Client(ProviderId.Volcengine).VerifyCredentials())).Status);
    }

    [Fact]
    public async Task ModelListsReadEachProvidersIds()
    {
        var sent = Serve(_ => (200, """{"object":"list","data":[{"id":"models/gemini-3.8-flash","object":"model"},{"id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5"}]}"""));
        var models = await Client(ProviderId.Gemini).ListModels();
        Assert.Equal(new[] { "gemini-3.8-flash", "claude-haiku-5-5" }, models.Select(m => m.Id));
        Assert.Equal("Claude Haiku 5.5", models[1].Name);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/models", sent[0].Url.AbsoluteUri);
        Assert.Empty(await Client(ProviderId.Deepgram).ListModels());
        Assert.Empty(await Client(ProviderId.ElevenLabs).ListModels());
        Assert.Single(sent);
        Assert.True(new ModelInfo("voxtral-mini-transcribe-3", "Voxtral").LooksLikeSpeechModel);
    }

    // MARK: Vocabulary hints

    [Fact]
    public void HintsGoOnlyWhereTheProviderDocumentsThem()
    {
        Assert.Equal(VocabularyHints.Style.ContextBias, VocabularyHints.StyleFor(ProviderId.Mistral, "voxtral-mini-transcribe-3"));
        Assert.Null(VocabularyHints.StyleFor(ProviderId.Mistral, "mistral-small-latest"));
        Assert.Equal(VocabularyHints.Style.Keyterm, VocabularyHints.StyleFor(ProviderId.Deepgram, "nova-3-medical"));
        Assert.Null(VocabularyHints.StyleFor(ProviderId.Deepgram, "nova-2"));
        Assert.Equal(VocabularyHints.Style.Keyterms, VocabularyHints.StyleFor(ProviderId.ElevenLabs, "scribe_v2"));
        foreach (var id in new[] { ProviderId.Zhipu, ProviderId.Ollama, ProviderId.Gemini, ProviderId.Anthropic, ProviderId.LMStudio })
        {
            Assert.Null(VocabularyHints.DirectHints(id, id.DefaultSttModel(), Vocabulary));
        }
        Assert.Equal(new VocabularyHints.Direct(VocabularyHints.Style.Keyterms, Terms: ["OpenTypeless", "Kwazara", "C++", "卓维克"]),
                     VocabularyHints.DirectHints(ProviderId.ElevenLabs, "scribe_v2", Vocabulary));
        Assert.Null(VocabularyHints.DirectHints(ProviderId.ElevenLabs, "scribe_v2", [new string('x', 60)]));
    }
}
