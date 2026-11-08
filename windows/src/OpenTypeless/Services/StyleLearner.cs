using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// Learns how the user writes (see <see cref="StyleLearning"/>) while "Learn my writing style" is on: keeps the
/// corrections made to pasted text, counts the dictations that finish, and asks the clean-up model for a new note on Learn
/// now and after every <see cref="StyleLearning.RelearnEvery"/> of them. The note replaces the old one only once it passes
/// the guard, and an automatic run leaves a note edited by hand alone.
///
/// The corrections are kept in <c>style-edits.json</c> in the data folder (the newest 30, each side cut to 500
/// characters), only while the switch is on; switching it off or Clear deletes them. They and History's texts go only to
/// the clean-up provider the user chose, and only when a note is learned. UI-thread only, like the history store.
/// </summary>
public sealed class StyleLearner
{
    public static StyleLearner Shared { get; } = new();

    /// <summary>Raised on the UI thread when <see cref="IsLearning"/> or <see cref="Problem"/> changes.</summary>
    public event Action? Changed;

    public bool IsLearning { get; private set; }
    /// <summary>Why the last run gave no new note, shown on the page until the next one; null when it gave one.</summary>
    public string? Problem { get; private set; }
    /// <summary>OpenRouter's metadata for a model, so reasoning is switched off as for a clean-up (set by SessionController).</summary>
    public Func<string, ModelInfo?>? ModelInfoFor { get; set; }
    /// <summary>Shows a spending reminder the request brought due (set by SessionController).</summary>
    public Action<SpendReminder>? OnSpendReminder { get; set; }

    private readonly AppSettings _settings = AppSettings.Shared;
    private static string EditsPath => Path.Combine(AppPaths.Support, "style-edits.json");

    private StyleLearner() { }

    /// <summary>A correction the user made to a pasted text (see EditWatcher): kept while the switch is on.</summary>
    public void RecordEdit(string inserted, string edited)
    {
        if (!_settings.LearnStyle || StyleLearning.Edit.Make(inserted, edited) is not { } edit) return;
        SaveEdits(StyleLearning.Adding(edit, LoadEdits()));
    }

    /// <summary>A dictation was counted once it finished: one more toward the next automatic run, if it's one to learn from.</summary>
    public void DictationFinished(DictationRecord record)
    {
        if (!_settings.LearnStyle || !IsEligible(record)) return;
        _settings.StyleNewDictations += 1;
        if (StyleLearning.IsDue(_settings.StyleNewDictations, _settings.StyleNote, _settings.StyleLearnedNote)) _ = Learn(automatic: true);
    }

    public void LearnNow() => _ = Learn(automatic: false);

    /// <summary>Empties the note and forgets the corrections kept; automatic runs start over.</summary>
    public void Clear()
    {
        _settings.StyleNote = "";
        _settings.StyleLearnedNote = "";
        _settings.StyleNewDictations = 0;
        DeleteEdits();
        SetProblem(null);
    }

    /// <summary>
    /// The switch went off: nothing more is learned or used, and the corrections kept for it are deleted. The note stays,
    /// for when it's switched on again (Clear empties it).
    /// </summary>
    public void SwitchedOff()
    {
        DeleteEdits();
        SetProblem(null);
    }

    private static bool IsEligible(DictationRecord record) =>
        StyleLearning.IsEligible(record.Status.RawValue(), record.Mode, record.IsFile, record.FinalText);

    private async Task Learn(bool automatic)
    {
        if (IsLearning) return;
        // Counted from this run on, whatever comes of it, so a provider that's down isn't asked after every dictation.
        _settings.StyleNewDictations = 0;
        if (_settings.PolishEndpoint is not { } endpoint || !_settings.IsConfigured(_settings.PolishProvider))
        {
            SetProblem(ApiException.MissingApiKey(_settings.PolishProvider.DisplayName()).Message);
            return;
        }
        var texts = HistoryStore.Shared.Records.Where(IsEligible).Select(r => r.FinalText).ToList();
        if (StyleLearning.Sample(texts, LoadEdits()) is not { } sample)
        {
            SetProblem(L("可以学习的文字还不够，多听写一些再试", "Not enough texts to learn from yet. Dictate a little more first"));
            return;
        }
        var provider = _settings.PolishProvider;
        var model = _settings.PolishModel.Trim();
        var options = new PolishOptions(model,
                                        ModelInfo: provider == ProviderId.OpenRouter ? ModelInfoFor?.Invoke(model) : null,
                                        Host: provider == ProviderId.OpenRouter ? _settings.OpenRouterHost(model) : null);
        var noteBefore = _settings.StyleNote;
        IsLearning = true;
        SetProblem(null);
        try
        {
            var answer = await new ApiClient(endpoint).LearnStyle(sample, options);
            Account(answer.Usage, provider, model);
            // Switched off meanwhile; or, for an automatic run, the note edited while it ran: that edit stands.
            if (!_settings.LearnStyle || (automatic && _settings.StyleNote != noteBefore)) return;
            var verdict = answer.Truncated ? StyleLearning.Verdict.Refused(StyleLearning.Rejection.TooLong) : StyleLearning.Check(answer.Text);
            if (verdict.Note is { } note)
            {
                _settings.StyleNote = note;
                _settings.StyleLearnedNote = note;
                AppLog.Debug("style", $"note learned: {note.Length} characters");
            }
            else if (verdict.Rejected == StyleLearning.Rejection.None)
            {
                SetProblem(L("还看不出明显的写作习惯，原来的说明保持不变", "No clear habits yet. The note is unchanged"));
            }
            else
            {
                AppLog.Debug("style", $"note rejected: {verdict.Rejected?.RawValue()}");
                SetProblem(L("这次没有得到可用的风格说明，原来的保持不变", "No usable note came back. The note is unchanged"));
            }
        }
        catch (Exception error)
        {
            var failure = ApiException.From(error);
            if (!failure.IsCancelled) SetProblem(L("未能学习：", "Couldn't learn: ") + failure.Message);
        }
        finally
        {
            IsLearning = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Adds the request to the spend, priced like a clean-up.</summary>
    private void Account(RequestUsage? usage, ProviderId provider, string model)
    {
        if (usage is null) return;
        var cost = CostEstimator.Chat(usage, PriceStore.Shared.Price(provider, model), provider == ProviderId.OpenRouter);
        if (UsageStore.Shared.RecordCleanUp(cost ?? 0, cost is null ? 1 : 0) is { } reminder) OnSpendReminder?.Invoke(reminder);
    }

    private void SetProblem(string? problem)
    {
        Problem = problem;
        Changed?.Invoke();
    }

    // MARK: The corrections kept

    private static List<StyleLearning.Edit> LoadEdits()
    {
        try
        {
            return File.Exists(EditsPath) ? StyleLearning.Edit.ListFromJson(SafeFile.ReadAllText(EditsPath)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void SaveEdits(List<StyleLearning.Edit> edits)
    {
        try
        {
            SafeFile.WriteAllText(EditsPath, StyleLearning.Edit.ListToJson(edits));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("style", $"can't save the corrections: {e.Message}");
        }
    }

    private static void DeleteEdits()
    {
        try
        {
            File.Delete(EditsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("style", $"can't delete the corrections: {e.Message}");
        }
    }
}
