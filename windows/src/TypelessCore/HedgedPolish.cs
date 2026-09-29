using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace TypelessCore;

/// <summary>One way to run the clean-up: a provider plus a model.</summary>
public sealed record PolishRoute(ApiClient Client, PolishOptions Options);

public sealed record HedgedPolishResult(
    PolishResult Result,
    /// <summary>The model whose answer was used.</summary>
    string Model,
    bool UsedBackup,
    /// <summary>From the start of the call until the winning stream's first token.</summary>
    double FirstTokenSeconds,
    double TotalSeconds,
    /// <summary>The provider that answered, to price the answer.</summary>
    ProviderId Provider = ProviderId.OpenRouter);

/// <summary>
/// Hedged clean-up: the primary route starts alone; if it hasn't produced its first token after
/// <c>hedgeDelay</c>, or fails before then, the backup starts too. Whichever streams first is kept and
/// the other is cancelled. A slow provider then costs at most <c>hedgeDelay</c> plus the backup's own
/// first-token time, while the extra spend is limited to the slow tail.
/// </summary>
public static class HedgedPolish
{
    /// <summary>
    /// Fixed on purpose rather than learned at runtime, since flash-model first-token latency is
    /// stable. Measured from the client (Sep 2026): gemini-3.1-flash-lite, the fastest clean-up model,
    /// has its first token at 0.41 s at the median and 0.49 s at p90, so a first token still missing at
    /// 0.55 s is already in the slow tail and the backup is worth asking.
    /// </summary>
    public const double DefaultHedgeDelay = 0.55;

    public static async Task<HedgedPolishResult> Run(string transcript, PolishRoute primary, PolishRoute? backup,
                                                     double hedgeDelay = DefaultHedgeDelay, CancellationToken cancellationToken = default)
    {
        PolishRoute[] routes = backup is null ? [primary] : [primary, backup];
        var clock = Stopwatch.StartNew();
        var events = Channel.CreateUnbounded<Event>();
        var firstToken = new int[routes.Length];
        var cancels = new CancellationTokenSource?[routes.Length];
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);

        void Launch(int index)
        {
            if (index >= routes.Length || cancels[index] != null) return;
            var route = routes[index];
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
            cancels[index] = cancel;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await route.Client.Polish(transcript, route.Options, onPartial: _ =>
                    {
                        if (Interlocked.Exchange(ref firstToken[index], 1) == 0) events.Writer.TryWrite(new Event.FirstToken(index));
                    }, cancellationToken: cancel.Token).ConfigureAwait(false);
                    events.Writer.TryWrite(new Event.Finished(index, result, null));
                }
                catch (Exception error)
                {
                    events.Writer.TryWrite(new Event.Finished(index, null, error));
                }
            }, CancellationToken.None);
        }

        _ = Task.Delay(TimeSpan.FromSeconds(hedgeDelay), timer.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) events.Writer.TryWrite(new Event.HedgeTimer());
        }, TaskScheduler.Default);

        try
        {
            Launch(0);
            int? winner = null;
            double? firstTokenAt = null;
            var failures = new Dictionary<int, Exception>();
            await foreach (var e in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (e)
                {
                    case Event.HedgeTimer:
                        if (winner == null) Launch(1);
                        break;
                    case Event.FirstToken(var index):
                        if (winner != null) break;
                        winner = index;
                        firstTokenAt = clock.Elapsed.TotalSeconds;
                        timer.Cancel();
                        for (var other = 0; other < cancels.Length; other++)
                        {
                            if (other != index) cancels[other]?.Cancel();
                        }
                        break;
                    case Event.Finished(var index, { } result, _):
                        if (winner != null && winner != index) break;
                        var now = clock.Elapsed.TotalSeconds;
                        return new HedgedPolishResult(result, routes[index].Options.Model, UsedBackup: index != 0,
                                                      FirstTokenSeconds: firstTokenAt ?? now, TotalSeconds: now,
                                                      Provider: routes[index].Client.Endpoint.Id);
                    case Event.Finished(var index, null, { } error):
                        // The chosen stream broke mid-way: its partial text is useless, so give up this round.
                        if (winner == index) ExceptionDispatchInfo.Throw(error);
                        if (winner != null) break;
                        failures[index] = error;
                        if (ApiException.From(error).IsCancelled) ExceptionDispatchInfo.Throw(error);
                        // Failing fast is the other reason to bring in the backup early.
                        Launch(1);
                        if (failures.Count == cancels.Count(c => c != null)) ExceptionDispatchInfo.Throw(failures.GetValueOrDefault(0) ?? error);
                        break;
                }
            }
            throw ApiException.Cancelled();
        }
        finally
        {
            // Stops the timer and whichever request is still running.
            scope.Cancel();
        }
    }

    private abstract record Event
    {
        public sealed record HedgeTimer : Event;
        public sealed record FirstToken(int Index) : Event;
        public sealed record Finished(int Index, PolishResult? Result, Exception? Error) : Event;
    }
}
