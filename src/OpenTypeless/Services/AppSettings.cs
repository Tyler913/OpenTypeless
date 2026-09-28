using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public int MaxRecordingMinutes
    {
        get => GetInt("maxRecordingMinutes", 20);
        set => SetValue("maxRecordingMinutes", Math.Clamp(value, 1, 60));
    }

    public bool DidEnableLaunchAtLoginByDefault
    {
        get => GetBool("didEnableLaunchAtLoginByDefault", false);
        set => SetValue("didEnableLaunchAtLoginByDefault", value);
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
