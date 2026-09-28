using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TypelessCore;

/// <summary>Evaluation-only accounting. The normal app request and retry path is deliberately untouched.</summary>
public sealed record EvaluationUsage
{
    [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int? TotalTokens { get; set; }
    [JsonPropertyName("cost")] public double? Cost { get; set; }
}

public sealed class EvaluationAttempt
{
    [JsonPropertyName("requestID")] public string? RequestId { get; set; }
    [JsonPropertyName("responseModel")] public string? ResponseModel { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("usage")] public EvaluationUsage? Usage { get; set; }
    [JsonPropertyName("costSource")] public string? CostSource { get; set; }
    [JsonPropertyName("finishReason")] public string? FinishReason { get; set; }
    [JsonPropertyName("httpStatus")] public int? HttpStatus { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("billingErrors")] public List<string> BillingErrors { get; set; } = [];
    [JsonPropertyName("rawOutput")] public string RawOutput { get; set; } = "";
    [JsonPropertyName("output")] public string Output { get; set; } = "";
    [JsonPropertyName("seconds")] public double Seconds { get; set; }
    /// <summary>Time to the first content token: what a hedged request waits on.</summary>
    [JsonPropertyName("firstTokenSeconds")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FirstTokenSeconds { get; set; }
    [JsonPropertyName("retryCount")] public int RetryCount { get; set; }
    [JsonPropertyName("receivedDone")] public bool ReceivedDone { get; set; }
    [JsonIgnore] public bool Truncated => FinishReason == "length";

    public void Consume(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var payload = line[5..].Trim(' ', '\t');
        if (payload == "[DONE]")
        {
            ReceivedDone = true;
            return;
        }
        JsonObject? obj;
        try { obj = JsonNode.Parse(payload) as JsonObject; } catch (JsonException) { obj = null; }
        if (obj is null)
        {
            Error = "malformed SSE data";
            return;
        }
        if (ApiClient.TryString(obj["id"], out var id)) RequestId = id;
        if (ApiClient.TryString(obj["model"], out var model)) ResponseModel = model;
        if (ApiClient.TryString(obj["provider"], out var provider)) Provider = provider;
        if (obj["usage"] is JsonObject u && ParseUsage(u) is { } parsed)
        {
            Usage = parsed;
            if (parsed.Cost is { } cost && double.IsFinite(cost) && cost >= 0) CostSource = "stream.usage.cost";
            else Usage.Cost = null;
        }
        foreach (var e in new SseParser().Parse(line))
        {
            switch (e)
            {
                case SseEvent.Content c: RawOutput += c.Text; break;
                case SseEvent.Finished f: FinishReason = f.Reason; break;
                case SseEvent.Error err: Error = err.Message; break;
                case SseEvent.Done: ReceivedDone = true; break;
            }
        }
    }

    /// <summary>All-or-nothing, like decoding the Codable struct: a field of the wrong type discards the usage.</summary>
    private static EvaluationUsage? ParseUsage(JsonObject u)
    {
        static bool Int(JsonNode? n, out int? value)
        {
            value = null;
            if (n is null) return true;
            if (n is JsonValue v && v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
            {
                value = (int)d;
                return true;
            }
            return false;
        }
        double? cost = null;
        if (u["cost"] is { } c)
        {
            if (c is JsonValue cv && cv.TryGetValue<double>(out var d)) cost = d; else return null;
        }
        if (!Int(u["prompt_tokens"], out var p) || !Int(u["completion_tokens"], out var cmp) || !Int(u["total_tokens"], out var t)) return null;
        return new EvaluationUsage { PromptTokens = p, CompletionTokens = cmp, TotalTokens = t, Cost = cost };
    }
}

public sealed record EvaluationPlan(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("bodyJSON")] string BodyJson,
    [property: JsonPropertyName("upperBoundUSD")] double UpperBoundUsd,
    [property: JsonPropertyName("inputTokenBound")] int InputTokenBound,
    [property: JsonPropertyName("outputTokenBound")] int OutputTokenBound);

public sealed partial class ApiClient
{
    /// <summary>Reuses the production prompt/body, adding only evaluation routing price/supported-parameter guards.</summary>
    public EvaluationPlan EvaluationPlan(string transcript, PolishOptions options, double promptPrice, double completionPrice, string? providerTag = null,
                                         JsonObject? providerRouting = null)
    {
        if (!IsOpenRouter || !double.IsFinite(promptPrice) || !double.IsFinite(completionPrice) || promptPrice < 0 || completionPrice < 0
            || options.ModelInfo?.SupportedParameters.Contains("max_tokens") != true)
        {
            throw ApiException.BadResponse("evaluation requires verified prices and max_tokens support");
        }
        // Same request and routing as the app, so latency and reliability match what users see.
        // (Price caps here once pinned models to overloaded cheap endpoints and skewed the results.)
        var body = PolishBody(transcript, options, includeOptional: true);
        // Byte-level upper estimate plus ample chat-template overhead; 20% billing safety margin.
        var messages = (JsonArray)body["messages"]!;
        if (providerRouting != null)
        {
            // Comparing routing preferences (e.g. sorting by latency vs throughput).
            body["provider"] = providerRouting.DeepClone();
        }
        if (providerTag != null)
        {
            // Explicitly benchmarking a single upstream provider.
            body["provider"] = new JsonObject { ["only"] = new JsonArray(providerTag), ["allow_fallbacks"] = false };
        }
        var inputBound = messages.Aggregate(2048, (sum, m) => sum + Encoding.UTF8.GetByteCount((string?)m!["content"] ?? ""));
        var outputBound = (int)body["max_tokens"]!;
        var upper = (inputBound * promptPrice + outputBound * completionPrice) * 1.2;
        var json = JsonSerializer.Serialize(SortKeys(body), BodyJson);
        return new EvaluationPlan(options.Model, json, upper, inputBound, outputBound);
    }

    private static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                                            .Select(p => KeyValuePair.Create(p.Key, SortKeys(p.Value)))),
        JsonArray array => new JsonArray(array.Select(SortKeys).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    /// <summary>One attempt only: failures and missing billing remain visible, with no hidden retries.</summary>
    public async Task<EvaluationAttempt> EvaluatePolish(EvaluationPlan plan, CancellationToken cancellationToken = default)
    {
        var result = new EvaluationAttempt();
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = Request("chat/completions", JsonContent(JsonNode.Parse(plan.BodyJson)!));
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            using var idle = new IdleTimer(25, cancellationToken);
            using var response = await idle.Run(t => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, t)).ConfigureAwait(false);
            result.HttpStatus = (int)response.StatusCode;
            if (response.Headers.TryGetValues("X-Generation-Id", out var ids)) result.RequestId = ids.FirstOrDefault();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (!IsSuccess(response.StatusCode))
            {
                var buffer = new byte[4096];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await idle.Run(t => stream.ReadAsync(buffer.AsMemory(read), t).AsTask()).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
                throw ApiException.Http((int)response.StatusCode, ErrorMessage(buffer.AsSpan(0, read)));
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await idle.Run(t => reader.ReadLineAsync(t).AsTask()).ConfigureAwait(false) is { } line)
            {
                result.Consume(line);
                if (result.FirstTokenSeconds == null && result.RawOutput.Length > 0) result.FirstTokenSeconds = clock.Elapsed.TotalSeconds;
                if (result.ReceivedDone) break;
            }
            if (!result.ReceivedDone && result.Error == null) result.Error = "stream ended without DONE";
            if (result.FinishReason == null && result.Error == null) result.Error = "missing finish_reason";
            result.Output = Prompts.SanitizePolishOutput(result.RawOutput);
            if (result.Output.Length == 0 && result.Error == null) result.Error = "empty output";
        }
        catch (Exception error)
        {
            result.Error = error.Message;
        }
        result.Seconds = clock.Elapsed.TotalSeconds;
        // Billing lookup is excluded from user-visible completion latency. Never infer zero cost.
        if (result.Usage?.Cost == null && result.RequestId is { } id)
        {
            foreach (var delay in new[] { 0.5, 1.5, 3.0 })
            {
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                try
                {
                    using var request = Request("generation");
                    request.Method = HttpMethod.Get;
                    request.RequestUri = new Uri(Url("generation") + "?id=" + Uri.EscapeDataString(id));
                    var (status, data) = await Send(request, 15, cancellationToken).ConfigureAwait(false);
                    JsonObject? bill = null;
                    try { bill = (JsonNode.Parse(data) as JsonObject)?["data"] as JsonObject; } catch (JsonException) { }
                    if ((int)status != 200 || bill is null || bill["total_cost"] is not JsonValue costValue
                        || !costValue.TryGetValue<double>(out var cost) || !double.IsFinite(cost) || cost < 0)
                    {
                        throw ApiException.BadResponse("generation billing unavailable");
                    }
                    int? IntValue(string key) => bill[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
                    result.Usage = new EvaluationUsage
                    {
                        PromptTokens = IntValue("native_tokens_prompt"),
                        CompletionTokens = IntValue("native_tokens_completion"),
                        Cost = cost,
                    };
                    result.CostSource = "generation.total_cost";
                    if (ApiClient.TryString(bill["provider_name"], out var providerName)) result.Provider = providerName;
                    break;
                }
                catch (Exception error)
                {
                    result.BillingErrors.Add(error.Message);
                }
            }
        }
        return result;
    }
}

/// <summary>
/// Durable reservations survive cancellation/crashes. Holds an exclusive process lock for the run.
/// Unknown costs keep their entire reservation, including HTTP errors with no generation ID.
/// </summary>
public sealed class EvaluationBudget : IDisposable
{
    public sealed class Entry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("upperBound")] public double UpperBound { get; set; }
        [JsonPropertyName("cost")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Cost { get; set; }
        [JsonPropertyName("requestID")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RequestId { get; set; }
    }

    public sealed class LedgerState
    {
        [JsonPropertyName("limit")] public double Limit { get; set; }
        [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = [];
        [JsonIgnore] public double Spent => Entries.Where(e => e.Cost.HasValue).Sum(e => e.Cost!.Value);
        [JsonIgnore] public double Reserved => Entries.Where(e => e.Cost == null).Sum(e => e.UpperBound);
    }

    public LedgerState State { get; private set; }
    private readonly string _path;
    private readonly FileStream _lockFile;

    public EvaluationBudget(string path, double limit)
    {
        if (!(limit > 0 && limit <= 2 && double.IsFinite(limit))) throw ApiException.BadResponse("budget must be in (0, 2]");
        _path = path;
        try
        {
            _lockFile = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw ApiException.BadResponse("another evaluation owns this budget");
        }
        catch (UnauthorizedAccessException)
        {
            throw ApiException.BadResponse("cannot open budget lock");
        }
        try
        {
            if (File.Exists(path))
            {
                State = JsonSerializer.Deserialize<LedgerState>(File.ReadAllBytes(path)) ?? throw ApiException.BadResponse("unreadable budget ledger");
                if (State.Limit != limit) throw ApiException.BadResponse("cannot change existing budget limit");
            }
            else
            {
                State = new LedgerState { Limit = limit };
            }
        }
        catch
        {
            _lockFile.Dispose();
            throw;
        }
    }

    public void Dispose() => _lockFile.Dispose();

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(State));
        File.Move(temp, _path, overwrite: true);
    }

    public void Check(double next, double future = 0)
    {
        if (!(double.IsFinite(next) && double.IsFinite(future) && next >= 0 && future >= 0
              && State.Spent + State.Reserved + next + future <= State.Limit))
        {
            throw ApiException.BadResponse("budget exhausted: paid + unresolved/in-flight + next + future exceeds cap");
        }
    }

    public string Reserve(double upper, double future = 0)
    {
        Check(upper, future);
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        State.Entries.Add(new Entry { Id = id, UpperBound = upper });
        Save(); // Must be durable BEFORE any network request.
        return id;
    }

    public void Settle(string id, double? cost, string? requestId)
    {
        var entry = State.Entries.FirstOrDefault(e => e.Id == id) ?? throw ApiException.BadResponse("unknown reservation");
        if (cost is { } c && (!double.IsFinite(c) || c < 0)) throw ApiException.BadResponse("invalid billed cost");
        entry.Cost = cost;
        entry.RequestId = requestId;
        Save();
        if (cost is { } billed && billed > entry.UpperBound) throw ApiException.BadResponse("billing exceeded conservative bound; stop and audit");
    }
}

/// <summary>Case-independent measures of how much a clean-up changed the speaker's text.</summary>
public static class PolishMetrics
{
    /// <summary>Fillers and correction phrases the clean-up is supposed to remove.</summary>
    internal static readonly HashSet<string> RemovableLatin =
    [
        "um", "uh", "umm", "uhh", "er", "ah", "oh", "like", "so", "ok", "okay", "yeah", "well",
        "actually", "basically", "mean", "you", "know", "wait", "no", "sorry", "scratch", "that",
    ];

    /// <summary>
    /// Share of the English words in a mostly-Chinese input that survive in the output. Translating
    /// "dark mode" to 深色模式 lowers it; recognition fixes like "swift UI" → SwiftUI don't.
    /// Returns null when the input is mostly English or has no English words worth checking.
    /// </summary>
    public static double? LatinRetention(string input, string output, IEnumerable<string>? ignoring = null)
    {
        // A Chinese character is roughly one word: compare counts to find the host language.
        var cjk = input.EnumerateRunes().Count(TranscriptJoiner.IsCJK);
        if (cjk < LatinWords(input).Count) return null;
        var ignored = (ignoring ?? []).SelectMany(LatinWords).ToHashSet();
        var words = LatinWords(input).ToHashSet();
        words.ExceptWith(RemovableLatin);
        words.ExceptWith(ignored);
        words.RemoveWhere(w => w.Length <= 1);
        if (words.Count == 0) return null;
        var haystack = new string(output.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return (double)words.Count(w => haystack.Contains(w, StringComparison.Ordinal)) / words.Count;
    }

    /// <summary>
    /// Character-level similarity (1 − normalised edit distance) after dropping whitespace and
    /// punctuation, so spacing and punctuation fixes don't count as edits.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        var x = Normalized(a);
        var y = Normalized(b);
        if (x.Count == 0 || y.Count == 0) return x.Count == y.Count ? 1 : 0;
        return 1 - (double)EditDistance(x, y) / Math.Max(x.Count, y.Count);
    }

    internal static int EditDistance<T>(IReadOnlyList<T> x, IReadOnlyList<T> y)
    {
        var comparer = EqualityComparer<T>.Default;
        var previous = Enumerable.Range(0, y.Count + 1).ToArray();
        for (var i = 1; i <= x.Count; i++)
        {
            var current = new int[y.Count + 1];
            current[0] = i;
            for (var j = 1; j <= y.Count; j++)
            {
                current[j] = comparer.Equals(x[i - 1], y[j - 1])
                    ? previous[j - 1]
                    : 1 + Math.Min(previous[j - 1], Math.Min(previous[j], current[j - 1]));
            }
            previous = current;
        }
        return previous[y.Count];
    }

    internal static List<string> LatinWords(string text) =>
        text.ToLowerInvariant()
            .Split(c => !(char.IsAscii(c) && char.IsLetterOrDigit(c)))
            .Where(w => w.Length > 0 && char.IsLetter(w[0]))
            .ToList();

    internal static List<Rune> Normalized(string text) =>
        text.ToLowerInvariant().EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r) && Rune.GetUnicodeCategory(r) switch
        {
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol => false,
            _ => true,
        }).ToList();

    /// <summary>Splits wherever <paramref name="separator"/> matches, like Swift's <c>split(whereSeparator:)</c>.</summary>
    internal static IEnumerable<string> Split(this string text, Func<char, bool> separator)
    {
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && !separator(text[i])) continue;
            if (i > start) yield return text[start..i];
            start = i + 1;
        }
    }
}
