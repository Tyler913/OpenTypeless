using Microsoft.UI.Dispatching;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// The Home page's numbers: words, speaking time and spend per day, in <c>usage.json</c> next to the settings. The
/// ledger outlives History (which drops old dictations), so totals keep growing from the day the app was installed.
/// UI-thread only, like the history store.
/// </summary>
public sealed class UsageStore
{
    public static UsageStore Shared { get; } = new();

    public event Action? Changed;

    private static string FilePath => Path.Combine(AppPaths.Support, "usage.json");

    public UsageLedger Ledger { get; }

    private UsageStore()
    {
        if (UsageLedger.Load(FilePath) is { } ledger)
        {
            Ledger = ledger;
        }
        else
        {
            // First run of a version with the Home page: start from the dictations History still has.
            Ledger = new UsageLedger();
            foreach (var record in HistoryStore.Shared.Records.ToList())
            {
                if (CountIfFinished(record)) HistoryStore.Shared.Update(record);
            }
            Ledger.Save(FilePath);
        }
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// Adds a finished dictation's words and speaking time once. Returns whether it was counted now, so the caller
    /// saves the record's <see cref="DictationRecord.Counted"/> flag and a retry doesn't count it again.
    /// </summary>
    public bool CountIfFinished(DictationRecord record)
    {
        if (record.Counted == true || record.Status is not (DictationStatus.Done or DictationStatus.PolishFailed)) return false;
        var words = WordCount.Count(record.FinalText);
        if (words == 0) return false;
        Ledger.AddDictation(DateOnly.FromDateTime(record.Date.LocalDateTime), words, record.Duration);
        record.Counted = true;
        return true;
    }

    /// <summary>Records one dictation's processing: its cost today, and its words if it just finished.</summary>
    public void Record(DictationRecord record, double transcriptionCost, double cleanupCost, int unpriced)
    {
        CountIfFinished(record);
        Ledger.AddCost(Today, transcriptionCost, cleanupCost, unpriced);
        Ledger.Save(FilePath);
        Changed?.Invoke();
    }
}

/// <summary>
/// OpenRouter's price list, kept fresh: loaded from the cached <c>openrouter-prices.json</c> at launch, downloaded
/// again when it's more than six hours old (checked hourly and whenever the Home or Models page opens).
/// </summary>
public sealed class PriceStore
{
    public static PriceStore Shared { get; } = new();

    public event Action? Changed;

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private static string FilePath => Path.Combine(AppPaths.Support, "openrouter-prices.json");

    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer? _timer;
    private bool _refreshing;

    public PriceCatalog Catalog { get; private set; } = PriceCatalog.Empty;

    private PriceStore()
    {
        try
        {
            if (File.Exists(FilePath) && PriceCatalog.FromJson(File.ReadAllText(FilePath)) is { } cached) Catalog = cached;
        }
        catch (IOException) { }
        _timer = _dispatcher?.CreateTimer();
        if (_timer != null)
        {
            _timer.Interval = TimeSpan.FromHours(1);
            _timer.Tick += (_, _) => RefreshIfStale();
            _timer.Start();
        }
    }

    /// <summary>What a model costs: OpenRouter's live price, or the price the user entered for any other provider.</summary>
    public ModelPrice? Price(ProviderId provider, string model) =>
        provider == ProviderId.OpenRouter ? Catalog.Price(model) : AppSettings.Shared.CustomPrice(provider, model);

    public void RefreshIfStale(TimeSpan? maxAge = null)
    {
        if (_refreshing || !Catalog.IsOlderThan(maxAge ?? MaxAge, DateTimeOffset.UtcNow)) return;
        _refreshing = true;
        _ = Refresh();

        async Task Refresh()
        {
            try
            {
                var endpoint = AppSettings.Shared.Endpoint(ProviderId.OpenRouter);
                var catalog = await PriceCatalog.Fetch(endpoint?.BaseUrl);
                await File.WriteAllTextAsync(FilePath, catalog.ToJson());
                Catalog = catalog;
                Changed?.Invoke();
            }
            catch (Exception error)
            {
                AppLog.Debug("prices", "refresh failed: " + error.Message);
            }
            finally
            {
                _refreshing = false;
            }
        }
    }
}
