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
        [nameof(AppSettings.SttProvider), nameof(AppSettings.PolishProvider), nameof(AppSettings.PolishEnabled),
         nameof(AppSettings.PolishBackupEnabled), nameof(AppSettings.PolishBackupProvider), "ApiKeys", "BaseUrls"];

    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly SettingsWindow _window;
    private readonly SessionController _controller;
    private readonly ModelField _sttField = new(speech: true);
    private readonly ModelField _chatField = new(speech: false);
    private readonly ModelField _backupField = new(speech: false);
    private string? _sttKey, _chatKey, _backupKey;
    private CardRow? _sttRow, _chatRow, _backupRow;
    private readonly PriceEditor _sttPrice = new(speech: true);
    private readonly PriceEditor _chatPrice = new(speech: false);
    private readonly PriceEditor _backupPrice = new(speech: false);

    public ModelsPage(SettingsWindow window, SessionController controller) : base(SettingsPage.Models)
    {
        _window = window;
        _controller = controller;
        _sttField.TextChanged = text =>
        {
            _settings.SttModel = text;
            _sttPrice.Show(_settings.SttProvider, text);
        };
        _chatField.TextChanged = text =>
        {
            _settings.PolishModel = text;
            _chatPrice.Show(_settings.PolishProvider, text);
            _controller.RefreshModelInfo();
        };
        _backupField.TextChanged = text =>
        {
            _settings.PolishBackupModel = text;
            _backupPrice.Show(_settings.PolishBackupProvider, text);
            _controller.RefreshModelInfo();
        };
        _settings.PropertyChanged += OnSettingsChanged;
        PriceStore.Shared.Changed += OnPricesChanged;
        Render();
        PriceStore.Shared.RefreshIfStale();
    }

    protected override void OnClosed()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        PriceStore.Shared.Changed -= OnPricesChanged;
    }

    private void OnPricesChanged() => DispatcherQueue.TryEnqueue(ShowPrices);

    private void ShowPrices()
    {
        _sttPrice.Show(_settings.SttProvider, _settings.SttModel);
        _chatPrice.Show(_settings.PolishProvider, _settings.PolishModel);
        _backupPrice.Show(_settings.PolishBackupProvider, _settings.PolishBackupModel);
    }

    private static CardRow PriceRow(PriceEditor editor) =>
        new() { Glyph = Glyphs.Money, Tint = Tint.Green, Title = L("价格", "Price"), Trailing = editor };

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
        if (_backupRow != null) _backupRow.Trailing = null;
        foreach (var editor in new[] { _sttPrice, _chatPrice, _backupPrice })
        {
            if (editor.Row != null) editor.Row.Trailing = null;
            editor.Row = null;
        }
        _chatRow = null;
        _backupRow = null;
        _sttField.Text = _settings.SttModel;
        _chatField.Text = _settings.PolishModel;
        _backupField.Text = _settings.PolishBackupModel;

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
        var sttPriceRow = PriceRow(_sttPrice);
        _sttPrice.Row = sttPriceRow;
        stt.Body.Add(sttPriceRow);
        stt.Body.Add(new CardDivider());
        var language = new ComboBox { MinWidth = 140 };
        (string Code, string Name)[] languages =
        [
            ("", L("自动检测", "Auto-detect")),
            .. Localization.Supported.Select(l => (l.RawValue(), l == AppLanguage.Zh ? "中文" : l.NativeName())),
        ];
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
            polish.Body.Add(new CardDivider());
            var chatPriceRow = PriceRow(_chatPrice);
            _chatPrice.Row = chatPriceRow;
            polish.Body.Add(chatPriceRow);

            polish.Body.Add(new CardDivider());
            var backup = new ToggleSwitch { IsOn = _settings.PolishBackupEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
            backup.Toggled += (_, _) =>
            {
                _settings.PolishBackupEnabled = backup.IsOn;
                _controller.RefreshModelInfo();
            };
            var delay = HedgedPolish.DefaultHedgeDelay.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + L(" 秒", " s");
            polish.Body.Add(new CardRow
            {
                Glyph = Glyphs.Lightning, Tint = Tint.Orange, Title = L("备用模型", "Backup model"),
                Subtitle = L($"首选模型 {delay} 内还没开始输出、或请求失败时，同时请求备用模型，用先出字的那个",
                             $"If the main model hasn't started answering within {delay}, or fails, the backup is asked too and the first to answer wins"),
                Trailing = backup,
            });
            if (_settings.PolishBackupEnabled)
            {
                polish.Body.Add(new CardDivider());
                polish.Body.Add(new CardRow
                {
                    Glyph = Glyphs.Cloud, Tint = Tint.Indigo, Title = L("备用服务商", "Backup provider"),
                    Trailing = ProviderPicker(_settings.PolishBackupProvider, ProviderIdExtensions.All, id =>
                    {
                        _settings.SelectPolishBackupProvider(id);
                        _controller.RefreshModelInfo();
                    }),
                });
                polish.Body.Add(new CardDivider());
                _backupRow = new CardRow
                {
                    Glyph = Glyphs.Component, Tint = Tint.Pink, Title = L("备用模型 ID", "Backup model ID"),
                    Subtitle = L("选一个不同厂商的快速模型，两边不容易同时变慢", "Pick a fast model from another vendor so both are rarely slow at once"),
                    Trailing = _backupField,
                };
                polish.Body.Add(_backupRow);
                polish.Body.Add(new CardDivider());
                var backupPriceRow = PriceRow(_backupPrice);
                _backupPrice.Row = backupPriceRow;
                polish.Body.Add(backupPriceRow);
            }
        }
        Body.Children.Add(polish);
        if (_settings.PolishEnabled && !_settings.IsConfigured(_settings.PolishProvider))
        {
            Body.Children.Add(SetupBanner(_settings.PolishProvider));
        }
        else if (_settings.PolishEnabled && _settings.PolishBackupEnabled && _settings.PolishBackupProvider != _settings.PolishProvider
                 && !_settings.IsConfigured(_settings.PolishBackupProvider))
        {
            Body.Children.Add(SetupBanner(_settings.PolishBackupProvider));
        }

        ShowPrices();
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
        var backupKey = $"{_settings.PolishBackupProvider.RawValue()}|{_settings.ApiKey(_settings.PolishBackupProvider).Length}|{_settings.BaseUrl(_settings.PolishBackupProvider)}";
        if (_settings.PolishBackupEnabled && backupKey != _backupKey)
        {
            _backupKey = backupKey;
            _backupField.SetModels([]);
            _ = Load(_settings.PolishBackupProvider, false, backupKey, () => _backupKey, _backupField);
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

/// <summary>
/// A model's price, next to where it's chosen: OpenRouter's live price (read-only), or fields to enter the price
/// for any other provider, so the Home page can count what it costs.
/// </summary>
internal sealed class PriceEditor : UserControl
{
    private readonly bool _speech;
    private readonly AppSettings _settings = AppSettings.Shared;
    private string? _shown;

    /// <summary>The row the editor sits in, whose subtitle says where the price comes from.</summary>
    public CardRow? Row
    {
        get => _row;
        set
        {
            _row = value;
            _shown = null; // the new row needs its subtitle
        }
    }

    private CardRow? _row;

    public PriceEditor(bool speech)
    {
        _speech = speech;
    }

    public void Show(ProviderId provider, string model)
    {
        model = model.Trim();
        var catalog = PriceStore.Shared.Catalog;
        var key = $"{provider.RawValue()}|{model}|{catalog.FetchedAt:o}";
        if (key == _shown) return;
        _shown = key;

        if (provider == ProviderId.OpenRouter)
        {
            var price = catalog.Price(model);
            if (Row != null)
            {
                Row.Subtitle = catalog.IsEmpty
                    ? L("正在从 OpenRouter 获取最新价格…", "Getting the latest prices from OpenRouter…")
                    : L($"OpenRouter 实时价格，更新于 {Formatting.ShortStamp(catalog.FetchedAt)}。实际按每次请求的扣费计算。",
                        $"Live from OpenRouter, updated {Formatting.ShortStamp(catalog.FetchedAt)}. Each request counts what OpenRouter billed.");
            }
            string text;
            if (_speech) text = L("按实际扣费", "As billed");
            else if (price is { } p)
                text = L($"输入 {UsageFormat.Rate(p.InputPerMillion ?? 0)} · 输出 {UsageFormat.Rate(p.OutputPerMillion ?? 0)} / 百万 token",
                         $"In {UsageFormat.Rate(p.InputPerMillion ?? 0)} · Out {UsageFormat.Rate(p.OutputPerMillion ?? 0)} / 1M tokens");
            else text = model.Length == 0 ? "—" : L("OpenRouter 没有列出这个模型", "Not listed on OpenRouter");
            Content = Ui.Text(text, 12.5, foreground: Ui.Secondary);
            IsEnabled = true;
            return;
        }

        if (Row != null)
        {
            Row.Subtitle = _speech
                ? L("按录音时长计费，美元 / 分钟。留空则不计入花费。", "Per minute of audio, in USD. Leave empty to leave it out of the spend.")
                : L("美元 / 百万 token（服务商不返回用量时按字数估算）。留空则不计入花费。",
                    "USD per million tokens (estimated from the text when the server doesn't say). Leave empty to leave it out of the spend.");
        }
        var current = _settings.CustomPrice(provider, model);
        if (_speech)
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    Box(current?.PerMinute, value => Save(provider, model, (current ?? new ModelPrice()) with { PerMinute = value }, v => current = v)),
                    new TextBlock { Text = L("美元/分钟", "$/min"), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Secondary },
                },
            };
        }
        else
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = L("输入", "In"), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Secondary },
                    Box(current?.InputPerMillion, value => Save(provider, model, (current ?? new ModelPrice()) with { InputPerMillion = value }, v => current = v)),
                    new TextBlock { Text = L("输出", "Out"), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Secondary },
                    Box(current?.OutputPerMillion, value => Save(provider, model, (current ?? new ModelPrice()) with { OutputPerMillion = value }, v => current = v)),
                },
            };
        }
        IsEnabled = model.Length > 0;
    }

    private void Save(ProviderId provider, string model, ModelPrice price, Action<ModelPrice> remember)
    {
        _settings.SetCustomPrice(provider, model, price);
        remember(price);
    }

    private static NumberBox Box(double? value, Action<double?> changed)
    {
        var box = new NumberBox
        {
            Width = 92,
            Minimum = 0,
            Value = value ?? double.NaN,
            PlaceholderText = "$",
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0, IsDecimalPointAlwaysDisplayed = false },
        };
        box.ValueChanged += (_, e) => changed(double.IsNaN(e.NewValue) ? null : Math.Max(0, e.NewValue));
        return box;
    }
}
