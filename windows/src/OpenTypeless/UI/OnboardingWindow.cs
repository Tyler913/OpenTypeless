using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OpenTypeless.Native;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;
using Windows.Graphics;

namespace OpenTypeless.UI;

/// <summary>
/// The guide on the very first launch: welcome, an API key, a first dictation, done. It is shown once; whether it
/// was finished, skipped or closed, later launches start as usual.
/// </summary>
public static class Onboarding
{
    /// <summary>
    /// Whether this launch shows the guide. Someone updating from a version without it already has a working
    /// setup, so for them it's marked as seen instead.
    /// </summary>
    public static bool ShouldShow(AppSettings settings)
    {
        if (settings.DidShowOnboarding) return false;
        if (settings.IsConfigured(settings.SttProvider))
        {
            settings.DidShowOnboarding = true;
            return false;
        }
        return true;
    }

    /// <summary>The providers offered, recommended first. Each of them does both steps; the rest are in Settings → Providers.</summary>
    public static readonly ProviderId[] Providers = [ProviderId.OpenRouter, ProviderId.OpenAI, ProviderId.Groq, ProviderId.SiliconFlow];

    /// <summary>
    /// With an OpenRouter key: Microsoft MAI-Transcribe for speech-to-text, Gemini 3.1 Flash Lite (the fastest
    /// clean-up model in eval/README.md) for clean-up.
    /// </summary>
    public static (string Stt, string Chat) Models(ProviderId id) =>
        id == ProviderId.OpenRouter ? ("microsoft/mai-transcribe-2", "google/gemini-3.1-flash-lite") : (id.DefaultSttModel(), id.DefaultChatModel());

    /// <summary>Uses this provider and key for both speech-to-text and clean-up.</summary>
    public static void Apply(ProviderId id, string key, AppSettings settings)
    {
        var (stt, chat) = Models(id);
        settings.SetApiKey(key, id);
        settings.SttProvider = id;
        settings.SttModel = stt;
        settings.PolishProvider = id;
        settings.PolishModel = chat;
    }
}

public enum OnboardingStep { Welcome, Provider, TryIt, Done }

/// <summary>The guide's own window; closing it at any step ends the guide.</summary>
public sealed class OnboardingWindow : Window
{
    private const string ArrowGlyph = "";

    private readonly SessionController _controller;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly Action _onClose;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ContentControl _stepHost = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        ContentTransitions = new TransitionCollection { new ContentThemeTransition { HorizontalOffset = 48 } },
    };
    private readonly Grid _footer = new() { Padding = new Thickness(28, 0, 28, 24), Height = 76 };
    private readonly DispatcherQueueTimer _permissionTimer;
    private OnboardingStep _step;
    private bool _closingForExit;

    // Provider step
    private ProviderId _provider = ProviderId.OpenRouter;
    private string _key = "";
    private Button? _continue;

    // Try step
    private string _tryText = "";
    private string? _tryError;
    private DateTimeOffset _tryStarted = DateTimeOffset.Now;
    private TextBox? _tryBox;
    private TextBlock? _tryStatus;
    private bool _micGranted = Permissions.MicrophoneGranted;

    public nint Hwnd { get; }

    public OnboardingWindow(SessionController controller, Action onClose, OnboardingStep step = OnboardingStep.Welcome)
    {
        _controller = controller;
        _onClose = onClose;
        _step = step;
        Title = "OpenTypeless";
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.Resize(new SizeInt32(Scaled(640), Scaled(620)));
        Center();

        var titleBar = new Grid { Height = 40, Background = new SolidColorBrush(Colors.Transparent) };
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        // A soft wash of the brand colours behind the top of every step.
        var wash = new Border
        {
            Height = 220,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.16,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.5, 0),
                EndPoint = new Windows.Foundation.Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop { Color = Palette.BrandStart, Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, Palette.BrandEnd.R, Palette.BrandEnd.G, Palette.BrandEnd.B), Offset = 1 },
                },
            },
        };
        Grid.SetRowSpan(wash, 3);
        root.Children.Add(wash);
        root.Children.Add(titleBar);
        _dots.Margin = new Thickness(0, 0, 0, 0);
        Grid.SetRow(_dots, 1);
        root.Children.Add(_dots);
        Grid.SetRow(_stepHost, 2);
        root.Children.Add(_stepHost);
        Grid.SetRow(_footer, 3);
        root.Children.Add(_footer);
        Content = root;
        SetTitleBar(titleBar);

        _permissionTimer = DispatcherQueue.CreateTimer();
        _permissionTimer.Interval = TimeSpan.FromSeconds(1.5);
        _permissionTimer.Tick += (_, _) =>
        {
            if (_step != OnboardingStep.TryIt || Permissions.MicrophoneGranted == _micGranted) return;
            _micGranted = Permissions.MicrophoneGranted;
            Render(animated: false);
        };
        _permissionTimer.Start();

        _controller.StateChanged += OnSessionChanged;
        _history.Changed += OnSessionChanged;
        Closed += (_, _) =>
        {
            _permissionTimer.Stop();
            _controller.StateChanged -= OnSessionChanged;
            _history.Changed -= OnSessionChanged;
            if (!_closingForExit) _onClose();
        };
        Render(animated: false);
    }

    public void Show()
    {
        AppWindow.Show();
        Activate();
        ForegroundHelper.Bring(Hwnd);
    }

    /// <summary>Closes without handing over to the rest of the app, when it quits.</summary>
    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
    }

    private void Go(OnboardingStep step)
    {
        _step = step;
        if (step == OnboardingStep.TryIt) _tryStarted = DateTimeOffset.Now;
        Render(animated: true);
    }

    private void Render(bool animated)
    {
        _dots.Children.Clear();
        foreach (var step in Enum.GetValues<OnboardingStep>())
        {
            var current = step == _step;
            _dots.Children.Add(new Border
            {
                Width = current ? 22 : 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = current ? Palette.Brand() : Palette.Brush(Tint.Gray, 0.35),
            });
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_dots,
            L($"第 {(int)_step + 1} 步，共 {Enum.GetValues<OnboardingStep>().Length} 步", $"Step {(int)_step + 1} of {Enum.GetValues<OnboardingStep>().Length}"));

        var transitions = _stepHost.ContentTransitions;
        if (!animated) _stepHost.ContentTransitions = null;
        _stepHost.Content = _step switch
        {
            OnboardingStep.Welcome => WelcomeStep(),
            OnboardingStep.Provider => ProviderStep(),
            OnboardingStep.TryIt => TryStep(),
            _ => DoneStep(),
        };
        if (!animated) _stepHost.ContentTransitions = transitions;
        RenderFooter();
    }

    private void RenderFooter()
    {
        _footer.Children.Clear();
        _continue = null;
        switch (_step)
        {
            case OnboardingStep.Welcome:
                _footer.Children.Add(SkipButton(L("跳过引导", "Skip the tour"), Close, HorizontalAlignment.Center));
                break;
            case OnboardingStep.Provider:
                _footer.Children.Add(SkipButton(L("稍后设置", "Set up later"), () => Go(OnboardingStep.TryIt), HorizontalAlignment.Left));
                _continue = PrimaryButton(L("继续", "Continue"), SaveKey);
                _continue.IsEnabled = _key.Trim().Length > 0;
                _footer.Children.Add(_continue);
                break;
            case OnboardingStep.TryIt:
                _footer.Children.Add(SkipButton(L("跳过", "Skip"), () => Go(OnboardingStep.Done), HorizontalAlignment.Left));
                _footer.Children.Add(PrimaryButton(L("继续", "Continue"), () => Go(OnboardingStep.Done)));
                break;
            default:
                _footer.Children.Add(PrimaryButton(L("开始使用", "Start dictating"), Close));
                break;
        }
    }

    private static Button SkipButton(string text, Action action, HorizontalAlignment alignment)
    {
        var button = new Button
        {
            Content = Ui.Text(text, 13, foreground: Ui.Secondary),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button PrimaryButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 120,
            Padding = new Thickness(20, 8, 20, 8),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel Header(string title, string subtitle) => new()
    {
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 22, 0, 0),
        Children =
        {
            Centered(Ui.Text(title, 26, FontWeights.SemiBold)),
            Centered(Ui.Text(subtitle, 13, foreground: Ui.Secondary, wrap: true), maxWidth: 520),
        },
    };

    private static TextBlock Centered(TextBlock text, double maxWidth = double.PositiveInfinity)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        text.MaxWidth = maxWidth;
        return text;
    }

    private static StackPanel StepPanel(double spacing) => new()
    {
        Spacing = spacing,
        Padding = new Thickness(44, 0, 44, 0),
        VerticalAlignment = VerticalAlignment.Top,
    };

    // MARK: Welcome

    private FrameworkElement WelcomeStep()
    {
        var grid = new Grid
        {
            Padding = new Thickness(40, 0, 40, 18),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        hero.Children.Add(new AppTile { TileSize = 104, HorizontalAlignment = HorizontalAlignment.Center });
        var name = Centered(Ui.Text("OpenTypeless", 38, FontWeights.Bold));
        name.Margin = new Thickness(0, 26, 0, 0);
        hero.Children.Add(name);
        var tagline = Centered(Ui.Text(L("按住一个键说话，松开后整理好的文字就出现在光标处。", "Hold a key and speak. Let go, and clean text appears at your cursor."),
                                       14, foreground: Ui.Secondary, wrap: true));
        tagline.Margin = new Thickness(0, 10, 0, 0);
        hero.Children.Add(tagline);
        hero.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(12, 5, 12, 6),
            CornerRadius = new CornerRadius(14),
            Background = Palette.Theme("ControlFillColorDefaultBrush"),
            BorderBrush = Palette.Theme("ControlElevationBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { Ui.Icon(Glyphs.Code, 12), Ui.Text(L("完全开源 · MIT 许可", "Fully open source · MIT licensed"), 12, FontWeights.Medium) },
            },
        });
        Grid.SetRow(hero, 1);
        grid.Children.Add(hero);

        var next = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = Ui.Icon(ArrowGlyph, 18),
        };
        Ui.Label(next, L("继续", "Continue"));
        next.Click += (_, _) => Go(OnboardingStep.Provider);
        next.Loaded += (_, _) => next.Focus(FocusState.Programmatic);
        Grid.SetRow(next, 3);
        grid.Children.Add(next);
        return grid;
    }

    // MARK: Provider

    private FrameworkElement ProviderStep()
    {
        _key = _settings.ApiKey(_provider);
        var panel = StepPanel(22);
        panel.Children.Add(Header(L("连接服务商", "Connect a provider"),
            L("录音会用你自己的 API Key 发给你选的服务商转写和整理。推荐 OpenRouter：一个 Key 就能同时用于两步。",
              "Your recording goes to a provider you choose, with your own API key. OpenRouter is recommended: one key covers both transcription and clean-up.")));

        // Provider chips
        var chips = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < Onboarding.Providers.Length; i++)
        {
            var id = Onboarding.Providers[i];
            chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var selected = id == _provider;
            var chip = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8),
                Content = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        Centered(Ui.Text(id == ProviderId.SiliconFlow ? L("硅基流动", "SiliconFlow") : id.DisplayName(), 13.5, FontWeights.SemiBold)),
                        Centered(Ui.Text(id == ProviderId.OpenRouter ? L("推荐", "Recommended") : " ", 11, foreground: selected ? null : Ui.Secondary)),
                    },
                },
            };
            if (selected) chip.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, id.DisplayName());
            chip.Click += (_, _) =>
            {
                if (_provider == id) return;
                _provider = id;
                Render(animated: false);
            };
            Grid.SetColumn(chip, i);
            chips.Children.Add(chip);
        }
        panel.Children.Add(chips);

        // Key
        var key = new PasswordBox { PlaceholderText = _provider.KeyPlaceholder(), Password = _key };
        key.PasswordChanged += (_, _) =>
        {
            _key = key.Password;
            if (_continue != null) _continue.IsEnabled = _key.Trim().Length > 0;
        };
        key.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) SaveKey();
        };
        key.Loaded += (_, _) => key.Focus(FocusState.Programmatic);
        var paste = new Button { Content = L("粘贴", "Paste") };
        paste.Click += async (_, _) =>
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                key.Password = (await content.GetTextAsync()).Trim();
            }
        };
        var keyRow = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(14),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        keyRow.Children.Add(key);
        Grid.SetColumn(paste, 1);
        keyRow.Children.Add(paste);
        var keyCard = new StackPanel { Children = { keyRow } };
        if (_provider.KeysUrl() is { } keysUrl)
        {
            var link = new HyperlinkButton
            {
                Padding = new Thickness(0),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        Ui.Text(L($"去 {_provider.DisplayName()} 创建一个", $"Create one at {_provider.DisplayName()}"), 12.5),
                        Ui.Icon(Glyphs.OpenInNew, 11),
                    },
                },
            };
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(keysUrl) { UseShellExecute = true });
            var hint = Ui.Text(L("还没有 Key？", "No key yet?"), 12.5, foreground: Ui.Secondary);
            hint.VerticalAlignment = VerticalAlignment.Center;
            keyCard.Children.Add(new CardDivider { Inset = 0 });
            keyCard.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Padding = new Thickness(14, 10, 14, 10),
                Children = { hint, link },
            });
        }
        panel.Children.Add(Card.Make(keyCard));

        // Models
        var (stt, chat) = Onboarding.Models(_provider);
        var models = new StackPanel { Spacing = 8 };
        models.Children.Add(Card.Make(new StackPanel
        {
            Children =
            {
                ModelRow(Glyphs.Speech, Tint.Blue, L("语音转文字", "Speech-to-text"), stt),
                new CardDivider(),
                ModelRow(Glyphs.Font, Tint.Purple, L("文字整理", "Clean-up"), chat),
            },
        }));
        var note = Ui.Text(L("之后可以随时在「设置 → 模型」里更换。", "You can change them any time in Settings → Models."), 11.5, foreground: Ui.Secondary, wrap: true);
        note.Margin = new Thickness(4, 0, 4, 0);
        models.Children.Add(note);
        panel.Children.Add(models);
        return panel;
    }

    private static CardRow ModelRow(string glyph, Tint tint, string title, string model)
    {
        var text = Ui.Text(model, 12, foreground: Ui.Secondary);
        text.FontFamily = new FontFamily("Cascadia Mono,Consolas");
        text.IsTextSelectionEnabled = true;
        return new CardRow { Glyph = glyph, Tint = tint, Title = title, Trailing = text };
    }

    private void SaveKey()
    {
        var key = _key.Trim();
        if (key.Length == 0) return;
        Onboarding.Apply(_provider, key, _settings);
        _controller.RefreshModelInfo();
        Go(OnboardingStep.TryIt);
    }

    // MARK: Try it

    private FrameworkElement TryStep()
    {
        var hotkey = _settings.Hotkey;
        var panel = StepPanel(18);
        panel.Children.Add(Header(L("试一试", "Try it out"),
            L($"点一下下面的框，按住 {hotkey.DisplayName} 说一句话，松开后文字就会出现在这里。",
              $"Click in the box below, hold {hotkey.DisplayName} and say something. Let go, and your words appear here.")));
        panel.Children.Add(new KeyCaps { Caps = hotkey.KeyCaps, Large = true, HorizontalAlignment = HorizontalAlignment.Center });

        if (!_micGranted)
        {
            var grant = new Button { Content = L("授权…", "Grant…") };
            grant.Click += (_, _) => Permissions.OpenMicrophoneSettings();
            panel.Children.Add(Card.Make(new CardRow
            {
                Glyph = Glyphs.Microphone, Tint = Tint.Red, Title = L("麦克风", "Microphone"),
                Subtitle = L("用来录下你的语音", "To record your voice"), Trailing = grant,
            }));
        }
        else if (!_settings.IsConfigured(_settings.SttProvider))
        {
            var add = new Button { Content = L("填写 API Key", "Add a key") };
            add.Click += (_, _) => Go(OnboardingStep.Provider);
            panel.Children.Add(new Banner(Glyphs.Key, Tint.Orange, L("还没有填写 API Key，听写暂时用不了。", "There's no API key yet, so dictation can't work."), add));
        }

        _tryBox = new TextBox
        {
            Text = _tryText,
            PlaceholderText = L("你说的话会出现在这里…", "Your words will appear here…"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Height = 104,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_tryBox, ScrollBarVisibility.Auto);
        _tryBox.TextChanged += (_, _) =>
        {
            _tryText = _tryBox.Text;
            UpdateTryStatus();
        };
        var box = _tryBox;
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
        panel.Children.Add(box);

        _tryStatus = new TextBlock { FontSize = 12, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 18 };
        panel.Children.Add(_tryStatus);
        UpdateTryStatus();
        return panel;
    }

    private void OnSessionChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_step != OnboardingStep.TryIt) return;
        CheckLatest();
        UpdateTryStatus();
    });

    /// <summary>The paste normally lands in the box; if it didn't, the dictation is shown from History instead.</summary>
    private void CheckLatest()
    {
        if (_history.Records.FirstOrDefault() is not { } latest || latest.Date < _tryStarted) return;
        switch (latest.Status)
        {
            case DictationStatus.Done or DictationStatus.PolishFailed:
                _tryError = null;
                if (string.IsNullOrWhiteSpace(_tryText) && _tryBox != null) _tryBox.Text = latest.FinalText;
                break;
            case DictationStatus.Failed:
                _tryError = latest.Error;
                break;
        }
    }

    private void UpdateTryStatus()
    {
        if (_tryStatus == null) return;
        (string Text, Brush? Brush) status = _controller.State switch
        {
            SessionController.SessionState.Recording => (L("正在听…", "Listening…"), Palette.Brush(Tint.Red)),
            SessionController.SessionState.Processing => (L("正在整理…", "Writing…"), Ui.Secondary),
            _ when _tryError != null => (_tryError, Palette.Brush(Tint.Red)),
            _ when !string.IsNullOrWhiteSpace(_tryText) => (L("成功了！在任何能打字的地方都可以这样用。", "It works! Use it anywhere you can type."), Palette.Brush(Tint.Green)),
            _ => ("", null),
        };
        _tryStatus.Text = status.Text;
        if (status.Brush != null) _tryStatus.Foreground = status.Brush;
        ToolTipService.SetToolTip(_tryStatus, string.IsNullOrEmpty(status.Text) ? null : status.Text);
    }

    // MARK: Done

    private FrameworkElement DoneStep()
    {
        var panel = StepPanel(22);
        var check = new Border
        {
            Width = 84,
            Height = 84,
            CornerRadius = new CornerRadius(42),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 34, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(204, 40, 180, 80), Offset = 0 },
                    new GradientStop { Color = Palette.Of(Tint.Green), Offset = 1 },
                },
            },
            Child = Ui.Icon(Glyphs.CheckMark, 34, new SolidColorBrush(Colors.White)),
        };
        panel.Children.Add(check);
        var header = Header(L("一切就绪", "You're all set"),
            L($"OpenTypeless 在系统托盘里待命。在任何应用里按住 {_settings.Hotkey.DisplayName} 就能听写。",
              $"OpenTypeless waits in the system tray. Hold {_settings.Hotkey.DisplayName} in any app to dictate."));
        header.Margin = new Thickness(0);
        panel.Children.Add(header);
        panel.Children.Add(Card.Make(new StackPanel
        {
            Children =
            {
                new CardRow
                {
                    Glyph = Glyphs.Microphone, Tint = Tint.Blue, Title = L("按住说话", "Hold to talk"),
                    Subtitle = L("按住快捷键说话，松开后自动整理并插入到光标处", "Hold the shortcut while you speak; release to clean up and insert"),
                },
                new CardDivider(),
                new CardRow
                {
                    Glyph = Glyphs.TouchPointer, Tint = Tint.Purple, Title = L("轻点一下：免手持", "Tap once: hands-free"),
                    Subtitle = L("适合长段口述，说完再按一次结束", "For long dictation — press again when done"),
                },
                new CardDivider(),
                new CardRow
                {
                    Glyph = Glyphs.Cancel, Tint = Tint.Gray, Title = L("Esc 取消", "Esc to cancel"),
                    Subtitle = L("录音或处理过程中随时取消", "Cancel any time while recording or processing"),
                },
            },
        }));
        return panel;
    }

    private int Scaled(double dips) => (int)Math.Round(dips * Win32.GetDpiForWindow(Hwnd) / 96.0);

    private void Center()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }
}
