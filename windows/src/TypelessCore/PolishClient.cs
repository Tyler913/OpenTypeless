using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypelessCore;

public sealed record PolishOptions(
    string Model,
    IReadOnlyList<string>? Vocabulary = null,
    string ExtraInstructions = "",
    /// <summary>OpenRouter model metadata from <c>/models</c>, used to switch reasoning off (or to its minimum).</summary>
    ModelInfo? ModelInfo = null,
    /// <summary>Replaces the built-in system prompt (used by the prompt evaluation tool).</summary>
    string? SystemPromptOverride = null,
    /// <summary>Earlier recognition errors the user fixed by hand, shown to the model as hints.</summary>
    IReadOnlyList<Correction>? Misheard = null,
    /// <summary>
    /// The OpenRouter host (an endpoint tag such as "groq" or "deepinfra/fp8") the user wants this model served by;
    /// null lets OpenRouter pick the fastest.
    /// </summary>
    string? Host = null,
    /// <summary>Translate into this language instead of cleaning up (see <see cref="Prompts.TranslateSystemPrompt"/>).</summary>
    TranslationTarget? Translation = null,
    /// <summary>
    /// The text around the speaker's cursor, sent after the transcript to spell names by (see <see cref="CursorContext"/>);
    /// null for none. A clean-up's only: a translation goes without it.
    /// </summary>
    string? Context = null,
    /// <summary>Called as a request that carries <c>Context</c> goes out (each attempt), so History can say it was sent.</summary>
    Action? OnContextSent = null);

public static class ReasoningConfig
{
    internal static readonly string[] EffortOrder = ["none", "minimal", "low", "medium", "high", "xhigh"];

    /// <summary>Reasoning adds seconds of latency and nothing to text clean-up, so turn it off or to the minimum.</summary>
    public static JsonObject? Body(ModelInfo? info)
    {
        if (info is null) return new JsonObject { ["exclude"] = true };
        if (!info.SupportedParameters.Contains("reasoning")) return null;
        if (info.ReasoningMandatory)
        {
            static int Rank(string effort) { var i = Array.IndexOf(EffortOrder, effort); return i < 0 ? 99 : i; }
            string? lowest = null;
            foreach (var effort in info.Efforts)
            {
                if (lowest is null || Rank(effort) < Rank(lowest)) lowest = effort;
            }
            if (lowest != null) return new JsonObject { ["effort"] = lowest, ["exclude"] = true };
            return new JsonObject { ["exclude"] = true };
        }
        if (info.Efforts.Contains("none")) return new JsonObject { ["effort"] = "none", ["exclude"] = true };
        return new JsonObject { ["enabled"] = false, ["exclude"] = true };
    }
}

public abstract record SseEvent
{
    public sealed record Content(string Text) : SseEvent;
    public sealed record Finished(string? Reason) : SseEvent;
    public sealed record Done : SseEvent;
    /// <summary>An error object in the stream, with the HTTP status its <c>code</c> gives, if any.</summary>
    public sealed record Error(string Message, int? Status = null) : SseEvent;
    /// <summary>The token counts (and on OpenRouter the cost), sent in the last chunk.</summary>
    public sealed record Usage(RequestUsage Value) : SseEvent;
}

/// <summary>Parses an OpenAI-style SSE stream for chat completions.</summary>
public sealed class SseParser
{
    public List<SseEvent> Parse(string rawLine)
    {
        var events = new List<SseEvent>();
        var line = rawLine.Trim(' ', '\t');
        // Empty lines separate events; ":" lines are keep-alive comments (": OPENROUTER PROCESSING").
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return events;
        var payload = line[5..].Trim(' ', '\t');
        if (payload == "[DONE]")
        {
            events.Add(new SseEvent.Done());
            return events;
        }
        JsonObject? obj;
        try { obj = JsonNode.Parse(payload) as JsonObject; } catch (JsonException) { return events; }
        if (obj is null) return events;
        if (obj.ContainsKey("error"))
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            events.Add(new SseEvent.Error(ApiClient.ErrorMessage(bytes), ApiClient.ErrorStatus(bytes)));
            return events;
        }
        if (RequestUsage.Parse(obj["usage"]) is { } usage) events.Add(new SseEvent.Usage(usage));
        if (obj["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice)
        {
            if (choice["delta"] is JsonObject delta && ApiClient.TryString(delta["content"], out var content) && content.Length > 0)
            {
                events.Add(new SseEvent.Content(content));
            }
            if (ApiClient.TryString(choice["finish_reason"], out var reason))
            {
                events.Add(reason == "error" ? new SseEvent.Error("provider returned finish_reason=error") : new SseEvent.Finished(reason));
            }
        }
        return events;
    }
}

/// <param name="Usage">What the request used: as reported, or estimated from the text when the server said nothing.</param>
public sealed record PolishResult(string Text, bool Truncated, RequestUsage? Usage = null);

public sealed partial class ApiClient
{
    /// <summary>
    /// Clean-up output is short, so the wait is mostly time to first token: prefer the lowest-latency
    /// provider, but push ones that stream slowly (under 50 tokens/s at the median) to the back.
    /// </summary>
    internal static JsonObject PolishRouting() => new()
    {
        ["sort"] = "latency",
        ["preferred_min_throughput"] = new JsonObject { ["p50"] = 50 },
        ["allow_fallbacks"] = true,
    };

    /// <summary>
    /// <see cref="PolishRouting()"/>, with the user's chosen host tried first. Should that host be down or busy,
    /// OpenRouter still falls back to the others, fastest first, so a dictation never fails over the choice.
    /// </summary>
    internal static JsonObject PolishRouting(string? host)
    {
        var routing = PolishRouting();
        if (!string.IsNullOrWhiteSpace(host)) routing["order"] = new JsonArray(host.Trim());
        return routing;
    }

    /// <summary>The system prompt and the user message a clean-up (or translation) of <paramref name="transcript"/> sends.</summary>
    internal static string[] PolishMessages(string transcript, PolishOptions options)
    {
        if (options.Translation is { } target)
        {
            return
            [
                options.SystemPromptOverride ?? Prompts.TranslateSystemPrompt(target, options.Vocabulary ?? [], options.ExtraInstructions, options.Misheard),
                Prompts.TranslateUserMessage(transcript, target),
            ];
        }
        return
        [
            options.SystemPromptOverride ?? Prompts.PolishSystemPrompt(options.Vocabulary ?? [], options.ExtraInstructions, options.Misheard),
            Prompts.PolishUserMessage(transcript, options.Context),
        ];
    }

    /// <summary>
    /// Builds the chat request body. Optional tuning parameters are only sent where they're known to
    /// be accepted: OpenRouter normalises them for every model, while e.g. OpenAI's reasoning models
    /// reject <c>temperature</c>.
    /// </summary>
    internal JsonObject PolishBody(string transcript, PolishOptions options, bool includeOptional)
    {
        var messages = PolishMessages(transcript, options);
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = messages[0] },
                new JsonObject { ["role"] = "user", ["content"] = messages[1] },
            },
        };
        if (!includeOptional) return body;
        if (IsOpenRouter)
        {
            body["max_tokens"] = Math.Max(1024, TextMetrics.CharacterCount(transcript) * 3);
            body["provider"] = PolishRouting(options.Host);
            if (ReasoningConfig.Body(options.ModelInfo) is { } reasoning) body["reasoning"] = reasoning;
            if (options.ModelInfo?.SupportedParameters.Contains("temperature") ?? true) body["temperature"] = 0.2;
        }
        else if (Endpoint.Id != ProviderId.OpenAI)
        {
            body["temperature"] = 0.2;
        }
        if (!IsOpenRouter)
        {
            // OpenAI-compatible servers only report token counts on a stream when asked (OpenRouter always does).
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        return body;
    }

    /// <summary>
    /// How long a clean-up may take in all: long enough for a slow provider to stream the whole answer out, and short
    /// enough that a stuck one gives the dictation back (as spoken) within a minute or two rather than four.
    /// </summary>
    public static double PolishTimeout(string transcript) => Math.Min(120, 20 + TextMetrics.CharacterCount(transcript) * 0.05);

    /// <summary>
    /// Clean-up models start answering within a second or two. OpenRouter sends keep-alive comments while a model is
    /// queued or slow to start, which keep the idle timeout from ever firing, so the first word has a deadline of its own.
    /// </summary>
    public const double PolishFirstTokenTimeout = 10;

    /// <summary>
    /// Streams a clean-up completion. Liveness is judged by an *idle* timeout (no bytes for
    /// <paramref name="idleTimeout"/> seconds) rather than a short total timeout, so long dictations that take a while
    /// to stream out are never killed mid-way. <paramref name="totalTimeout"/> (by default <see cref="PolishTimeout"/>) is
    /// only a runaway guard; the first content must arrive within <paramref name="firstTokenTimeout"/>. All three end in
    /// a <see cref="ApiErrorKind.Timeout"/>.
    /// </summary>
    public async Task<PolishResult> Polish(string transcript, PolishOptions options, double idleTimeout = 25, double? totalTimeout = null,
                                           Action<string>? onPartial = null, CancellationToken cancellationToken = default,
                                           double firstTokenTimeout = PolishFirstTokenTimeout)
    {
        var limits = new StreamLimits(idleTimeout, totalTimeout ?? PolishTimeout(transcript), firstTokenTimeout);
        try
        {
            return await StreamPolish(transcript, options, true, limits, onPartial, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException error) when (error.Kind == ApiErrorKind.Http && error.Status is 400 or 422)
        {
            // A server rejecting an optional parameter: retry with the bare minimum request.
            var lower = error.Detail.ToLowerInvariant();
            if (!new[] { "temperature", "max_tokens", "reasoning", "provider", "stream_options", "include_usage", "unsupported", "unrecognized", "unknown" }
                    .Any(lower.Contains)) throw;
            return await StreamPolish(transcript, options, false, limits, onPartial, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record StreamLimits(double Idle, double Total, double FirstToken);

    private async Task<PolishResult> StreamPolish(string transcript, PolishOptions options, bool includeOptional, StreamLimits limits,
                                                  Action<string>? onPartial, CancellationToken cancellationToken)
    {
        var body = PolishBody(transcript, options, includeOptional);
        var request = Request("chat/completions", JsonContent(body));
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        // Told as the request goes out (not when it's built, nor for one cancelled first), so History says so only when the
        // text around the cursor really went to the provider.
        var sentContext = options.Translation is null && Prompts.ContextBlock(options.Context) != null;
        if (sentContext && !cancellationToken.IsCancellationRequested) options.OnContextSent?.Invoke();

        return await Timeouts.WithTimeout(limits.Total, async ct =>
        {
            using var firstToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
            firstToken.CancelAfter(TimeSpan.FromSeconds(limits.FirstToken));
            try
            {
                using (request)
                using (var idle = new IdleTimer(limits.Idle, firstToken.Token))
                {
                    using var response = await idle.Run(t => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, t)).ConfigureAwait(false);
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
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
                    var parser = new SseParser();
                    var text = new StringBuilder();
                    string? finishReason = null;
                    var receivedDone = false;
                    RequestUsage? usage = null;
                    while (await idle.Run(t => reader.ReadLineAsync(t).AsTask()).ConfigureAwait(false) is { } line)
                    {
                        foreach (var e in parser.Parse(line))
                        {
                            switch (e)
                            {
                                case SseEvent.Content c:
                                    // The answer has started: from here on only the idle and total timeouts apply.
                                    if (text.Length == 0) firstToken.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                                    text.Append(c.Text);
                                    onPartial?.Invoke(text.ToString());
                                    break;
                                case SseEvent.Finished f:
                                    finishReason = f.Reason;
                                    break;
                                case SseEvent.Done:
                                    receivedDone = true;
                                    break;
                                case SseEvent.Usage u:
                                    usage = u.Value;
                                    break;
                                case SseEvent.Error err:
                                    // The error's own code (401, 402, 429…) decides whether asking again can help; without one
                                    // it's the upstream provider failing.
                                    throw ApiException.Http(err.Status ?? 502, err.Message);
                            }
                            if (receivedDone) break;
                        }
                        if (receivedDone) break;
                    }
                    var cleaned = Prompts.SanitizePolishOutput(text.ToString(), Prompts.Reminder(options.Translation), sentContext);
                    if (cleaned.Length == 0) throw ApiException.BadResponse(L("模型返回了空内容", "the model returned nothing"));
                    if (usage is null || (usage.InputTokens == null && usage.OutputTokens == null && usage.Cost == null))
                    {
                        usage = EstimatedUsage(body, text.ToString());
                    }
                    return new PolishResult(cleaned, IsTruncated(finishReason, receivedDone), usage);
                }
            }
            catch (Exception error) when ((error is OperationCanceledException or IOException)
                                          && firstToken.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Nothing but keep-alive comments until the first-token deadline.
                throw ApiException.TimedOut(limits.FirstToken);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An answer stopped by the output limit or a content filter is incomplete, and so is a stream that ended without
    /// saying it had finished (neither a finish reason nor <c>[DONE]</c>): the connection broke off. Inserting it as the
    /// clean-up would silently lose the rest of the dictation.
    /// </summary>
    internal static bool IsTruncated(string? finishReason, bool receivedDone) => finishReason switch
    {
        "length" or "content_filter" => true,
        null => !receivedDone,
        _ => false,
    };
}

public sealed partial class ApiClient
{
    /// <summary>Token counts guessed from the messages sent and the text received, for servers that report none.</summary>
    internal static RequestUsage EstimatedUsage(JsonObject body, string output)
    {
        var input = 0;
        foreach (var message in body["messages"] as JsonArray ?? [])
        {
            if (TryString(message?["content"], out var content)) input += TokenEstimate.Count(content) + 4;
        }
        return new RequestUsage(input, TokenEstimate.Count(output), Estimated: true);
    }

    /// <summary>Token counts guessed for a clean-up of <paramref name="transcript"/> that had sent <paramref name="output"/>.</summary>
    internal static RequestUsage EstimatedPolishUsage(string transcript, PolishOptions options, string output) =>
        new(PolishMessages(transcript, options).Sum(m => TokenEstimate.Count(m) + 4), TokenEstimate.Count(output), Estimated: true);
}

/// <summary>
/// An inactivity timeout: each awaited step must make progress within <c>seconds</c>, like URLSession's
/// <c>timeoutInterval</c>. A stall surfaces as a timeout (<see cref="ApiErrorKind.Timeout"/>), which the clean-up doesn't retry.
/// </summary>
internal sealed class IdleTimer : IDisposable
{
    private readonly double _seconds;
    private readonly CancellationToken _outer;
    private readonly CancellationTokenSource _cts;

    public IdleTimer(double seconds, CancellationToken outer)
    {
        _seconds = seconds;
        _outer = outer;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
    }

    public async Task<T> Run<T>(Func<CancellationToken, Task<T>> step)
    {
        _cts.CancelAfter(TimeSpan.FromSeconds(_seconds));
        try
        {
            return await step(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_outer.IsCancellationRequested)
        {
            throw ApiException.TimedOut(_seconds);
        }
        catch (IOException) when (_cts.IsCancellationRequested && !_outer.IsCancellationRequested)
        {
            throw ApiException.TimedOut(_seconds);
        }
    }

    public void Dispose() => _cts.Dispose();
}
