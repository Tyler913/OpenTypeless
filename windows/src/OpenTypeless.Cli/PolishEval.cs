using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.Cli;

/// <summary>
/// Isolated OpenRouter benchmark; never touches the app's settings or credential store unless asked to.
/// See eval/README.md for budget, output privacy, repetition and dry-run options.
/// </summary>
public static class PolishEval
{
    public sealed class Case
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("input")] public string Input { get; set; } = "";
        [JsonPropertyName("mustContain")] public List<string>? MustContain { get; set; }
        [JsonPropertyName("mustNotContain")] public List<string>? MustNotContain { get; set; }
        [JsonPropertyName("anyOf")] public List<List<string>>? AnyOf { get; set; }
    }

    public sealed record Job(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("round")] int Round,
        [property: JsonPropertyName("plan")] EvaluationPlan Plan);

    public sealed record Result(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("round")] int Round,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("reservationID")] string ReservationId,
        [property: JsonPropertyName("parameters")] EvaluationPlan Parameters,
        [property: JsonPropertyName("attempt")] EvaluationAttempt Attempt,
        [property: JsonPropertyName("failures")] List<string> Failures,
        [property: JsonPropertyName("latinRetention")] double? LatinRetention,
        [property: JsonPropertyName("similarity")] double? Similarity);

    private static readonly JsonSerializerOptions OutputJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> Run(string[] args)
    {
        string? Value(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        void Save<T>(T value, string path)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, OutputJson));
            File.Move(temp, path, overwrite: true);
        }

        try
        {
            if ((Value("--provider") ?? "openrouter") != "openrouter"
                || Value("--eval-polish") is not { } casesPath || Value("--out") is not { } outPath
                || (Value("--models") ?? Value("--model")) is not { } modelNames)
            {
                throw ApiException.BadResponse("required: --eval-polish CASES --models IDS --out PRIVATE_PATH; OpenRouter only");
            }
            // All detailed artifacts must live outside the working repository, because they can contain your text.
            // This file is windows/src/OpenTypeless.Cli/PolishEval.cs, three folders below the repository root.
            var repository = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "..", ".."));
            foreach (var path in new[] { outPath, Value("--budget-ledger") }.OfType<string>())
            {
                var resolved = ResolveLinks(Path.GetFullPath(path));
                if (resolved.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || resolved.Equals(repository, StringComparison.OrdinalIgnoreCase))
                {
                    throw ApiException.BadResponse("detailed output / budget ledger must be outside repository");
                }
            }
            var cases = JsonSerializer.Deserialize<List<Case>>(await File.ReadAllBytesAsync(casesPath)) ?? [];
            if (cases.Count == 0 || cases.Select(c => c.Id).Distinct().Count() != cases.Count)
            {
                throw ApiException.BadResponse("empty cases or duplicate IDs");
            }
            var rounds = int.TryParse(Value("--rounds") ?? "1", out var r) ? r : 0;
            if (rounds is < 1 or > 10) throw ApiException.BadResponse("invalid rounds");
            var models = modelNames.Split(',').ToList();
            if (models.Count == 0 || models.Any(string.IsNullOrEmpty) || models.Distinct().Count() != models.Count)
            {
                throw ApiException.BadResponse("duplicate/empty models");
            }
            var dryRun = args.Contains("--dry-run");
            // Opt-in: `--key-from-credential-store` uses the key saved by the app instead of the environment.
            var key = args.Contains("--key-from-credential-store") || args.Contains("--key-from-keychain")
                ? Credentials.Load().GetValueOrDefault("openrouter", "")
                : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
            if (!dryRun && key.Trim().Length == 0) throw ApiException.MissingApiKey("OPENROUTER_API_KEY environment variable");

            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
            var client = new ApiClient(ProviderEndpoint.OpenRouter(key), http);
            byte[] catalogData;
            if (Value("--catalog") is { } catalogPath)
            {
                catalogData = await File.ReadAllBytesAsync(catalogPath);
            }
            else
            {
                using var response = await http.GetAsync("https://openrouter.ai/api/v1/models");
                if ((int)response.StatusCode != 200) throw ApiException.BadResponse("catalog unavailable");
                catalogData = await response.Content.ReadAsByteArrayAsync();
            }
            var catalog = (JsonNode.Parse(catalogData) as JsonObject)?["data"] as JsonArray ?? [];
            var prompt = Value("--prompt-file") is { } promptFile ? await File.ReadAllTextAsync(promptFile) : null;
            var providerTag = Value("--provider-tag");
            JsonObject? providerRouting = null;
            if (Value("--provider-routing") is { } routing)
            {
                try { providerRouting = JsonNode.Parse(routing) as JsonObject; } catch (JsonException) { }
                if (providerRouting == null) throw ApiException.BadResponse("--provider-routing must be a JSON object");
            }
            JsonObject? endpointQuote = null;
            if (providerTag != null)
            {
                var endpoints = Value("--endpoint-catalog") is { } endpointPath
                    ? ((JsonNode.Parse(await File.ReadAllBytesAsync(endpointPath)) as JsonObject)?["data"] as JsonObject)?["endpoints"] as JsonArray
                    : null;
                endpointQuote = models.Count == 1
                    ? endpoints?.OfType<JsonObject>().FirstOrDefault(e => Str(e["tag"]) == providerTag && Str(e["model_id"]) == models[0]
                                                                         && e["status"] is JsonValue s && s.TryGetValue<int>(out var status) && status == 0)
                    : null;
                if (endpointQuote == null)
                {
                    throw ApiException.BadResponse("explicit route requires a verified healthy endpoint quote for one model");
                }
            }

            var options = new Dictionary<string, PolishOptions>();
            var prices = new Dictionary<string, (double Prompt, double Completion)>();
            var tierThresholds = new Dictionary<string, int>();
            foreach (var model in models)
            {
                var row = catalog.OfType<JsonObject>().FirstOrDefault(m => Str(m["id"]) == model);
                var pricing = (endpointQuote ?? row)?["pricing"] as JsonObject;
                if (row == null || pricing == null
                    || !double.TryParse(Str(pricing["prompt"]), System.Globalization.CultureInfo.InvariantCulture, out var ip)
                    || !double.TryParse(Str(pricing["completion"]), System.Globalization.CultureInfo.InvariantCulture, out var op)
                    || (double.TryParse(Str(pricing["request"]) ?? "0", System.Globalization.CultureInfo.InvariantCulture, out var request) ? request : -1) != 0)
                {
                    throw ApiException.BadResponse($"model absent or unverifiable pricing: {model}");
                }
                var reasoning = row["reasoning"] as JsonObject;
                var info = new ModelInfo(model, model,
                    Strings((endpointQuote ?? row)["supported_parameters"]),
                    reasoning?["mandatory"] is JsonValue m && m.TryGetValue<bool>(out var mandatory) && mandatory,
                    Strings(reasoning?["supported_efforts"]));
                options[model] = new PolishOptions(model, ModelInfo: info, SystemPromptOverride: prompt);
                prices[model] = (ip, op);
                var thresholds = (pricing["overrides"] as JsonArray)?.OfType<JsonObject>()
                    .Select(o => o["min_prompt_tokens"] is JsonValue v && v.TryGetValue<int>(out var t) ? t : (int?)null)
                    .OfType<int>().ToList();
                if (thresholds is { Count: > 0 }) tierThresholds[model] = thresholds.Min();
            }

            var jobs = new List<Job>();
            // Latin-style rotation per case AND round, all requests sequential on a reused connection.
            for (var round = 1; round <= rounds; round++)
            {
                for (var index = 0; index < cases.Count; index++)
                {
                    var c = cases[index];
                    var offset = (index + round - 1) % models.Count;
                    for (var i = 0; i < models.Count; i++)
                    {
                        var model = models[(i + offset) % models.Count];
                        var price = prices[model];
                        var plan = client.EvaluationPlan(c.Input, options[model], price.Prompt, price.Completion, providerTag, providerRouting);
                        if (tierThresholds.TryGetValue(model, out var threshold) && plan.InputTokenBound >= threshold)
                        {
                            throw ApiException.BadResponse("input crosses unbudgeted price tier");
                        }
                        jobs.Add(new Job(c.Id, round, plan));
                    }
                }
            }
            if (dryRun)
            {
                Save(jobs, outPath);
                Cli.Log($"dry-run: {jobs.Count} requests, conservative upper ${jobs.Sum(j => j.Plan.UpperBoundUsd):0.000000}");
                return 0;
            }
            if (Value("--budget-ledger") is not { } ledgerPath
                || !double.TryParse(Value("--budget-usd") ?? "2", System.Globalization.CultureInfo.InvariantCulture, out var budgetLimit)
                || !double.TryParse(Value("--future-reserve-usd") ?? "0", System.Globalization.CultureInfo.InvariantCulture, out var future)
                || future < 0)
            {
                throw ApiException.BadResponse("required: --budget-ledger PRIVATE_PATH; invalid budget/reserve");
            }
            if (File.Exists(outPath)) throw ApiException.BadResponse("output exists; use a new stage filename to avoid losing evidence");
            using var budget = new EvaluationBudget(ledgerPath, budgetLimit);
            budget.Check(jobs.Sum(j => j.Plan.UpperBoundUsd), future);
            var results = new List<Result>();
            Save(results, outPath);
            foreach (var job in jobs)
            {
                var c = cases.First(x => x.Id == job.Id);
                var reservation = budget.Reserve(job.Plan.UpperBoundUsd, future);
                var attempt = await client.EvaluatePolish(job.Plan);
                var failures = new List<string>();
                foreach (var s in c.MustContain ?? []) if (!attempt.Output.Contains(s)) failures.Add($"missing “{s}”");
                foreach (var s in c.MustNotContain ?? []) if (attempt.Output.Contains(s)) failures.Add($"still has “{s}”");
                foreach (var g in c.AnyOf ?? []) if (!g.Any(attempt.Output.Contains)) failures.Add($"none of [{string.Join(", ", g)}]");
                if (Prompts.LooksLikeAnAnswer(c.Input, attempt.Output)) failures.Add("looks like an answer");
                if (attempt.Truncated) failures.Add("truncated");
                if (attempt.Error != null) failures.Add("request error; see private attempt");
                var ok = attempt.Error == null && attempt.Output.Length > 0;
                results.Add(new Result(c.Id, job.Round, job.Plan.Model, reservation, job.Plan, attempt, failures,
                                       ok ? PolishMetrics.LatinRetention(c.Input, attempt.Output, c.MustNotContain) : null,
                                       ok ? PolishMetrics.Similarity(c.Input, attempt.Output) : null));
                Save(results, outPath);
                budget.Settle(reservation, attempt.Usage?.Cost, attempt.RequestId);
                Cli.Log($"{results.Count}/{jobs.Count} {job.Plan.Model} r{job.Round} {(failures.Count == 0 ? "PASS" : "FAIL")} {attempt.Seconds:0.00}s" +
                        $" · paid ${budget.State.Spent:0.000000} · unresolved ${budget.State.Reserved:0.000000}");
            }
            Summarize(results, models);
            return results.All(x => x.Failures.Count == 0) ? 0 : 2;
        }
        catch (Exception error)
        {
            Cli.Log($"evaluation stopped: {error.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Per-model totals: checks passed, how much of the speaker's English and wording survived, and
    /// first-token / total latency percentiles.
    /// </summary>
    private static void Summarize(List<Result> results, List<string> models)
    {
        static string Mean(List<double> xs) => xs.Count == 0 ? "-" : xs.Average().ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        static string Pct(List<double> xs, double p)
        {
            if (xs.Count == 0) return "-";
            var sorted = xs.Order().ToList();
            return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }
        foreach (var model in models)
        {
            var rows = results.Where(r => r.Model == model).ToList();
            var ttft = rows.Select(r => r.Attempt.FirstTokenSeconds).OfType<double>().ToList();
            var total = rows.Where(r => r.Attempt.Error == null).Select(r => r.Attempt.Seconds).ToList();
            Cli.Log($"{model}: pass {rows.Count(r => r.Failures.Count == 0)}/{rows.Count}"
                    + $" · English kept {Mean(rows.Select(r => r.LatinRetention).OfType<double>().ToList())}"
                    + $" · similarity {Mean(rows.Select(r => r.Similarity).OfType<double>().ToList())}"
                    + $" · first token p50 {Pct(ttft, 0.5)} p90 {Pct(ttft, 0.9)}"
                    + $" · total p50 {Pct(total, 0.5)} p90 {Pct(total, 0.9)}");
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str).OfType<string>().ToList() : [];

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>Resolves symbolic links / junctions on the path and its parents.</summary>
    private static string ResolveLinks(string path)
    {
        static FileSystemInfo? Target(FileSystemInfo item)
        {
            try { return item.Exists && item.LinkTarget != null ? item.ResolveLinkTarget(true) : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        var info = new FileInfo(path);
        if (Target(info) is { } target) return target.FullName;
        var directory = info.Directory;
        var suffix = info.Name;
        while (directory?.Parent != null) // the drive root is never a link
        {
            if (Target(directory) is { } resolved) return Path.Combine(resolved.FullName, suffix);
            suffix = Path.Combine(directory.Name, suffix);
            directory = directory.Parent;
        }
        return path;
    }
}
