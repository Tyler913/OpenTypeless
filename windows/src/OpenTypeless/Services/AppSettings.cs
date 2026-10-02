using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenTypeless.Input;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// User preferences. API keys live in Windows Credential Manager; everything else in
/// <c>%LOCALAPPDATA%\OpenTypeless\settings.json</c>, with the same keys and defaults as the macOS app's UserDefaults.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public static AppSettings Shared { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly JsonObject _store;
    private Dictionary<string, string> _apiKeys;

    private AppSettings()
    {
        _path = AppPaths.SettingsFile;
        JsonObject? loaded = null;
        try
        {
            // Read as text so a byte-order mark (e.g. after hand-editing in Notepad) is handled.
            if (File.Exists(_path)) loaded = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
        }
        catch (JsonException)
        {
            // A corrupt file falls back to defaults rather than blocking launch.
        }
        _store = loaded ?? new JsonObject();
        // In CLI mode with OPENROUTER_API_KEY set, don't touch the credential store.
        var cli = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));
        _apiKeys = cli ? new() : Credentials.Load();
        Localization.Preference = () => GetString(Localization.SettingsKey, "system");
    }

    // MARK: Stored values

    public AppLanguage AppLanguage
    {
        get => Localization.ParseLanguage(GetString(Localization.SettingsKey, "system"));
        set => SetValue(Localization.SettingsKey, value.RawValue());
    }

    public Hotkey Hotkey
    {
        get
        {
            lock (_lock)
            {
                if (_store["hotkey"] is JsonObject obj)
                {
                    try
                    {
                        if (obj.Deserialize<Hotkey>() is { } hotkey) return hotkey;
                    }
                    catch (JsonException) { }
                }
            }
            return Hotkey.Default;
        }
        set => SetNode("hotkey", JsonSerializer.SerializeToNode(value));
    }

    public ProviderId SttProvider
    {
        get => ProviderIdExtensions.Parse(GetString("sttProvider", "")) ?? ProviderId.OpenRouter;
        set => SetValue("sttProvider", value.RawValue());
    }

    public string SttModel
    {
        get => GetString("sttModel", ProviderId.OpenRouter.DefaultSttModel());
        set => SetValue("sttModel", value);
    }

    public ProviderId PolishProvider
    {
        get => ProviderIdExtensions.Parse(GetString("polishProvider", "")) ?? ProviderId.OpenRouter;
        set => SetValue("polishProvider", value.RawValue());
    }

    public string PolishModel
    {
        get => GetString("polishModel", ProviderId.OpenRouter.DefaultChatModel());
        set => SetValue("polishModel", value);
    }

    public bool PolishEnabled
    {
        get => GetBool("polishEnabled", true);
        set => SetValue("polishEnabled", value);
    }

    /// <summary>A second speech-to-text route, asked when the first is late for a chunk or fails (see TranscriptionPipeline).</summary>
    public bool SttBackupEnabled
    {
        get => GetBool("sttBackupEnabled", false);
        set => SetValue("sttBackupEnabled", value);
    }

    public ProviderId SttBackupProvider
    {
        get => ProviderIdExtensions.Parse(GetString("sttBackupProvider", "")) ?? ProviderId.OpenRouter;
        set => SetValue("sttBackupProvider", value.RawValue());
    }

    public string SttBackupModel
    {
        get => GetString("sttBackupModel", ProviderId.OpenRouter.DefaultBackupSttModel());
        set => SetValue("sttBackupModel", value);
    }

    /// <summary>A second clean-up model that races the first when it is slow to start (see HedgedPolish).</summary>
    public bool PolishBackupEnabled
    {
        get => GetBool("polishBackupEnabled", true);
        set => SetValue("polishBackupEnabled", value);
    }

    public ProviderId PolishBackupProvider
    {
        get => ProviderIdExtensions.Parse(GetString("polishBackupProvider", "")) ?? ProviderId.OpenRouter;
        set => SetValue("polishBackupProvider", value.RawValue());
    }

    public string PolishBackupModel
    {
        get => GetString("polishBackupModel", ProviderId.OpenRouter.DefaultBackupChatModel());
        set => SetValue("polishBackupModel", value);
    }

    /// <summary>Spoken-language hint for STT; "" = auto-detect. (Key predates the UI language setting.)</summary>
    public string SttLanguage
    {
        get => GetString("language", "");
        set => SetValue("language", value);
    }

    public string Vocabulary
    {
        get => GetString("vocabulary", "Claude, OpenRouter, SwiftUI, GitHub, Typeless");
        set => SetValue("vocabulary", value);
    }

    public string ExtraInstructions
    {
        get => GetString("extraInstructions", "");
        set => SetValue("extraInstructions", value);
    }

    public bool PlaySounds
    {
        get => GetBool("playSounds", true);
        set => SetValue("playSounds", value);
    }

    public bool RestoreClipboard
    {
        get => GetBool("restoreClipboard", true);
        set => SetValue("restoreClipboard", value);
    }

    /// <summary>Press Enter once the text is pasted, so a chat message or AI prompt is sent without touching the keyboard.</summary>
    public bool PressEnterAfterInsert
    {
        get => GetBool("pressEnterAfterInsert", false);
        set => SetValue("pressEnterAfterInsert", value);
    }

    /// <summary>Mute the speakers while recording (see OutputMute).</summary>
    public bool MuteWhileRecording
    {
        get => GetBool("muteWhileRecording", false);
        set => SetValue("muteWhileRecording", value);
    }

    public int MaxRecordingMinutes
    {
        get => GetInt("maxRecordingMinutes", 20);
        set => SetValue("maxRecordingMinutes", Math.Clamp(value, 1, 60));
    }

    public HistoryRetention HistoryRetention
    {
        get => Enum.TryParse<HistoryRetention>(GetString("historyRetention", ""), ignoreCase: true, out var value) ? value : HistoryRetention.Month;
        set => SetValue("historyRetention", value.ToString().ToLowerInvariant());
    }

    /// <summary>Learn vocabulary from the fixes the user makes to dictated text (see EditWatcher).</summary>
    public bool LearnFromEdits
    {
        get => GetBool("learnFromEdits", true);
        set => SetValue("learnFromEdits", value);
    }

    /// <summary>The microphone to record from, by endpoint ID; "" follows the default input.</summary>
    public string MicrophoneId
    {
        get => GetString("microphone", "");
        set => SetValue("microphone", value);
    }

    /// <summary>Keep the microphone running between dictations, so recording starts at once with the moment before the key.</summary>
    public bool KeepMicrophoneWarm
    {
        get => GetBool("keepMicrophoneWarm", false);
        set => SetValue("keepMicrophoneWarm", value);
    }

    /// <summary>Show what's being said above the capsule while recording, recognised by Windows (see LivePreview).</summary>
    public bool LivePreview
    {
        get => GetBool("livePreview", false);
        set => SetValue("livePreview", value);
    }

    /// <summary>Open the Home page when the app starts at sign-in (a manual launch always shows it).</summary>
    public bool ShowHomeAtLogin
    {
        get => GetBool("showHomeAtLogin", false);
        set => SetValue("showHomeAtLogin", value);
    }

    /// <summary>The typing speed "time saved" on the Home page is measured against.</summary>
    public int TypingWordsPerMinute
    {
        get => Math.Clamp(GetInt("typingWordsPerMinute", UsageTotals.DefaultTypingWordsPerMinute), 10, 300);
        set => SetValue("typingWordsPerMinute", Math.Clamp(value, 10, 300));
    }

    /// <summary>The first-launch guide has been shown (see Onboarding); it never comes back after that.</summary>
    public bool DidShowOnboarding
    {
        get => GetBool("didShowOnboarding", false);
        set => SetValue("didShowOnboarding", value);
    }

    public bool DidEnableLaunchAtLoginByDefault
    {
        get => GetBool("didEnableLaunchAtLoginByDefault", false);
        set => SetValue("didEnableLaunchAtLoginByDefault", value);
    }

    /// <summary>Look for a new release once a day and download it in the background (see Updater).</summary>
    public bool AutoCheckUpdates
    {
        get => GetBool("autoCheckUpdates", true);
        set => SetValue("autoCheckUpdates", value);
    }

    public DateTimeOffset? LastUpdateCheck
    {
        get => DateTimeOffset.TryParse(GetString("lastUpdateCheck", ""), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) ? date : null;
        set => SetValue("lastUpdateCheck", value?.ToString("o") ?? "");
    }

    /// <summary>A version the user chose to skip; automatic checks don't offer it.</summary>
    public string SkippedUpdateVersion
    {
        get => GetString("skippedUpdateVersion", "");
        set => SetValue("skippedUpdateVersion", value);
    }

    /// <summary>The version being installed when the app quit, to tell after the restart whether it worked.</summary>
    public string InstallingUpdateVersion
    {
        get => GetString("installingUpdateVersion", "");
        set => SetValue("installingUpdateVersion", value);
    }

    // MARK: Providers

    public string ApiKey(ProviderId id)
    {
        if (id == ProviderId.OpenRouter && Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is { Length: > 0 } env) return env;
        lock (_lock) return _apiKeys.GetValueOrDefault(id.RawValue(), "");
    }

    public void SetApiKey(string key, ProviderId id)
    {
        var trimmed = key.Trim();
        lock (_lock)
        {
            if (_apiKeys.GetValueOrDefault(id.RawValue(), "") == trimmed) return;
            if (trimmed.Length == 0) _apiKeys.Remove(id.RawValue()); else _apiKeys[id.RawValue()] = trimmed;
            Credentials.Save(new Dictionary<string, string>(_apiKeys));
        }
        Raise("ApiKeys");
    }

    public string BaseUrl(ProviderId id)
    {
        lock (_lock)
        {
            return _store["baseURLs"] is JsonObject urls && urls[id.RawValue()] is JsonValue v && v.TryGetValue<string>(out var url)
                ? url
                : id.DefaultBaseUrl();
        }
    }

    public void SetBaseUrl(string url, ProviderId id)
    {
        var trimmed = url.Trim();
        lock (_lock)
        {
            if (_store["baseURLs"] is not JsonObject urls)
            {
                urls = new JsonObject();
                _store["baseURLs"] = urls;
            }
            if (trimmed.Length == 0 || trimmed == id.DefaultBaseUrl()) urls.Remove(id.RawValue()); else urls[id.RawValue()] = trimmed;
            Save();
        }
        Raise("BaseUrls");
    }

    public ProviderEndpoint? Endpoint(ProviderId id)
    {
        var raw = BaseUrl(id).Trim('/', ' ');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || !url.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(url.Host))
        {
            return null;
        }
        return new ProviderEndpoint(id, url, ApiKey(id));
    }

    public bool IsConfigured(ProviderId id) => Endpoint(id) != null && (!id.RequiresKey() || ApiKey(id).Length > 0);

    public ProviderEndpoint? SttEndpoint => Endpoint(SttProvider);
    public ProviderEndpoint? PolishEndpoint => Endpoint(PolishProvider);

    /// <summary>Switching provider swaps in that provider's default model, since model IDs aren't portable.</summary>
    public void SelectSttProvider(ProviderId id)
    {
        if (id == SttProvider) return;
        SttProvider = id;
        SttModel = id.DefaultSttModel();
    }

    public void SelectPolishProvider(ProviderId id)
    {
        if (id == PolishProvider) return;
        PolishProvider = id;
        PolishModel = id.DefaultChatModel();
    }

    /// <summary>The backup speech-to-text route, when switched on, configured, and actually different from the primary.</summary>
    public ProviderEndpoint? SttBackupEndpoint
    {
        get
        {
            var model = SttBackupModel.Trim();
            if (!SttBackupEnabled || model.Length == 0 || !SttBackupProvider.SupportsStt() || !IsConfigured(SttBackupProvider)
                || (SttBackupProvider == SttProvider && model == SttModel.Trim())) return null;
            return Endpoint(SttBackupProvider);
        }
    }

    public void SelectSttBackupProvider(ProviderId id)
    {
        if (id == SttBackupProvider) return;
        SttBackupProvider = id;
        SttBackupModel = id.DefaultBackupSttModel();
    }

    /// <summary>The backup route, when switched on, configured, and actually different from the primary.</summary>
    public ProviderEndpoint? PolishBackupEndpoint
    {
        get
        {
            var model = PolishBackupModel.Trim();
            if (!PolishBackupEnabled || model.Length == 0 || !IsConfigured(PolishBackupProvider)
                || (PolishBackupProvider == PolishProvider && model == PolishModel.Trim())) return null;
            return Endpoint(PolishBackupProvider);
        }
    }

    public void SelectPolishBackupProvider(ProviderId id)
    {
        if (id == PolishBackupProvider) return;
        PolishBackupProvider = id;
        PolishBackupModel = id.DefaultBackupChatModel();
    }

    // MARK: Model prices

    /// <summary>The price the user entered for a model of a provider other than OpenRouter (whose prices are live).</summary>
    public ModelPrice? CustomPrice(ProviderId provider, string model)
    {
        lock (_lock) return ModelPrice.FromJson((_store["modelPrices"] as JsonObject)?[ModelPrice.Key(provider, model)]);
    }

    public void SetCustomPrice(ProviderId provider, string model, ModelPrice? price)
    {
        if (model.Trim().Length == 0) return;
        lock (_lock)
        {
            if (_store["modelPrices"] is not JsonObject prices)
            {
                prices = new JsonObject();
                _store["modelPrices"] = prices;
            }
            var key = ModelPrice.Key(provider, model);
            if (price is null || price.IsEmpty) prices.Remove(key); else prices[key] = price.ToJson();
            Save();
        }
        Raise("ModelPrices");
    }

    // MARK: Learned vocabulary

    /// <summary>Terms learned from the user's corrections, oldest first.</summary>
    public List<LearnedTerm> LearnedTerms
    {
        get
        {
            lock (_lock)
            {
                try
                {
                    return _store["learnedVocabulary"]?.Deserialize<List<LearnedTerm>>() ?? [];
                }
                catch (JsonException)
                {
                    return [];
                }
            }
        }
        private set => SetNode("learnedVocabulary", JsonSerializer.SerializeToNode(value));
    }

    /// <summary>Learned terms the user removed; never learned again (lowercased).</summary>
    private List<string> ForgottenTerms
    {
        get
        {
            lock (_lock)
            {
                return _store["forgottenVocabulary"] is JsonArray array
                    ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
                    : [];
            }
        }
        set => SetNode("forgottenVocabulary", JsonSerializer.SerializeToNode(value));
    }

    /// <summary>Adds what the user's corrections taught to the vocabulary. Returns the terms that are new.</summary>
    public List<string> Learn(IEnumerable<Correction> corrections)
    {
        var added = new List<string>();
        var learned = LearnedTerms;
        var forgotten = ForgottenTerms.ToHashSet();
        foreach (var correction in corrections)
        {
            var term = correction.Corrected.Trim();
            var key = term.ToLowerInvariant();
            if (forgotten.Contains(key)) continue;
            if (learned.FirstOrDefault(t => t.Term.ToLowerInvariant() == key) is { } existing)
            {
                if (!existing.HeardAs.Contains(correction.Heard)) existing.HeardAs.Add(correction.Heard);
                continue;
            }
            learned.Add(new LearnedTerm { Term = term, HeardAs = [correction.Heard], Date = DateTimeOffset.Now });
            if (!VocabularyList.Any(t => t.ToLowerInvariant() == key))
            {
                var current = Vocabulary.Trim();
                Vocabulary = current.Length == 0 ? term : current + ", " + term;
                added.Add(term);
            }
        }
        LearnedTerms = learned;
        return added;
    }

    /// <summary>Removes a learned term from the vocabulary and makes sure it isn't learned again.</summary>
    public void Forget(LearnedTerm term)
    {
        var key = term.Term.ToLowerInvariant();
        LearnedTerms = LearnedTerms.Where(t => t.Term.ToLowerInvariant() != key).ToList();
        var forgotten = ForgottenTerms;
        if (!forgotten.Contains(key)) ForgottenTerms = [.. forgotten, key];
        if (VocabularyList.Any(t => t.ToLowerInvariant() == key))
        {
            Vocabulary = string.Join(", ", VocabularyList.Where(t => t.ToLowerInvariant() != key));
        }
    }

    /// <summary>Misheard forms of vocabulary terms, newest first, for the clean-up prompt.</summary>
    public List<Correction> MisheardHints
    {
        get
        {
            var terms = VocabularyList.Select(t => t.ToLowerInvariant()).ToHashSet();
            return Enumerable.Reverse(LearnedTerms)
                .Where(t => terms.Contains(t.Term.ToLowerInvariant()))
                .SelectMany(t => t.HeardAs.Select(heard => new Correction(heard, t.Term)))
                .Take(40)
                .ToList();
        }
    }

    public List<string> VocabularyList =>
        Vocabulary.Split([',', '，', '\n'])
                  .Select(t => t.Trim(' ', '\t', '\r'))
                  .Where(t => t.Length > 0)
                  .ToList();

    // MARK: Storage

    private string GetString(string key, string fallback)
    {
        lock (_lock) return _store[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    }

    private bool GetBool(string key, bool fallback)
    {
        lock (_lock) return _store[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    }

    private int GetInt(string key, int fallback)
    {
        lock (_lock) return _store[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
    }

    private void SetValue<T>(string key, T value, [CallerMemberName] string property = "")
    {
        SetNode(key, JsonValue.Create(value), property);
    }

    private void SetNode(string key, JsonNode? value, [CallerMemberName] string property = "")
    {
        lock (_lock)
        {
            if (JsonNode.DeepEquals(_store[key], value)) return;
            _store[key] = value;
            Save();
        }
        Raise(property);
    }

    private void Save()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, _store.ToJsonString(FileJson));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Best effort: the next change writes again.
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>A vocabulary term learned from the user fixing a dictation, with what recognition heard instead.</summary>
public sealed class LearnedTerm
{
    [JsonPropertyName("term")] public string Term { get; set; } = "";
    [JsonPropertyName("heardAs")] public List<string> HeardAs { get; set; } = [];
    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; }
}

/// <summary>
/// How long finished dictations are kept. The recording is the big part (about 1.9 MB per minute
/// of 16 kHz WAV); the text is a few KB.
/// </summary>
public enum HistoryRetention { None, Day, Week, Month, Year, Forever }

public static class HistoryRetentionExtensions
{
    public static readonly HistoryRetention[] All = Enum.GetValues<HistoryRetention>();

    public static string Label(this HistoryRetention retention) => retention switch
    {
        HistoryRetention.None => L("不保存录音", "Don't keep recordings"),
        HistoryRetention.Day => L("保存 1 天", "1 day"),
        HistoryRetention.Week => L("保存 7 天", "7 days"),
        HistoryRetention.Month => L("保存 1 个月", "1 month"),
        HistoryRetention.Year => L("保存 1 年", "1 year"),
        _ => L("永不删除", "Forever"),
    };

    /// <summary>null = never expires.</summary>
    public static TimeSpan? MaxAge(this HistoryRetention retention) => retention switch
    {
        HistoryRetention.None => TimeSpan.Zero,
        HistoryRetention.Day => TimeSpan.FromDays(1),
        HistoryRetention.Week => TimeSpan.FromDays(7),
        HistoryRetention.Month => TimeSpan.FromDays(30),
        HistoryRetention.Year => TimeSpan.FromDays(365),
        _ => null,
    };
}
