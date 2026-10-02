using System.Text.Json.Nodes;

namespace TypelessCore;

/// <summary>
/// Hands the user's vocabulary to the speech-to-text model as spelling hints, for the models that take them.
/// </summary>
/// <remarks>
/// OpenRouter's own <c>keyterms</c> field is turned down by every speech-to-text model it serves (Oct 2026), and its
/// model list doesn't say which take what. So the provider's own field is passed through <c>provider.options</c>, for
/// the models where it was measured to work: names the model got wrong came out right with it, and terms in the list
/// that weren't spoken never showed up (see docs/DESIGN.md). Anything else gets no hints rather than a field that might
/// be rejected; and a request that is rejected anyway is sent again without them (see <see cref="ApiClient"/>).
/// </remarks>
public static class VocabularyHints
{
    public enum Style
    {
        /// <summary>Azure speech (Microsoft MAI-Transcribe): <c>phraseList.phrases</c>.</summary>
        PhraseList,
        /// <summary>OpenAI transcription models and Groq's Whisper: a <c>prompt</c> with the words spelled right.</summary>
        Prompt,
        /// <summary>AssemblyAI Universal: <c>keyterms_prompt</c>.</summary>
        KeytermsPrompt,
    }

    /// <summary>At most this many terms are sent, the first ones in the list.</summary>
    public const int MaxTerms = 100;
    /// <summary>Whisper reads only the last 224 tokens of a prompt, so a prompt is kept well under that.</summary>
    public const int MaxPromptCharacters = 600;

    private static readonly string[] OpenAIPromptModels =
        ["openai/gpt-4o-transcribe", "openai/gpt-4o-mini-transcribe", "openai/gpt-transcribe", "openai/whisper-1"];

    /// <summary>How this model takes hints, or null when it doesn't (as far as was measured).</summary>
    public static Style? StyleFor(ProviderId provider, string model)
    {
        model = model.Trim().ToLowerInvariant();
        return provider switch
        {
            ProviderId.OpenRouter when model.StartsWith("microsoft/mai-transcribe", StringComparison.Ordinal) => Style.PhraseList,
            ProviderId.OpenRouter when model.StartsWith("assemblyai/", StringComparison.Ordinal) => Style.KeytermsPrompt,
            ProviderId.OpenRouter when OpenAIPromptModels.Any(m => model.StartsWith(m, StringComparison.Ordinal)) => Style.Prompt,
            // Both document `prompt` on /audio/transcriptions.
            ProviderId.OpenAI or ProviderId.Groq => Style.Prompt,
            _ => null,
        };
    }

    /// <summary>The terms to send: trimmed, without duplicates, at most <see cref="MaxTerms"/>.</summary>
    public static List<string> Terms(IEnumerable<string>? vocabulary)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (vocabulary ?? [])
            .Select(t => t.Trim())
            .Where(t => t.Length > 0 && seen.Add(t))
            .Take(MaxTerms)
            .ToList();
    }

    /// <summary>The terms as a prompt: just the words, comma-separated, cut at a whole term.</summary>
    public static string Prompt(IReadOnlyList<string> terms)
    {
        var prompt = "";
        foreach (var term in terms)
        {
            var next = prompt.Length == 0 ? term : prompt + ", " + term;
            if (next.Length > MaxPromptCharacters) break;
            prompt = next;
        }
        return prompt;
    }

    /// <summary>The OpenRouter <c>provider</c> object carrying the hints for this model, or null.</summary>
    public static JsonObject? OpenRouterProvider(string model, IEnumerable<string>? vocabulary)
    {
        var terms = Terms(vocabulary);
        if (terms.Count == 0 || StyleFor(ProviderId.OpenRouter, model) is not { } style) return null;
        JsonArray Array() => new(terms.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        var options = style switch
        {
            Style.PhraseList => new JsonObject { ["azure"] = new JsonObject { ["phraseList"] = new JsonObject { ["phrases"] = Array() } } },
            Style.Prompt => new JsonObject { ["openai"] = new JsonObject { ["prompt"] = Prompt(terms) } },
            _ => new JsonObject { ["assemblyai"] = new JsonObject { ["keyterms_prompt"] = Array() } },
        };
        return new JsonObject { ["options"] = options };
    }

    /// <summary>The <c>prompt</c> form field for a direct OpenAI or Groq request, or null.</summary>
    public static string? MultipartPrompt(ProviderId provider, string model, IEnumerable<string>? vocabulary)
    {
        var terms = Terms(vocabulary);
        if (provider == ProviderId.OpenRouter || terms.Count == 0 || StyleFor(provider, model) != Style.Prompt) return null;
        return Prompt(terms);
    }
}
