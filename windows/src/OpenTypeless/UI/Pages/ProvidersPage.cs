using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

public static class ProviderStyle
{
    public static string Glyph(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => Glyphs.Link,
        ProviderId.OpenAI => Glyphs.Star,
        ProviderId.Groq => Glyphs.Lightning,
        ProviderId.SiliconFlow => Glyphs.Cloud,
        ProviderId.DeepSeek => Glyphs.Search,
        _ => Glyphs.Code,
    };

    public static Tint Tint(this ProviderId id) => id switch
    {
        ProviderId.OpenRouter => UI.Tint.Indigo,
        ProviderId.OpenAI => UI.Tint.Teal,
        ProviderId.Groq => UI.Tint.Orange,
        ProviderId.SiliconFlow => UI.Tint.Purple,
        ProviderId.DeepSeek => UI.Tint.Blue,
        _ => UI.Tint.Gray,
    };

    public static string Capabilities(this ProviderId id) =>
        id.SupportsStt() ? L("语音转文字 · 文字整理", "Speech-to-text · Clean-up") : L("文字整理", "Clean-up");
}

public sealed class ProvidersPage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly List<ProviderCard> _cards = new();

    public ProvidersPage() : base(SettingsPage.Providers)
    {
        var expanded = new HashSet<ProviderId> { _settings.SttProvider, _settings.PolishProvider };
        var stack = new StackPanel { Spacing = 10 };
        foreach (var id in ProviderIdExtensions.All)
        {
            var card = new ProviderCard(id, expanded.Contains(id));
            _cards.Add(card);
            stack.Children.Add(card);
        }
        Body.Children.Add(stack);
        _settings.PropertyChanged += OnSettingsChanged;
    }

    protected override void OnClosed() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => _cards.ForEach(c => c.RefreshHeader()));
}

internal sealed class ProviderCard : UserControl
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly ProviderId _id;
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StatusPill _status = new();
    private readonly FontIcon _chevron = Ui.Icon(Glyphs.ChevronRight, 11);
    private readonly Border _divider = new() { Height = 1 };
    private readonly StackPanel _details = new() { Spacing = 12, Padding = new Thickness(14) };
    private readonly Button _reset = new();
    private readonly Button _test = new();
    private readonly StackPanel _testResult = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private bool _expanded;
    private bool _testing;

    public ProviderCard(ProviderId id, bool expanded)
    {
        _id = id;
        _expanded = expanded;

        // Header (the whole row toggles the card).
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Ui.Text(id.DisplayName(), 14, FontWeights.SemiBold), _tags } };
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { name, Ui.Text(id.Capabilities(), 12, foreground: Ui.Secondary) } };
        var header = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        _chevron.Foreground = Ui.Tertiary;
        _chevron.VerticalAlignment = VerticalAlignment.Center;
        _chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        AddTo(header, new IconBadge { Glyph = id.Glyph(), Tint = id.Tint(), BadgeSize = 30 }, 0);
        AddTo(header, titles, 1);
        AddTo(header, _status, 2);
        AddTo(header, _chevron, 3);
        var toggle = new Button
        {
            Content = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 12, 14, 12),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, id.DisplayName());
        toggle.Click += (_, _) =>
        {
            _expanded = !_expanded;
            UpdateExpansion();
        };

        _divider.Background = Palette.Theme("DividerStrokeColorDefaultBrush");
        BuildDetails();
        Content = Card.Make(new StackPanel { Children = { toggle, _divider, _details } });
        RefreshHeader();
        UpdateExpansion();
    }

    private static void AddTo(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private void UpdateExpansion()
    {
        _divider.Visibility = _details.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _chevron.RenderTransform = new RotateTransform { Angle = _expanded ? 90 : 0 };
    }

    public void RefreshHeader()
    {
        _tags.Children.Clear();
        foreach (var tag in UsageTags())
        {
            _tags.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 2),
                Background = Palette.Brush(Tint.Accent, 0.14),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Text(tag, 10.5, FontWeights.Medium, Palette.Brush(Tint.Accent)),
            });
        }
        var configured = _settings.IsConfigured(_id);
        _status.Text = configured ? L("已配置", "Ready") : L("未配置", "Not set");
        _status.Tint = configured ? Tint.Green : Tint.Secondary;
        _reset.Visibility = _id != ProviderId.Custom && _settings.BaseUrl(_id) != _id.DefaultBaseUrl() ? Visibility.Visible : Visibility.Collapsed;
        _test.IsEnabled = !_testing && _settings.Endpoint(_id) != null;
    }

    private IEnumerable<string> UsageTags()
    {
        if (_settings.SttProvider == _id) yield return L("转写中使用", "Speech-to-text");
        if (_settings.PolishEnabled && _settings.PolishProvider == _id) yield return L("整理中使用", "Clean-up");
    }

    private void BuildDetails()
    {
        // API key
        var key = new PasswordBox { PlaceholderText = _id.KeyPlaceholder(), Password = _settings.ApiKey(_id) };
        key.PasswordChanged += (_, _) => _settings.SetApiKey(key.Password, _id);
        var paste = new Button { Content = L("粘贴", "Paste") };
        paste.Click += async (_, _) =>
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                _settings.SetApiKey(text, _id);
                key.Password = _settings.ApiKey(_id);
            }
        };
        _details.Children.Add(FieldRow("API Key", key, paste));

        // Base URL
        var url = new TextBox
        {
            PlaceholderText = _id == ProviderId.Custom ? "http://localhost:8000/v1" : _id.DefaultBaseUrl(),
            Text = _settings.BaseUrl(_id),
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            FontSize = 12.5,
        };
        url.TextChanged += (_, _) => _settings.SetBaseUrl(url.Text, _id);
        _reset.Content = L("恢复默认", "Reset");
        _reset.Click += (_, _) =>
        {
            _settings.SetBaseUrl(_id.DefaultBaseUrl(), _id);
            url.Text = _settings.BaseUrl(_id);
        };
        _details.Children.Add(FieldRow("Base URL", url, _reset));

        // Link / hint, test result, test button
        var bottom = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        if (_id.KeysUrl() is { } keysUrl)
        {
            var link = new HyperlinkButton
            {
                Padding = new Thickness(0),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Ui.Text(L("获取 API Key", "Get an API key"), 12.5), Ui.Icon(Glyphs.OpenInNew, 11) },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, L("获取 API Key", "Get an API key"));
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(keysUrl) { UseShellExecute = true });
            bottom.Children.Add(link);
        }
        else
        {
            bottom.Children.Add(Ui.Text(L("任何兼容 OpenAI 接口的服务：/audio/transcriptions 与 /chat/completions",
                                          "Any OpenAI-compatible server: /audio/transcriptions and /chat/completions"), 12, foreground: Ui.Secondary, wrap: true));
        }
        _testResult.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_testResult, 1);
        bottom.Children.Add(_testResult);
        _test.Content = L("测试连接", "Test");
        _test.Click += (_, _) => Test();
        Grid.SetColumn(_test, 2);
        bottom.Children.Add(_test);
        _details.Children.Add(bottom);
    }

    private static Grid FieldRow(string label, FrameworkElement field, Button button)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(72) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var text = Ui.Text(label, 12, FontWeights.Medium, Ui.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return grid;
    }

    private async void Test()
    {
        if (_settings.Endpoint(_id) is not { } endpoint) return;
        _testing = true;
        _test.Content = L("测试中…", "Testing…");
        _testResult.Children.Clear();
        RefreshHeader();
        (bool Ok, string Text) result;
        try
        {
            result = (true, await new ApiClient(endpoint).VerifyCredentials());
        }
        catch (Exception error)
        {
            result = (false, ApiException.From(error).Message);
        }
        _testing = false;
        _test.Content = L("测试连接", "Test");
        var tint = result.Ok ? Tint.Green : Tint.Red;
        var text = Ui.Text(result.Text, 12, foreground: Palette.Brush(tint));
        text.MaxLines = 2;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxWidth = 320;
        ToolTipService.SetToolTip(text, result.Text);
        _testResult.Children.Add(Ui.Icon(result.Ok ? Glyphs.Completed : Glyphs.ErrorBadge, 13, Palette.Brush(tint)));
        _testResult.Children.Add(text);
        RefreshHeader();
    }
}
