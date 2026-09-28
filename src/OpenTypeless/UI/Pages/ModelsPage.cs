using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;

namespace OpenTypeless.UI;

public sealed class ModelsPage : PageBase
{
    private static readonly HashSet<string> StructuralProperties =
        [nameof(AppSettings.SttProvider), nameof(AppSettings.PolishProvider), nameof(AppSettings.PolishEnabled), "ApiKeys", "BaseUrls"];

    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly SettingsWindow _window;
    private readonly SessionController _controller;
    private readonly ModelField _sttField = new(speech: true);
    private readonly ModelField _chatField = new(speech: false);
    private string? _sttKey, _chatKey;
    private CardRow? _sttRow, _chatRow;

    public ModelsPage(SettingsWindow window, SessionController controller) : base(SettingsPage.Models)
    {
        _window = window;
        _controller = controller;
        _sttField.TextChanged = text => _settings.SttModel = text;
        _chatField.TextChanged = text =>
        {
            _settings.PolishModel = text;
            _controller.RefreshModelInfo();
        };
        _settings.PropertyChanged += OnSettingsChanged;
        Render();
    }

    protected override void OnClosed() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && StructuralProperties.Contains(name)) DispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        Body.Children.Clear();
        // The model fields survive re-renders (they keep focus and caret); release them from their old rows first.
        if (_sttRow != null) _sttRow.Trailing = null;
        if (_chatRow != null) _chatRow.Trailing = null;
        _chatRow = null;
        _sttField.Text = _settings.SttModel;
        _chatField.Text = _settings.PolishModel;

        var stt = new CardSection
        {
            Title = L("语音转文字", "Speech to text"),
            Footer = L("长语音会在停顿处自动切成 ≤28 秒的片段，边录边转，每片失败会自动重试。",
                       "Long recordings are split at pauses into ≤28 s segments that are transcribed while you talk, each retried on failure."),
        };
        stt.Body.Add(new CardRow
        {
            Glyph = Glyphs.Cloud, Tint = Tint.Indigo, Title = L("服务商", "Provider"),
            Trailing = ProviderPicker(_settings.SttProvider, ProviderIdExtensions.All.Where(p => p.SupportsStt()), _settings.SelectSttProvider),
        });
        stt.Body.Add(new CardDivider());
        _sttRow = new CardRow { Glyph = Glyphs.Microphone, Tint = Tint.Blue, Title = L("模型", "Model"), Trailing = _sttField };
        stt.Body.Add(_sttRow);
        stt.Body.Add(new CardDivider());
        var language = new ComboBox { MinWidth = 140 };
        (string Code, string Name)[] languages = [("", L("自动检测", "Auto-detect")), ("zh", "中文"), ("en", "English"), ("ja", "日本語"), ("ko", "한국어")];
        foreach (var (_, name) in languages) language.Items.Add(name);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(languages, l => l.Code == _settings.SttLanguage));
        language.SelectionChanged += (_, _) => _settings.SttLanguage = languages[Math.Max(0, language.SelectedIndex)].Code;
        stt.Body.Add(new CardRow
        {
            Glyph = Glyphs.Message, Tint = Tint.Teal, Title = L("说话语言", "Spoken language"),
            Subtitle = L("固定语言可以提高准确率；中英混说请选自动", "Fixing it can help accuracy; use Auto for mixed speech"),
            Trailing = language,
        });
        Body.Children.Add(stt);
        if (!_settings.IsConfigured(_settings.SttProvider)) Body.Children.Add(SetupBanner(_settings.SttProvider));

        var polish = new CardSection { Title = L("文字整理", "Clean-up") };
        var enabled = new ToggleSwitch { IsOn = _settings.PolishEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
        enabled.Toggled += (_, _) => _settings.PolishEnabled = enabled.IsOn;
        polish.Body.Add(new CardRow
        {
            Glyph = Glyphs.StarFill, Tint = Tint.Purple, Title = L("用 AI 整理文字", "Clean up with AI"),
            Subtitle = L("去掉口头禅和自我修正，自动分段、整理成列表", "Removes fillers and false starts, adds paragraphs and lists"),
            Trailing = enabled,
        });
        if (_settings.PolishEnabled)
        {
            polish.Body.Add(new CardDivider());
            polish.Body.Add(new CardRow
            {
                Glyph = Glyphs.Cloud, Tint = Tint.Indigo, Title = L("服务商", "Provider"),
                Trailing = ProviderPicker(_settings.PolishProvider, ProviderIdExtensions.All, id =>
                {
                    _settings.SelectPolishProvider(id);
                    _controller.RefreshModelInfo();
                }),
            });
            polish.Body.Add(new CardDivider());
            _chatRow = new CardRow
            {
                Glyph = Glyphs.Component, Tint = Tint.Pink, Title = L("模型", "Model"),
                Subtitle = _settings.PolishProvider == ProviderId.OpenRouter ? L("推理会自动关闭或降到最低，以减少延迟", "Reasoning is turned off or minimised for speed") : null,
                Trailing = _chatField,
            };
            polish.Body.Add(_chatRow);
        }
        Body.Children.Add(polish);
        if (_settings.PolishEnabled && !_settings.IsConfigured(_settings.PolishProvider)) Body.Children.Add(SetupBanner(_settings.PolishProvider));

        LoadModelsIfNeeded();
    }

    private Banner SetupBanner(ProviderId id)
    {
        var button = new Button { Content = L("去填写", "Add key") };
        button.Click += (_, _) => _window.Navigate(SettingsPage.Providers);
        return new Banner(Glyphs.Warning, Tint.Orange, L($"{id.DisplayName()} 还没有配置 API Key", $"{id.DisplayName()} has no API key yet"), button);
    }

    private DropDownButton ProviderPicker(ProviderId selection, IEnumerable<ProviderId> options, Action<ProviderId> onSelect)
    {
        var flyout = new MenuFlyout();
        foreach (var id in options)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = id == selection || _settings.IsConfigured(id) ? id.DisplayName() : id.DisplayName() + L("（未配置）", " (not set)"),
                IsChecked = id == selection,
            };
            item.Click += (_, _) => onSelect(id);
            flyout.Items.Add(item);
        }
        return new DropDownButton { Content = selection.DisplayName(), Flyout = flyout };
    }

    private void LoadModelsIfNeeded()
    {
        var sttKey = $"{_settings.SttProvider.RawValue()}|{_settings.ApiKey(_settings.SttProvider).Length}|{_settings.BaseUrl(_settings.SttProvider)}";
        if (sttKey != _sttKey)
        {
            _sttKey = sttKey;
            _sttField.SetModels([]);
            _ = Load(_settings.SttProvider, true, sttKey, () => _sttKey, _sttField);
        }
        var chatKey = $"{_settings.PolishProvider.RawValue()}|{_settings.ApiKey(_settings.PolishProvider).Length}|{_settings.BaseUrl(_settings.PolishProvider)}";
        if (chatKey != _chatKey)
        {
            _chatKey = chatKey;
            _chatField.SetModels([]);
            _ = Load(_settings.PolishProvider, false, chatKey, () => _chatKey, _chatField);
        }
    }

    private async Task Load(ProviderId id, bool speech, string key, Func<string?> currentKey, ModelField field)
    {
        var models = await LoadModels(id, speech);
        if (currentKey() == key) field.SetModels(models);
    }

    private async Task<List<ModelInfo>> LoadModels(ProviderId id, bool speech)
    {
        if (_settings.Endpoint(id) is not { } endpoint) return [];
        List<ModelInfo> models;
        try
        {
            models = await new ApiClient(endpoint).ListModels(speech && id == ProviderId.OpenRouter ? "transcription" : null);
        }
        catch
        {
            return [];
        }
        if (id == ProviderId.OpenRouter) return models.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        // Other providers list every model together; split them by name.
        var filtered = models.Where(m => m.LooksLikeSpeechModel == speech).ToList();
        return (filtered.Count == 0 ? models : filtered).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
    }
}

/// <summary>Free-text model ID with a browse menu of the provider's models (grouped by vendor when long).</summary>
internal sealed class ModelField : UserControl
{
    private readonly TextBox _text = new()
    {
        Width = 260,
        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
        FontSize = 12.5,
        PlaceholderText = L("模型 ID", "Model ID"),
    };
    private readonly DropDownButton _browse = new();
    private bool _setting;

    public Action<string>? TextChanged;

    public ModelField(bool speech)
    {
        _text.TextChanged += (_, _) =>
        {
            if (!_setting) TextChanged?.Invoke(_text.Text.Trim());
        };
        _browse.Content = Ui.Icon(Glyphs.List, 14);
        Ui.Label(_browse, L("浏览可用模型", "Browse models"));
        SetModels([]);
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _text, _browse } };
    }

    public string Text
    {
        get => _text.Text;
        set
        {
            if (_text.Text == value) return;
            _setting = true;
            _text.Text = value;
            _setting = false;
        }
    }

    public void SetModels(IReadOnlyList<ModelInfo> models)
    {
        var flyout = new MenuFlyout();
        if (models.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = L("填写 API Key 后可浏览模型", "Add an API key to browse models"), IsEnabled = false });
        }
        else if (models.Count > 40)
        {
            foreach (var group in models.GroupBy(m => m.Id.Split('/')[0]).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var sub = new MenuFlyoutSubItem { Text = group.Key };
                foreach (var model in group) sub.Items.Add(Item(model));
                flyout.Items.Add(sub);
            }
        }
        else
        {
            foreach (var model in models) flyout.Items.Add(Item(model));
        }
        _browse.Flyout = flyout;
    }

    private MenuFlyoutItem Item(ModelInfo model)
    {
        var item = new MenuFlyoutItem { Text = model.Id };
        item.Click += (_, _) =>
        {
            _text.Text = model.Id; // raises TextChanged → saved
        };
        return item;
    }
}
