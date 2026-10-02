using System.Text;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

[Collection("MockServer")]
public class VocabularyHintsTests
{
    private static readonly string[] Vocabulary = ["OpenTypeless", " Kwazara ", "", "opentypeless", "卓维克"];

    private static async Task<JsonObject> Body(string model, IReadOnlyList<string>? vocabulary)
    {
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        using var request = client.TranscriptionRequest([0], new TranscriptionOptions(model, null, vocabulary));
        return (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
    }

    [Fact]
    public void TermsAreTrimmedDedupedAndCapped()
    {
        Assert.Equal(new[] { "OpenTypeless", "Kwazara", "卓维克" }, VocabularyHints.Terms(Vocabulary));
        Assert.Equal(VocabularyHints.MaxTerms, VocabularyHints.Terms(Enumerable.Range(0, 300).Select(i => $"t{i}")).Count);
    }

    [Fact]
    public void PromptStopsAtAWholeTerm()
    {
        var prompt = VocabularyHints.Prompt(Enumerable.Range(0, 200).Select(i => $"term{i}").ToList());
        Assert.True(prompt.Length <= VocabularyHints.MaxPromptCharacters);
        Assert.False(prompt.EndsWith(','));
        Assert.Matches(@"term\d+$", prompt);
    }

    [Fact]
    public async Task MicrosoftGetsAPhraseList()
    {
        var body = await Body("microsoft/mai-transcribe-2", Vocabulary);
        var phrases = body["provider"]!["options"]!["azure"]!["phraseList"]!["phrases"]!.AsArray().Select(n => (string)n!);
        Assert.Equal(new[] { "OpenTypeless", "Kwazara", "卓维克" }, phrases);
    }

    [Theory]
    [InlineData("openai/gpt-4o-transcribe")]
    [InlineData("openai/gpt-4o-mini-transcribe")]
    [InlineData("openai/whisper-1")]
    public async Task OpenAIModelsGetAPrompt(string model)
    {
        var body = await Body(model, Vocabulary);
        Assert.Equal("OpenTypeless, Kwazara, 卓维克", (string)body["provider"]!["options"]!["openai"]!["prompt"]!);
    }

    [Fact]
    public async Task AssemblyAIGetsKeyterms()
    {
        var body = await Body("assemblyai/universal-3-5-pro", Vocabulary);
        Assert.Equal(3, body["provider"]!["options"]!["assemblyai"]!["keyterms_prompt"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("openai/whisper-large-v3")]
    [InlineData("google/gemini-3.5-transcribe")]
    [InlineData("deepgram/nova-3")]
    [InlineData("nvidia/parakeet-tdt-0.6b-v3")]
    public async Task OtherModelsGetNoHints(string model) => Assert.Null((await Body(model, Vocabulary))["provider"]);

    [Fact]
    public async Task EmptyVocabularyGetsNoHints() => Assert.Null((await Body("microsoft/mai-transcribe-2", [" ", ""]))["provider"]);

    [Fact]
    public void DirectOpenAIAndGroqSendAPromptField()
    {
        Assert.Equal("OpenTypeless, Kwazara, 卓维克", VocabularyHints.MultipartPrompt(ProviderId.OpenAI, "gpt-4o-transcribe", Vocabulary));
        Assert.NotNull(VocabularyHints.MultipartPrompt(ProviderId.Groq, "whisper-large-v3-turbo", Vocabulary));
        Assert.Null(VocabularyHints.MultipartPrompt(ProviderId.SiliconFlow, "FunAudioLLM/SenseVoiceSmall", Vocabulary));
        Assert.Null(VocabularyHints.MultipartPrompt(ProviderId.Custom, "whisper", Vocabulary));
        var form = MultipartForm.Transcription([0], new TranscriptionOptions("m"), "OpenTypeless");
        Assert.Contains("name=\"prompt\"\r\n\r\nOpenTypeless\r\n", Encoding.UTF8.GetString(form.Body));
    }

    [Fact]
    public async Task ARejectedHintIsSentAgainWithoutIt()
    {
        var bodies = new List<string>();
        MockOpenRouter.Delay = null;
        MockOpenRouter.Handler = (_, body) =>
        {
            var text = Encoding.UTF8.GetString(body);
            lock (bodies) bodies.Add(text);
            return text.Contains("\"provider\"")
                ? (400, MockOpenRouter.Utf8("""{"error":{"message":"Provider returned 400"}}"""))
                : (200, MockOpenRouter.Utf8("""{"text":"ok"}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        var text = await client.Transcribe([0], new TranscriptionOptions("microsoft/mai-transcribe-2", null, Vocabulary), 10);
        Assert.Equal("ok", text);
        Assert.Equal(2, bodies.Count);
        Assert.DoesNotContain("\"provider\"", bodies[1]);
    }

    [Fact]
    public async Task ARequestWithoutHintsIsNotRepeated()
    {
        var count = 0;
        MockOpenRouter.Delay = null;
        MockOpenRouter.Handler = (_, _) =>
        {
            Interlocked.Increment(ref count);
            return (400, MockOpenRouter.Utf8("""{"error":{"message":"bad"}}"""));
        };
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), MockOpenRouter.Client());
        await Assert.ThrowsAsync<ApiException>(() => client.Transcribe([0], new TranscriptionOptions("deepgram/nova-3", null, Vocabulary), 10));
        Assert.Equal(1, count);
    }
}
