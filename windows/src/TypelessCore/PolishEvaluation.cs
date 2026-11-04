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
    [property: JsonPropertyName("outputTokenBound")] int OutputTokenBound,
    /// <summary>The request carries text around the cursor, so its output is cleaned of that as the app's is.</summary>
    [property: JsonPropertyName("sentContext")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SentContext = false);

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
        return new EvaluationPlan(options.Model, json, upper, inputBound, outputBound, Prompts.ContextBlock(options.Context) != null);
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
            result.Output = Prompts.SanitizePolishOutput(result.RawOutput, sentContext: plan.SentContext);
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
        // A shared start and end never change the distance, and after a small fix to a long text they are nearly all
        // of it: only the part in between needs the quadratic table (the learner runs this on the UI thread).
        var start = 0;
        while (start < x.Count && start < y.Count && comparer.Equals(x[start], y[start])) start++;
        var end = 0;
        while (end < x.Count - start && end < y.Count - start && comparer.Equals(x[x.Count - 1 - end], y[y.Count - 1 - end])) end++;
        int n = x.Count - start - end, m = y.Count - start - end;
        var previous = Enumerable.Range(0, m + 1).ToArray();
        var current = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            current[0] = i;
            for (var j = 1; j <= m; j++)
            {
                current[j] = comparer.Equals(x[start + i - 1], y[start + j - 1])
                    ? previous[j - 1]
                    : 1 + Math.Min(previous[j - 1], Math.Min(previous[j], current[j - 1]));
            }
            (previous, current) = (current, previous);
        }
        return previous[m];
    }

    /// <summary>Four letters in a row of a normalised text.</summary>
    internal const int GramLength = 4;

    /// <summary>Every run of four letters, by where it starts.</summary>
    internal static List<(int, int, int, int)> Grams(int[] text)
    {
        var grams = new List<(int, int, int, int)>();
        for (var i = 0; i + GramLength <= text.Length; i++) grams.Add((text[i], text[i + 1], text[i + 2], text[i + 3]));
        return grams;
    }

    /// <summary>
    /// One place where <c>y</c> differs from <c>x</c>: the letters of <c>y</c> there (positions <c>Start</c> up to
    /// <c>End</c>), and how many of <c>x</c>'s it replaces.
    /// </summary>
    internal readonly record struct Edit(int Start, int End, int Replaced);

    /// <summary>
    /// Where <paramref name="y"/> differs from <paramref name="x"/>, by an edit-distance alignment of the two: each run of
    /// changes between letters they share. Null for texts too long to align here (over <paramref name="maxCells"/> pairs
    /// of letters, a table that size in bytes).
    /// </summary>
    internal static List<Edit>? Edits(int[] x, int[] y, long maxCells = 8_000_000)
    {
        var start = 0;
        while (start < x.Length && start < y.Length && x[start] == y[start]) start++;
        var end = 0;
        while (end < x.Length - start && end < y.Length - start && x[x.Length - 1 - end] == y[y.Length - 1 - end]) end++;
        int n = x.Length - start - end, m = y.Length - start - end;
        if ((long)n * m > maxCells) return null;
        // How each cell was reached: 0 the same letter, 1 another letter in its place, 2 a letter of x left out, 3 a letter
        // of y added. Ties go in that order, as on macOS.
        var moves = new byte[(n + 1) * (m + 1)];
        var previous = Enumerable.Range(0, m + 1).ToArray();
        var current = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            current[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var same = x[start + i - 1] == y[start + j - 1];
                int diagonal = previous[j - 1] + (same ? 0 : 1), leftOut = previous[j] + 1, added = current[j - 1] + 1;
                if (diagonal <= leftOut && diagonal <= added)
                {
                    current[j] = diagonal;
                    moves[i * (m + 1) + j] = same ? (byte)0 : (byte)1;
                }
                else if (leftOut <= added)
                {
                    current[j] = leftOut;
                    moves[i * (m + 1) + j] = 2;
                }
                else
                {
                    current[j] = added;
                    moves[i * (m + 1) + j] = 3;
                }
            }
            (previous, current) = (current, previous);
        }
        // Back from the end; a run of changes ends at the next shared letter.
        var runs = new List<Edit>();
        int row = n, column = m;
        int? runEnd = null;
        var replaced = 0;
        while (row > 0 || column > 0)
        {
            int move = row == 0 ? 3 : column == 0 ? 2 : moves[row * (m + 1) + column];
            if (move == 0)
            {
                if (runEnd is { } runStop) runs.Add(new Edit(start + column, start + runStop, replaced));
                runEnd = null;
                replaced = 0;
                row--;
                column--;
                continue;
            }
            runEnd ??= column;
            switch (move)
            {
                case 1:
                    row--;
                    column--;
                    replaced++;
                    break;
                case 2:
                    row--;
                    replaced++;
                    break;
                default:
                    column--;
                    break;
            }
        }
        if (runEnd is { } last) runs.Add(new Edit(start, start + last, replaced));
        runs.Reverse();
        return runs;
    }

    /// <summary><see cref="Normalized"/>, as Unicode scalar values.</summary>
    internal static int[] Scalars(string text) => Normalized(text).Select(r => r.Value).ToArray();

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

/// <summary>
/// One case of an evaluation set (eval/*.json) and the checks its output must pass: the strings it must and must not
/// contain, and, where the case asks, how it's laid out (paragraphs, list items) and how long it is. Every output is also
/// checked for stock phrases the speaker didn't say (<see cref="PolishChecks.StockPhrases"/>). The same as the macOS
/// app's, so a set passes or fails alike on both.
/// </summary>
public sealed class PolishCase
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("input")] public string Input { get; set; } = "";
    [JsonPropertyName("mustContain")] public List<string>? MustContain { get; set; }
    [JsonPropertyName("mustNotContain")] public List<string>? MustNotContain { get; set; }
    [JsonPropertyName("anyOf")] public List<List<string>>? AnyOf { get; set; }
    /// <summary>Translate into this language (a <see cref="TranslationTarget"/> id) instead of cleaning up, as translation mode does.</summary>
    [JsonPropertyName("translateTo")] public string? TranslateTo { get; set; }
    /// <summary>Text around the cursor sent with the transcript (see <see cref="CursorContext"/>), as the app would.</summary>
    [JsonPropertyName("context")] public string? Context { get; set; }
    /// <summary>The layout of the app it's dictated into (a <see cref="TextLayout"/> id); the prompt's own when absent.</summary>
    [JsonPropertyName("layout")] public string? Layout { get; set; }
    /// <summary>The speech-to-text language set ("" or absent auto-detects), which picks the examples sent with it.</summary>
    [JsonPropertyName("language")] public string? Language { get; set; }
    /// <summary>How many paragraphs the output may have, [least, most] (see <see cref="TextShape"/>).</summary>
    [JsonPropertyName("paragraphs")] public List<int>? Paragraphs { get; set; }
    /// <summary>How many list items the output may have, [least, most]; [0, 0] for none.</summary>
    [JsonPropertyName("listItems")] public List<int>? ListItems { get; set; }
    /// <summary>
    /// The output's length as a share of the input's, [least, most], once the input's fillers and the strings the output
    /// must not contain are left out (see <see cref="PolishChecks.LengthRatio"/>): [0.8, 1.2] for a clean-up that only
    /// mends.
    /// </summary>
    [JsonPropertyName("length")] public List<double>? Length { get; set; }

    [JsonIgnore] public TextLayout TextLayout => TextLayouts.From(Layout) ?? TextLayout.Natural;

    /// <summary>What's wrong with <paramref name="output"/> as this case's answer; empty when nothing is.</summary>
    public List<string> Failures(string output)
    {
        var failures = new List<string>();
        foreach (var s in MustContain ?? []) if (!output.Contains(s, StringComparison.Ordinal)) failures.Add($"missing “{s}”");
        foreach (var s in MustNotContain ?? []) if (output.Contains(s, StringComparison.Ordinal)) failures.Add($"still has “{s}”");
        foreach (var g in AnyOf ?? []) if (!g.Any(s => output.Contains(s, StringComparison.Ordinal))) failures.Add($"none of [{string.Join(", ", g)}]");
        var shape = TextShape.Of(output);
        if (Paragraphs is [var least, var most] && (shape.Paragraphs < least || shape.Paragraphs > most))
        {
            failures.Add($"{shape.Paragraphs} paragraphs, not {least}–{most}");
        }
        if (ListItems is [var fewest, var mostItems] && (shape.ListItems < fewest || shape.ListItems > mostItems))
        {
            failures.Add($"{shape.ListItems} list items, not {fewest}–{mostItems}");
        }
        if (Length is [var shortest, var longest])
        {
            var ratio = PolishChecks.LengthRatio(Input, output, MustNotContain ?? []);
            if (ratio < shortest || ratio > longest)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"length {ratio:0.00} of the input, not {shortest:0.00}–{longest:0.00}"));
            }
        }
        foreach (var phrase in PolishChecks.AddedStockPhrases(Input, output)) failures.Add($"stock phrase “{phrase}”");
        return failures;
    }
}

/// <summary>How a text is laid out: its paragraphs (blocks between blank lines with any text that isn't a list item) and list items.</summary>
public readonly record struct TextShape(int Paragraphs, int ListItems)
{
    public static TextShape Of(string text)
    {
        int paragraphs = 0, items = 0;
        var prose = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n').Append(""))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (prose) paragraphs++;
                prose = false;
            }
            else if (Prompts.ListMarkerLength(line) > 0) items++;
            else prose = true;
        }
        return new TextShape(paragraphs, items);
    }
}

/// <summary>The evaluation's checks of how a clean-up reads, beyond the strings a case names.</summary>
public static class PolishChecks
{
    /// <summary>
    /// Stock phrases of written-up text, lower-case, that a clean-up must never add (the prompt names the commonest): one
    /// in an output and not in its input fails the case, in any language.
    /// </summary>
    public static readonly string[] StockPhrases =
    [
        "值得一提的是", "综合来看", "总的来说", "总而言之", "进行优化", "需要注意的是", "值得注意的是",
        "it's worth noting", "it is worth noting", "in conclusion", "furthermore", "moreover", "overall,",
        "注目すべき", "総合的に見て", "まとめると",
        "주목할 만한", "결론적으로", "종합적으로",
        "cabe destacar", "en conclusión", "en resumen",
        "vale ressaltar", "vale destacar", "em conclusão", "em resumo",
        "il convient de noter", "en conclusion", "en résumé",
        "es ist erwähnenswert", "zusammenfassend", "abschließend",
        "стоит отметить", "в заключение", "подводя итог",
    ];

    /// <summary>The stock phrases in <paramref name="output"/> that weren't in <paramref name="input"/>.</summary>
    public static List<string> AddedStockPhrases(string input, string output)
    {
        var said = input.ToLowerInvariant().Replace('’', '\'');
        var written = output.ToLowerInvariant().Replace('’', '\'');
        return StockPhrases.Where(p => written.Contains(p, StringComparison.Ordinal) && !said.Contains(p, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Fillers that are never words of their own, wherever they stand (in Chinese, Japanese and Korean text they needn't be set off by spaces).</summary>
    internal static readonly string[] FillerRuns = ["嗯", "呃", "えーと", "えっと", "あのー", "ええと"];

    /// <summary>Fillers that count only as words of their own, lower-case ("um", but not the "um" in "summary").</summary>
    internal static readonly HashSet<string> FillerWords =
        ["um", "uh", "umm", "uhh", "erm", "ehm", "eh", "euh", "ähm", "äh", "음", "어", "ну", "э"];

    /// <summary>
    /// The length of <paramref name="output"/> as a share of <paramref name="input"/>'s, both in letters (see
    /// <see cref="PolishMetrics.Similarity"/>: white space and punctuation don't count), once the input's fillers and
    /// every one of <paramref name="removed"/> (the strings the output must not contain: replaced values, correction
    /// phrases) are left out of it: what a clean-up that only mends keeps.
    /// </summary>
    public static double LengthRatio(string input, string output, IEnumerable<string> removed)
    {
        var basis = input;
        foreach (var s in removed.Concat(FillerRuns)) if (s.Length > 0) basis = basis.Replace(s, "", StringComparison.Ordinal);
        var fillers = ExampleLanguage.Words(basis).Where(FillerWords.Contains).Sum(w => PolishMetrics.Normalized(w).Count);
        var letters = Math.Max(1, PolishMetrics.Normalized(basis).Count - fillers);
        return (double)PolishMetrics.Normalized(output).Count / letters;
    }
}
