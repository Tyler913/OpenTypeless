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
    /// saves the record's <see cref="DictationRecord.Counted"/> flag and a retry doesn't count it again. A transcribed
    /// file isn't counted: its words weren't dictated, and an hour-long meeting would swamp the words and the time saved
    /// over typing. What it cost is counted all the same (<see cref="Record"/>).
    /// </summary>
    public bool CountIfFinished(DictationRecord record)
    {
        if (record.IsFile || record.Counted == true || record.Status is not (DictationStatus.Done or DictationStatus.PolishFailed)) return false;
        var words = WordCount.Count(record.FinalText);
        if (words == 0) return false;
        Ledger.AddDictation(DateOnly.FromDateTime(record.Date.LocalDateTime), words, record.Duration);
        record.Counted = true;
        return true;
    }

    /// <summary>
    /// Records one dictation's processing: its cost today, and its words if it just finished. Returns the spending
    /// reminder that cost brings due, if any (see <see cref="SpendLimit"/>); it's saved as shown, so it comes once a month.
    /// </summary>
    public SpendReminder? Record(DictationRecord record, double transcriptionCost, double cleanupCost, int unpriced)
    {
        CountIfFinished(record);
        var today = Today;
        var before = Ledger.Month(today).Cost;
        Ledger.AddCost(today, transcriptionCost, cleanupCost, unpriced);
        Ledger.Save(FilePath);
        var reminder = SpendReminderFor(today, before);
        Changed?.Invoke();
        return reminder;
    }

    /// <summary>This calendar month's spend (what Home shows as "this month") against the monthly limit, while one is set.</summary>
    public (double Spent, double Limit)? MonthlySpend =>
        AppSettings.Shared.ActiveSpendLimit is { } limit ? (Ledger.Month(Today).Cost, limit) : null;

    private SpendReminder? SpendReminderFor(DateOnly today, double spentBefore)
    {
        var settings = AppSettings.Shared;
        if (settings.ActiveSpendLimit is not { } limit) return null;
        var spent = Ledger.Month(today).Cost;
        var saved = settings.SpendReminderState;
        var outcome = SpendLimit.Check(spentBefore, spent, limit, SpendLimit.MonthKey(today), saved);
        if (outcome.State != saved) settings.SpendReminderState = outcome.State;
        return outcome.Reminder is { } level ? new SpendReminder(level, spent, limit) : null;
    }
}

/// <summary>A spending reminder that came due (see <see cref="SpendLimit"/>), shown in the capsule once the dictation is done.</summary>
public sealed record SpendReminder(SpendLimit.Level Level, double Spent, double Limit)
{
    public string Message
    {
        get
        {
            var amount = UsageFormat.Money(Spent);
            var cap = UsageFormat.Money(Limit);
            if (Level == SpendLimit.Level.Reached)
            {
                return L($"本月花费已达上限：{amount} / {cap}。听写不受影响",
                         $"You've reached this month's spending limit: {amount} of {cap}. Dictation keeps working");
            }
            var percent = (int)Math.Round(SpendLimit.WarningFraction * 100);
            return L($"本月已花费 {amount}，达到上限 {cap} 的 {percent}%",
                     $"This month's spend is {amount}, {percent}% of your {cap} limit");
        }
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
