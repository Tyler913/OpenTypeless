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
    /// <summary>False when the settings file is there but couldn't be read: the defaults run and nothing is saved over it.</summary>
    private readonly bool _writable = true;
    /// <summary>The saved keys only: OPENROUTER_API_KEY overrides one in <see cref="ApiKey"/> but never lands in here.</summary>
    private readonly Dictionary<string, string> _apiKeys;

    private AppSettings()
    {
        _path = AppPaths.SettingsFile;
        var existed = File.Exists(_path);
        _store = existed ? LoadStore(_path, out _writable) : new JsonObject();
        // The hotkey is always stored, so a new default only reaches new installs: one that ran before without
        // choosing a hotkey had Right Ctrl, the default until 1.4.0, and keeps it.
        if (_store["hotkey"] == null)
        {
            _store["hotkey"] = JsonSerializer.SerializeToNode(existed ? Hotkey.RightControl : Hotkey.Default);
            Save();
        }
        // Profiles are read from the store on every access, so an ID that's missing or taken is repaired once, here, and
        // saved: edits, Remove and retries then keep finding their profile.
        if (_store["appProfiles"] is JsonArray savedProfiles)
        {
            var profiles = AppProfile.ListFromJson(savedProfiles);
            var repaired = AppProfile.WithUniqueIds(profiles);
            if (!repaired.SequenceEqual(profiles))
            {
                _store["appProfiles"] = AppProfile.ListToJson(repaired);
                Save();
            }
        }
        // Snippets are told apart by ID: one saved without its own (none, or another's) is given one now, and kept, so
        // editing or deleting it works (an ID made up on every read would change under the editor).
        var (savedSnippets, snippetsRepaired) = Snippet.ReadList(_store["snippets"]);
        if (snippetsRepaired)
        {
            _store["snippets"] = JsonSerializer.SerializeToNode(savedSnippets);
            Save();
        }
        // Loaded even when OPENROUTER_API_KEY is set: it only stands in for the OpenRouter key, and the app launched
        // with it still needs the others (and must not save over them when one is edited).
        _apiKeys = Credentials.Load();
        Localization.Preference = () => GetString(Localization.SettingsKey, "system");
    }

    /// <summary>
    /// The saved settings. One that can't be parsed is moved aside (see <see cref="SafeFile"/>) and the defaults start,
    /// rather than blocking launch or being saved over it. One that still can't be read after a few tries is held by
    /// another program (antivirus, OneDrive) rather than damaged: it stays, the defaults run for this session, and
    /// <paramref name="writable"/> is false so nothing is saved over it.
    /// </summary>
    private static JsonObject LoadStore(string path, out bool writable)
    {
        writable = true;
        string text;
        try
        {
            // Read as text so a byte-order mark (e.g. after hand-editing in Notepad) is handled.
            text = SafeFile.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("settings", $"can't read {path}, not saving over it this session: {e.Message}");
            writable = false;
            return new JsonObject();
        }
        try
        {
            if (JsonNode.Parse(text) is JsonObject loaded) return loaded;
        }
        catch (JsonException e)
        {
            AppLog.Debug("settings", $"can't parse {path}: {e.Message}");
        }
        var aside = SafeFile.MoveAside(path);
        AppLog.Debug("settings", aside != null ? $"damaged settings moved to {aside}" : "damaged settings couldn't be moved aside");
        // Not moved aside: it's the only copy, so it isn't saved over either.
        writable = aside != null;
        return new JsonObject();
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

    /// <summary>
    /// Fit the spacing and capitalisation of pasted text to the text around the cursor (see InsertionJoiner). Safe on by
    /// default: a field that can't be read gets the text as it is.
    /// </summary>
    public bool FitToSurroundingText
    {
        get => GetBool("fitToSurroundingText", true);
        set => SetValue("fitToSurroundingText", value);
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

    /// <summary>
    /// Translation mode: holding the hotkey and pressing <see cref="TranslationChord"/> inserts that dictation translated
    /// (see <see cref="TranslationTarget"/>). Off until the user turns it on and picks a language.
    /// </summary>
    public bool TranslationEnabled
    {
        get => GetBool("translationEnabled", false);
        set => SetValue("translationEnabled", value);
    }

    /// <summary>
    /// A listed language's code (see <see cref="TypelessCore.TranslationLanguage"/>), or <see cref="OtherTranslationLanguage"/>
    /// for the one typed in <see cref="TranslationOtherLanguage"/>.
    /// </summary>
    public string TranslationLanguage
    {
        get => GetString("translationLanguage", TypelessCore.TranslationLanguage.English.Code());
        set => SetValue("translationLanguage", value);
    }

    public string TranslationOtherLanguage
    {
        get => GetString("translationOtherLanguage", "");
        set => SetValue("translationOtherLanguage", value);
    }

    public TranslationChord TranslationChord
    {
        get => TranslationChords.Parse(GetString("translationChord", "")) ?? TypelessCore.TranslationChord.Shift;
        set => SetValue("translationChord", value.RawValue());
    }

    /// <summary>
    /// Command mode: holding the hotkey and pressing <see cref="CommandChord"/> edits the text selected in the app in front
    /// by what is said (see <see cref="SelectionCommand"/>). Off until the user turns it on: while it's on, the chord's key
    /// pressed with the hotkey is kept from the app in front.
    /// </summary>
    public bool CommandModeEnabled
    {
        get => GetBool("commandModeEnabled", false);
        set => SetValue("commandModeEnabled", value);
    }

    /// <summary>
    /// One of the modifiers translation's chord is chosen from (see <see cref="TypelessCore.TranslationChord"/>); one
    /// translation mode also has is left to translation (see <see cref="SelectionCommand.ChordKeys"/>).
    /// </summary>
    public TranslationChord CommandChord
    {
        get => TranslationChords.Parse(GetString("commandChord", "")) ?? TypelessCore.TranslationChord.Control;
        set => SetValue("commandChord", value.RawValue());
    }

    /// <summary>Whether command mode's chord is the same as translation's, while both are on: then it does nothing.</summary>
    public bool CommandChordClashes => CommandModeEnabled && TranslationTarget != null && CommandChord == TranslationChord;

    /// <summary>What <see cref="TranslationLanguage"/> holds when the language is typed in.</summary>
    public const string OtherTranslationLanguage = "other";

    /// <summary>
    /// What a translation dictation is translated into; null while translation mode is off, or another language is picked
    /// without a name.
    /// </summary>
    public TranslationTarget? TranslationTarget =>
        TranslationEnabled
            ? TypelessCore.TranslationTarget.From(TranslationLanguage == OtherTranslationLanguage ? TranslationOtherLanguage : TranslationLanguage)
            : null;

    /// <summary>
    /// Send the text around the cursor with the transcript, for the clean-up to spell names by (see CursorContextReader).
    /// Off by default: it goes to the clean-up provider.
    /// </summary>
    public bool UseCursorContext
    {
        get => GetBool("useCursorContext", false);
        set => SetValue("useCursorContext", value);
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

    /// <summary>Clean up the transcript of a file transcribed from History (the switch in its import dialog), when clean-up is on.</summary>
    public bool FileCleanUp
    {
        get => GetBool("fileCleanUp", true);
        set => SetValue("fileCleanUp", value);
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

    /// <summary>The key requests use: OPENROUTER_API_KEY, when set, in place of the saved OpenRouter key.</summary>
    public string ApiKey(ProviderId id)
    {
        if (id == ProviderId.OpenRouter && OpenRouterKeyFromEnvironment is { } env) return env;
        return SavedApiKey(id);
    }

    /// <summary>The key saved in Settings, which is what a key field shows and edits, so OPENROUTER_API_KEY is never saved.</summary>
    public string SavedApiKey(ProviderId id)
    {
        lock (_lock) return _apiKeys.GetValueOrDefault(id.RawValue(), "");
    }

    public static string? OpenRouterKeyFromEnvironment =>
        Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is { Length: > 0 } env ? env : null;

    /// <summary>
    /// Saves the key. False when Credential Manager refused it: the key still works until the app quits, but the
    /// caller should say it wasn't saved.
    /// </summary>
    public bool SetApiKey(string key, ProviderId id)
    {
        var trimmed = key.Trim();
        bool saved;
        lock (_lock)
        {
            if (_apiKeys.GetValueOrDefault(id.RawValue(), "") == trimmed) return true;
            // On top of what's stored now, not only what was read at launch, so a key never read here isn't erased.
            foreach (var (name, value) in Credentials.Load()) _apiKeys.TryAdd(name, value);
            if (trimmed.Length == 0) _apiKeys.Remove(id.RawValue()); else _apiKeys[id.RawValue()] = trimmed;
            saved = Credentials.Save(new Dictionary<string, string>(_apiKeys));
        }
        Raise("ApiKeys");
        return saved;
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

    // MARK: Action shortcuts

    /// <summary>The shortcut the user gave <paramref name="action"/> (see <see cref="ShortcutAction"/>); none by default.</summary>
    public Hotkey? Shortcut(ShortcutAction action)
    {
        lock (_lock)
        {
            if ((_store["shortcuts"] as JsonObject)?[action.RawValue()] is JsonObject obj)
            {
                try
                {
                    return obj.Deserialize<Hotkey>();
                }
                catch (JsonException) { }
            }
        }
        return null;
    }

    /// <summary>
    /// Null clears it. Saved under "shortcuts" by the action's name, as on macOS; one saved by a newer version for an
    /// action this one doesn't know is left as it is.
    /// </summary>
    public void SetShortcut(ShortcutAction action, Hotkey? hotkey)
    {
        lock (_lock)
        {
            if (_store["shortcuts"] is not JsonObject shortcuts)
            {
                shortcuts = new JsonObject();
                _store["shortcuts"] = shortcuts;
            }
            if (hotkey is null) shortcuts.Remove(action.RawValue());
            else shortcuts[action.RawValue()] = JsonSerializer.SerializeToNode(hotkey);
            Save();
        }
        Raise(nameof(Shortcuts));
    }

    /// <summary>The actions' shortcuts, as the hotkey hook matches keys against them.</summary>
    public ShortcutMap Shortcuts
    {
        get
        {
            var chords = new Dictionary<ShortcutAction, KeyChord>();
            foreach (var action in ShortcutActions.All)
            {
                if (Shortcut(action) is { } hotkey) chords[action] = hotkey.Chord;
            }
            return new ShortcutMap(chords);
        }
    }

    // MARK: OpenRouter hosts

    /// <summary>The OpenRouter host (endpoint tag) to ask first for this clean-up model, or null to let OpenRouter pick the fastest.</summary>
    public string? OpenRouterHost(string model)
    {
        lock (_lock)
            return (_store["openRouterHosts"] as JsonObject)?[model.Trim()] is JsonValue v && v.TryGetValue<string>(out var host)
                && host.Length > 0 ? host : null;
    }

    public void SetOpenRouterHost(string? host, string model)
    {
        model = model.Trim();
        if (model.Length == 0) return;
        lock (_lock)
        {
            if (_store["openRouterHosts"] is not JsonObject hosts)
            {
                hosts = new JsonObject();
                _store["openRouterHosts"] = hosts;
            }
            if (string.IsNullOrWhiteSpace(host)) hosts.Remove(model); else hosts[model] = host.Trim();
            Save();
        }
        Raise("OpenRouterHosts");
    }

    // MARK: App profiles

    /// <summary>Settings for particular apps, by executable name, applied to a dictation started in them (see AppProfile). None at first.</summary>
    public List<AppProfile> AppProfiles
    {
        get
        {
            if (_sampleProfiles is { } samples) return samples.ToList();
            lock (_lock) return AppProfile.ListFromJson(_store["appProfiles"]);
        }
        private set
        {
            if (_sampleProfiles != null)
            {
                _sampleProfiles = value.ToList();
                Raise(nameof(AppProfiles));
                return;
            }
            SetNode("appProfiles", AppProfile.ListToJson(value));
        }
    }

    /// <summary>Set for the UI snapshots: these profiles are shown in place of the saved ones, and nothing about them is saved.</summary>
    private List<AppProfile>? _sampleProfiles;

    /// <summary>For the UI snapshots: shows these profiles in place of the saved ones, which stay as they are.</summary>
    public void ShowSampleAppProfiles(IEnumerable<AppProfile> profiles) => _sampleProfiles = profiles.ToList();

    /// <summary>What a dictation runs with when no profile applies.</summary>
    public DictationSettings GlobalDictationSettings => new(PolishEnabled, PressEnterAfterInsert, SttLanguage, ExtraInstructions);

    /// <summary>What a dictation started in the app with this executable name ("slack.exe") runs with.</summary>
    public DictationSettings DictationSettingsForApp(string? executable) =>
        GlobalDictationSettings.Applying(AppProfile.Matching(executable, AppProfiles));

    /// <summary>
    /// What a retry of a dictation made with the profile <paramref name="id"/> runs with: that profile's settings while it
    /// still exists, the global ones otherwise.
    /// </summary>
    public DictationSettings DictationSettingsForProfile(string? id) =>
        GlobalDictationSettings.Applying(AppProfiles.FirstOrDefault(p => p.Id == id));

    /// <summary>Adds a profile for an app, following the global settings until it's edited; an app that has one already keeps it.</summary>
    public AppProfile AddAppProfile(string executable, string name)
    {
        var profiles = AppProfiles;
        if (AppProfile.Matching(executable, profiles) is { } existing) return existing;
        var profile = new AppProfile { App = executable, Name = name };
        AppProfiles = [.. profiles, profile];
        return profile;
    }

    public void UpdateAppProfile(AppProfile profile)
    {
        var profiles = AppProfiles;
        var index = profiles.FindIndex(p => p.Id == profile.Id);
        if (index < 0 || profiles[index] == profile) return;
        profiles[index] = profile;
        AppProfiles = profiles;
    }

    public void RemoveAppProfile(string id) => AppProfiles = AppProfiles.Where(p => p.Id != id).ToList();

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

    /// <summary>
    /// Adds an imported file's new terms to the vocabulary, leaving out ones already in it (ignoring case) and ones
    /// <see cref="VocabularyTransfer.Plan"/> keeps out of every clean-up request. Importing is the user asking for these
    /// terms, so one they once removed from the learned terms may be learned again too. Returns what it did.
    /// </summary>
    public VocabularyTransfer.ImportPlan ImportVocabulary(IReadOnlyCollection<string> terms)
    {
        var plan = VocabularyTransfer.Plan(terms, VocabularyList);
        if (plan.Added.Count > 0) Vocabulary = VocabularyTransfer.Merged(Vocabulary, plan.Added);
        var kept = VocabularyList.Select(t => t.ToLowerInvariant()).ToHashSet();
        kept.IntersectWith(terms.Select(t => t.ToLowerInvariant()));
        var forgotten = ForgottenTerms;
        if (forgotten.Any(kept.Contains)) ForgottenTerms = forgotten.Where(t => !kept.Contains(t)).ToList();
        return plan;
    }

    /// <summary>The vocabulary as it's exported: each term with how recognition misheard it, when it was learned from a fix.</summary>
    public List<VocabularyTransfer.Term> VocabularyForExport
    {
        get
        {
            var learned = new Dictionary<string, List<string>>();
            foreach (var term in LearnedTerms) learned.TryAdd(term.Term.ToLowerInvariant(), term.HeardAs);
            return VocabularyList
                .Select(t => new VocabularyTransfer.Term(t, learned.TryGetValue(t.ToLowerInvariant(), out var heard) ? heard : new List<string>()))
                .ToList();
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

    /// <summary>
    /// What speech-to-text and the clean-up are given as vocabulary: the snippets' triggers ahead of the vocabulary, so a
    /// trigger comes through as written for its snippet to be found.
    /// </summary>
    public List<string> DictationVocabulary => SnippetExpander.Vocabulary(Snippets, VocabularyList);

    // MARK: Snippets

    /// <summary>Spoken shortcuts: a trigger said in a dictation is replaced by its expansion (see SnippetExpander). None at first.</summary>
    public List<Snippet> Snippets
    {
        get
        {
            // Entry by entry: one edited by hand into null, or without its text, is left out rather than hiding the others.
            lock (_lock) return Snippet.ReadList(_store["snippets"]).Snippets;
        }
        private set => SetNode("snippets", JsonSerializer.SerializeToNode(value));
    }

    /// <summary>Adds the snippet, or replaces the one with its ID.</summary>
    public void SaveSnippet(Snippet snippet)
    {
        var snippets = Snippets;
        var index = snippets.FindIndex(s => s.Id == snippet.Id);
        if (index >= 0) snippets[index] = snippet; else snippets.Add(snippet);
        Snippets = snippets;
    }

    public void DeleteSnippet(Snippet snippet) => Snippets = Snippets.Where(s => s.Id != snippet.Id).ToList();

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
        if (!_writable) return;
        try
        {
            // Flushed to disk before it replaces the old file, so a power cut can't leave a file of zeros.
            SafeFile.WriteAllText(_path, _store.ToJsonString(FileJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next change writes again.
            AppLog.Debug("settings", $"can't save {_path}: {e.Message}");
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
