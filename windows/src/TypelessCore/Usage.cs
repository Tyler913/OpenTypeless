using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypelessCore;

/// <summary>
/// What one request used, as the server reported it. Every field is optional: OpenRouter reports tokens, audio
/// seconds and the billed cost; OpenAI-compatible servers report tokens at most. <see cref="Estimated"/> marks usage
/// filled in from the text because the server reported nothing.
/// </summary>
public sealed record RequestUsage(int? InputTokens = null, int? OutputTokens = null, double? AudioSeconds = null,
                                  double? Cost = null, bool Estimated = false)
{
    /// <summary>
    /// Reads a <c>usage</c> object: chat completions (<c>prompt_tokens</c> / <c>completion_tokens</c>) and transcriptions
    /// (<c>input_tokens</c> / <c>output_tokens</c> / <c>seconds</c>), with OpenRouter's <c>cost</c> in USD.
    /// </summary>
    public static RequestUsage? Parse(JsonNode? node)
    {
        if (node is not JsonObject usage) return null;
        int? Tokens(string key, string alternative)
        {
            var value = Number(usage[key]) ?? Number(usage[alternative]);
            return value is { } v ? (int)Math.Round(v) : null;
        }
        var parsed = new RequestUsage(Tokens("prompt_tokens", "input_tokens"), Tokens("completion_tokens", "output_tokens"),
                                      Number(usage["seconds"]), Number(usage["cost"]));
        return parsed.InputTokens == null && parsed.OutputTokens == null && parsed.AudioSeconds == null && parsed.Cost == null ? null : parsed;
    }

    /// <summary>A finite, non-negative number, or null.</summary>
    internal static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        double number;
        if (value.TryGetValue<double>(out var d)) number = d;
        else if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) number = p;
        else return null;
        return double.IsFinite(number) && number >= 0 ? number : null;
    }

    /// <summary>Adds the audio length when the server didn't report it.</summary>
    public RequestUsage WithAudioSeconds(double seconds) => AudioSeconds == null ? this with { AudioSeconds = seconds } : this;
}

/// <summary>Rough token counts for servers that report no usage: one per CJK character, one per four other characters.</summary>
public static class TokenEstimate
{
    public static int Count(string text)
    {
        var cjk = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (WordCount.IsCjkWordCharacter(rune.Value)) cjk++;
            else other++;
        }
        return cjk + (other + 3) / 4;
    }
}

/// <summary>
/// A model's price in USD: per million input and output tokens, and per minute of audio for speech-to-text.
/// Entered by the user for providers other than OpenRouter.
/// </summary>
public sealed record ModelPrice(double? InputPerMillion = null, double? OutputPerMillion = null, double? PerMinute = null)
{
    public bool HasTokenPrices => InputPerMillion != null || OutputPerMillion != null;
    public bool IsEmpty => !HasTokenPrices && PerMinute == null;

    /// <summary>The key a user-entered price is stored under: the provider and the model ID.</summary>
    public static string Key(ProviderId provider, string model) => provider.RawValue() + "|" + model.Trim();

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (InputPerMillion is { } input) json["input"] = input;
        if (OutputPerMillion is { } output) json["output"] = output;
        if (PerMinute is { } minute) json["minute"] = minute;
        return json;
    }

    public static ModelPrice? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json) return null;
        var price = new ModelPrice(RequestUsage.Number(json["input"]), RequestUsage.Number(json["output"]), RequestUsage.Number(json["minute"]));
        return price.IsEmpty ? null : price;
    }
}

/// <summary>Works out what a request cost, from the cost the server reported or from a price.</summary>
public static class CostEstimator
{
    /// <summary>
    /// A clean-up request. With <paramref name="preferReported"/> (OpenRouter, which bills exactly what it reports)
    /// the reported cost wins; otherwise a user-entered price wins over whatever the server says. Null when there's
    /// nothing to go on.
    /// </summary>
    public static double? Chat(RequestUsage usage, ModelPrice? price, bool preferReported)
    {
        if (preferReported && usage.Cost is { } reported) return reported;
        if (price is { HasTokenPrices: true } && (usage.InputTokens != null || usage.OutputTokens != null))
        {
            return FromTokens(usage, price);
        }
        return usage.Cost;
    }

    /// <summary>A speech-to-text request: priced per minute of audio, or per token when the server counts tokens.</summary>
    public static double? Transcription(RequestUsage usage, ModelPrice? price, bool preferReported)
    {
        if (preferReported && usage.Cost is { } reported) return reported;
        if (price?.PerMinute is { } perMinute && usage.AudioSeconds is { } seconds) return seconds / 60 * perMinute;
        if (price is { HasTokenPrices: true } && !usage.Estimated && (usage.InputTokens != null || usage.OutputTokens != null))
        {
            return FromTokens(usage, price);
        }
        return usage.Cost;
    }

    /// <summary>All the speech-to-text requests of one dictation: their total cost, and how many had no price.</summary>
    public static (double Cost, int Unpriced) Transcriptions(IEnumerable<RequestUsage> usages, ModelPrice? price, bool preferReported)
    {
        var total = 0.0;
        var unpriced = 0;
        foreach (var usage in usages)
        {
            if (Transcription(usage, price, preferReported) is { } cost) total += cost;
            else unpriced++;
        }
        return (total, unpriced);
    }

    private static double FromTokens(RequestUsage usage, ModelPrice price) =>
        (usage.InputTokens ?? 0) * (price.InputPerMillion ?? 0) / 1_000_000
        + (usage.OutputTokens ?? 0) * (price.OutputPerMillion ?? 0) / 1_000_000;
}

/// <summary>
/// OpenRouter's current prices for every model, from its public <c>/models</c> list (no key needed). Prices change,
/// so the app refreshes the list regularly and caches the last one on disk. OpenRouter's reported cost per request
/// is what's counted; these prices are shown next to the models and fill in when a response carries no cost.
/// </summary>
public sealed class PriceCatalog
{
    public DateTimeOffset FetchedAt { get; }
    public IReadOnlyDictionary<string, ModelPrice> Models { get; }

    public PriceCatalog(DateTimeOffset fetchedAt, IReadOnlyDictionary<string, ModelPrice> models)
    {
        FetchedAt = fetchedAt;
        Models = models;
    }

    public static PriceCatalog Empty { get; } = new(DateTimeOffset.MinValue, new Dictionary<string, ModelPrice>());

    public bool IsEmpty => Models.Count == 0;

    public ModelPrice? Price(string model) => Models.GetValueOrDefault(model.Trim());

    public bool IsOlderThan(TimeSpan age, DateTimeOffset now) => now - FetchedAt >= age;

    /// <summary>
    /// Reads a <c>/models</c> response. <c>pricing.prompt</c> and <c>pricing.completion</c> are USD per token, stored
    /// here per million; negative values mean "varies" (routers) and are skipped.
    /// </summary>
    public static Dictionary<string, ModelPrice> ParseModels(ReadOnlySpan<byte> json)
    {
        var models = new Dictionary<string, ModelPrice>();
        JsonArray? list;
        try
        {
            list = (JsonNode.Parse(json.ToArray()) as JsonObject)?["data"] as JsonArray;
        }
        catch (JsonException)
        {
            return models;
        }
        foreach (var node in list ?? [])
        {
            if (node is not JsonObject model || !ApiClient.TryString(model["id"], out var id) || model["pricing"] is not JsonObject pricing) continue;
            var input = RequestUsage.Number(pricing["prompt"]);
            var output = RequestUsage.Number(pricing["completion"]);
            if (input == null && output == null) continue;
            models[id] = new ModelPrice(input * 1_000_000, output * 1_000_000);
        }
        return models;
    }

    /// <summary>Downloads the chat and speech-to-text model lists (OpenRouter lists them separately).</summary>
    public static async Task<PriceCatalog> Fetch(Uri? baseUrl = null, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var client = http ?? ApiClient.SharedHttp;
        var root = (baseUrl ?? new Uri(ProviderId.OpenRouter.DefaultBaseUrl())).AbsoluteUri.TrimEnd('/');
        var models = new Dictionary<string, ModelPrice>();
        foreach (var url in new[] { root + "/models", root + "/models?output_modalities=transcription" })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (!ApiClient.IsSuccess(response.StatusCode)) throw ApiException.Http((int)response.StatusCode, ApiClient.ErrorMessage(data));
            foreach (var (id, price) in ParseModels(data)) models[id] = price;
        }
        if (models.Count == 0) throw ApiException.BadResponse("models list");
        return new PriceCatalog(DateTimeOffset.UtcNow, models);
    }

    public string ToJson()
    {
        var models = new JsonObject();
        foreach (var (id, price) in Models.OrderBy(m => m.Key, StringComparer.Ordinal)) models[id] = price.ToJson();
        var root = new JsonObject
        {
            ["fetchedAt"] = FetchedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["models"] = models,
        };
        return root.ToJsonString();
    }

    public static PriceCatalog? FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["models"] is not JsonObject list) return null;
            if (!ApiClient.TryString(root["fetchedAt"], out var stamp)
                || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fetchedAt)) return null;
            var models = new Dictionary<string, ModelPrice>();
            foreach (var (id, node) in list)
            {
                if (ModelPrice.FromJson(node) is { } price) models[id] = price;
            }
            return new PriceCatalog(fetchedAt, models);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>How the Home page writes money, durations and counts.</summary>
public static class UsageFormat
{
    /// <summary>"$0", "&lt; $0.0001", "$0.0042", "$0.034", "$1.20", "$1,234.50": enough digits to see small amounts.</summary>
    public static string Money(double usd)
    {
        if (usd <= 0) return "$0";
        if (usd < 0.0001) return "< $0.0001";
        var format = usd < 0.01 ? "#,0.0000" : usd < 1 ? "#,0.000" : "#,0.00";
        return "$" + usd.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>A price per million tokens or per minute: "$0.75", "$0.006", "$0" (no rounding up to cents).</summary>
    public static string Rate(double usd) =>
        "$" + usd.ToString(usd >= 1 ? "#,0.##" : "0.######", CultureInfo.InvariantCulture);

    /// <summary>"45 s", "12 min", "3 h 12 min", "26 h" (Chinese: "45 秒", "12 分钟", "3 小时 12 分钟").</summary>
    public static string Duration(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        if (total < 60) return L($"{total} 秒", $"{total} s");
        var minutes = total / 60;
        if (minutes < 60) return L($"{minutes} 分钟", $"{minutes} min");
        var hours = minutes / 60;
        var rest = minutes % 60;
        if (hours >= 24 || rest == 0) return L($"{hours} 小时", $"{hours} h");
        return L($"{hours} 小时 {rest} 分钟", $"{hours} h {rest} min");
    }

    /// <summary>"12,345"</summary>
    public static string Count(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}
