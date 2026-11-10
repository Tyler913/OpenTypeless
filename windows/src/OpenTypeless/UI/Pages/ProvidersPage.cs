using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

/// <summary>
/// The providers that are set up (in use, or with a key or address of their own), and an Add button with the rest, grouped
/// by what they do, so the page stays short however many providers there are.
/// </summary>
public sealed class ProvidersPage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly List<ProviderCard> _cards = new();
    private readonly StackPanel _stack = new() { Spacing = 8 };
    private readonly HashSet<ProviderId> _expanded;
    /// <summary>Shown since the page opened, so a provider whose key was just cleared doesn't vanish under the pointer.</summary>
    private readonly HashSet<ProviderId> _shown;

    public ProvidersPage() : base(SettingsPage.Providers, help: L("填写 API Key。语音转文字和文字整理可以使用不同的服务商。",
                                                                  "Add API keys. Speech-to-text and clean-up can use different providers.")
                                                                + "\n\n" + L("这里只列出已经设置的服务商，其他的在「添加服务商」里。",
                                                                             "Only the providers you've set up are listed; add others with Add provider."))
    {
        _expanded = [_settings.SttProvider, _settings.PolishProvider];
        _shown = ProviderIdExtensions.All.Where(_settings.IsSetUp).ToHashSet();
        Body.Children.Add(_stack);
        Render();
        _settings.PropertyChanged += OnSettingsChanged;
    }

    protected override void OnClosed() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => _cards.ForEach(c => c.RefreshHeader()));

    private void Render()
    {
        _stack.Children.Clear();
        _cards.Clear();
        foreach (var id in ProviderIdExtensions.All.Where(p => _shown.Contains(p) || _settings.IsSetUp(p)))
        {
            var card = new ProviderCard(id, _expanded.Contains(id),
                expanded =>
                {
                    if (expanded) _expanded.Add(id); else _expanded.Remove(id);
                },
                () =>
                {
                    _shown.Remove(id);
                    _expanded.Remove(id);
                    Render();
                });
            _cards.Add(card);
            _stack.Children.Add(card);
        }
        var available = ProviderIdExtensions.All.Where(p => !_cards.Any(c => c.Id == p)).ToList();
        if (available.Count == 0) return;
        var label = L("添加服务商", "Add provider");
        var add = new DropDownButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { Ui.Icon(Glyphs.Add, 12), new TextBlock { Text = label } },
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, label);
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in Enum.GetValues<ProviderGroup>())
        {
            var ids = available.Where(p => p.Group() == group).ToList();
            if (ids.Count == 0) continue;
            var sub = new MenuFlyoutSubItem { Text = GroupTitle(group) };
            foreach (var id in ids)
            {
                var item = new MenuFlyoutItem { Text = id.DisplayName() };
                item.Click += (_, _) =>
                {
                    _settings.AddProvider(id);
                    _shown.Add(id);
                    _expanded.Add(id);
                    Render();
                };
                sub.Items.Add(item);
            }
            flyout.Items.Add(sub);
        }
        add.Flyout = flyout;
        _stack.Children.Add(add);
    }

    /// <summary>The heading of the providers of this group in the Add menu.</summary>
    internal static string GroupTitle(ProviderGroup group) => group switch
    {
        ProviderGroup.Both => L("语音转文字和文字整理", "Speech-to-text and clean-up"),
        ProviderGroup.Chat => L("文字整理", "Clean-up"),
        ProviderGroup.Speech => L("语音转文字", "Speech to text"),
        _ => L("本机或自建服务", "On this computer or your own server"),
    };
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
    private readonly Button _remove = new();
    private readonly Border _footerDivider = new() { Height = 1 };
    private readonly Grid _footer = new() { Padding = new Thickness(14, 10, 14, 10) };
    private readonly Action<bool> _onExpanded;
    private readonly StackPanel _testResult = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Shown while Credential Manager refuses the key, which then only lasts until the app quits.</summary>
    private readonly TextBlock _keyError = Ui.Text(L("无法保存到 Windows 凭据管理器，应用退出后需要重新填写这个 Key",
                                                     "Couldn't save to Windows Credential Manager; you'll need to enter this key again after the app quits"),
                                                   12, foreground: Palette.Brush(Tint.Red), wrap: true);
    private bool _expanded;
    private bool _testing;

    public ProviderId Id => _id;

    /// <param name="onExpanded">Told when the card is opened or closed, so the page keeps it that way when it's rebuilt.</param>
    /// <param name="onRemoved">Called once Remove has taken the provider off the page.</param>
    public ProviderCard(ProviderId id, bool expanded, Action<bool> onExpanded, Action onRemoved)
    {
        _id = id;
        _expanded = expanded;
        _onExpanded = onExpanded;

        // Header (the whole row toggles the card).
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Text(id.DisplayName(), 13.5, FontWeights.Medium), _tags },
        };
        var header = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        _chevron.Foreground = Ui.Tertiary;
        _chevron.VerticalAlignment = VerticalAlignment.Center;
        _chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        AddTo(header, name, 0);
        AddTo(header, _status, 1);
        AddTo(header, _chevron, 2);
        var toggle = new Button
        {
            Content = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, id.DisplayName());
        toggle.Click += (_, _) =>
        {
            _expanded = !_expanded;
            _onExpanded(_expanded);
            UpdateExpansion();
        };

        _divider.Background = Palette.Theme("DividerStrokeColorDefaultBrush");
        _footerDivider.Background = Palette.Theme("DividerStrokeColorDefaultBrush");
        BuildDetails();
        BuildFooter(onRemoved);
        Content = Card.Make(new StackPanel { Children = { toggle, _divider, _details, _footerDivider, _footer } });
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
        _divider.Visibility = _details.Visibility = _footerDivider.Visibility = _footer.Visibility =
            _expanded ? Visibility.Visible : Visibility.Collapsed;
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
        // Nothing to say once the provider works; only a missing key gets a pill.
        _status.Text = L("未配置", "Not set");
        _status.Tint = Tint.Secondary;
        _status.Visibility = _settings.IsConfigured(_id) ? Visibility.Collapsed : Visibility.Visible;
        _reset.Visibility = _id != ProviderId.Custom && _settings.BaseUrl(_id) != _id.DefaultBaseUrl() ? Visibility.Visible : Visibility.Collapsed;
        _test.IsEnabled = !_testing && _settings.Endpoint(_id) != null;
        // A provider a step uses stays: switch the step to another one first.
        var inUse = _settings.IsInUse(_id);
        _remove.IsEnabled = !inUse;
        ToolTipService.SetToolTip(_remove, inUse
            ? L("正在使用，先在「模型」里换成别的服务商", "In use: choose another provider under Models first")
            : L("移除这个服务商和它的 API Key", "Removes this provider and its API key"));
    }

    /// <summary>What the provider can do here, and Remove.</summary>
    private void BuildFooter(Action onRemoved)
    {
        var steps = _id.SupportsStt() && _id.SupportsChat() ? ProvidersPage.GroupTitle(ProviderGroup.Both)
            : _id.SupportsStt() ? L("仅语音转文字", "Speech-to-text only") : L("仅文字整理", "Clean-up only");
        var text = Ui.Text(steps, 12, foreground: Ui.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.Children.Add(text);
        var removeLabel = L("移除", "Remove");
        _remove.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { Ui.Icon(Glyphs.Delete, 12), new TextBlock { Text = removeLabel } },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_remove, removeLabel);
        _remove.Click += (_, _) =>
        {
            if (_settings.IsInUse(_id)) return;
            _settings.RemoveProvider(_id);
            onRemoved();
        };
        Grid.SetColumn(_remove, 1);
        _footer.Children.Add(_remove);
    }

    private IEnumerable<string> UsageTags()
    {
        if (_settings.SttProvider == _id) yield return L("转写中使用", "Speech-to-text");
        if (_settings.PolishEnabled && _settings.PolishProvider == _id) yield return L("整理中使用", "Clean-up");
    }

    private void BuildDetails()
    {
        // API key: the saved one, so OPENROUTER_API_KEY (used in its place when set) is never shown or saved here.
        var key = new PasswordBox { PlaceholderText = _id.KeyPlaceholder(), Password = _settings.SavedApiKey(_id) };
        key.PasswordChanged += (_, _) => ShowKeySaved(_settings.SetApiKey(key.Password, _id));
        var paste = new Button { Content = L("粘贴", "Paste") };
        paste.Click += async (_, _) =>
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                // PasswordChanged saves it (and shows whether that worked).
                key.Password = (await content.GetTextAsync()).Trim();
            }
        };
        _details.Children.Add(FieldRow(L("API Key", "API Key"), key, paste));
        if (_id == ProviderId.OpenRouter && AppSettings.OpenRouterKeyFromEnvironment != null)
        {
            var note = Ui.Text(L("正在使用环境变量 OPENROUTER_API_KEY 中的 Key；没有设置这个变量时才用这里保存的 Key",
                                 "Using the key in the OPENROUTER_API_KEY environment variable; the key saved here is used when it isn't set"),
                               12, foreground: Ui.Secondary, wrap: true);
            note.Margin = new Thickness(104, 0, 0, 0);
            _details.Children.Add(note);
        }
        _keyError.Margin = new Thickness(104, 0, 0, 0);
        _keyError.Visibility = Visibility.Collapsed;
        _details.Children.Add(_keyError);

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
        _details.Children.Add(FieldRow(L("接口地址", "Base URL"), url, _reset));

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
            bottom.Children.Add(Ui.Text(_id.IsLocal()
                                            ? L("运行在这台电脑上，不需要 API Key", "Runs on this computer; no API key needed")
                                            : L("任何兼容 OpenAI 接口的服务：/audio/transcriptions 与 /chat/completions",
                                                "Any OpenAI-compatible server: /audio/transcriptions and /chat/completions"),
                                        12, foreground: Ui.Secondary, wrap: true));
        }
        _testResult.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_testResult, 1);
        bottom.Children.Add(_testResult);
        _test.Content = L("测试连接", "Test");
        _test.Click += (_, _) => Test();
        ToolTipService.SetToolTip(_test, L("检查 API Key，并测三次往返延迟取中位数", "Checks the API key and times three round trips (median)"));
        Grid.SetColumn(_test, 2);
        bottom.Children.Add(_test);
        _details.Children.Add(bottom);
    }

    private void ShowKeySaved(bool saved) => _keyError.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;

    private static Grid FieldRow(string label, FrameworkElement field, Button button)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(96) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var text = Ui.Text(label, 12, FontWeights.Medium, Ui.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
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
        (bool Ok, string Text, Tint Tint) result;
        try
        {
            // Three round trips; the median latency shows how quickly this provider answers from here.
            var check = await new ApiClient(endpoint).CheckConnection();
            result = (true, check.Summary, check.Speed switch
            {
                ConnectionCheck.SpeedRating.Fast => Tint.Green,
                ConnectionCheck.SpeedRating.Fine => Tint.Orange,
                _ => Tint.Red,
            });
        }
        catch (Exception error)
        {
            result = (false, ApiException.From(error).Message, Tint.Red);
        }
        _testing = false;
        _test.Content = L("测试连接", "Test");
        var tint = result.Tint;
        var text = Ui.Text(result.Text, 12, foreground: Palette.Brush(tint));
        text.MaxLines = 2;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxWidth = 320;
        ToolTipService.SetToolTip(text, result.Text);
        _testResult.Children.Add(Ui.Icon(result.Ok ? Glyphs.Completed : Glyphs.ErrorBadge, 13, Palette.Brush(tint), filled: true));
        _testResult.Children.Add(text);
        RefreshHeader();
    }
}
