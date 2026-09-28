using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Input;
using OpenTypeless.Native;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;
using Windows.Graphics;

namespace OpenTypeless.UI;

/// <summary>
/// The panel shown when clicking the tray icon. It is sized to its content before it is shown and anchored to
/// the icon on whichever edge the taskbar is, and closes when it loses focus, like a macOS transient popover.
/// </summary>
public sealed class PopoverWindow : Window
{
    public const double WidthDips = 340;
    private const double MarginDips = 12;

    private readonly SessionController _controller;
    private readonly Action<SettingsPage?> _openSettings;
    private readonly Action _quit;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly Grid _root = new();
    private Win32.RECT _anchor;
    private DateTimeOffset _hiddenAt = DateTimeOffset.MinValue;
    private string? _copiedId;
    private (Updater.UpdatePhase, bool) _updateState;

    public nint Hwnd { get; }
    public bool IsOpen { get; private set; }

    public PopoverWindow(SessionController controller, Action<SettingsPage?> openSettings, Action quit)
    {
        _controller = controller;
        _openSettings = openSettings;
        _quit = quit;
        Title = "OpenTypeless";
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowHelpers.MakeFloating(this);
        SystemBackdrop = new DesktopAcrylicBackdrop();
        Content = _root;
        _root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) Dismiss();
        };

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && IsOpen) Dismiss();
        };
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Dismiss();
        };
        _controller.StateChanged += RefreshIfOpen;
        _history.Changed += RefreshIfOpen;
        // Download progress doesn't show here, so only a new phase (or a pending install) needs a rebuild.
        Updater.Shared.Changed += () =>
        {
            var state = (Updater.Shared.Phase, Updater.Shared.InstallPending);
            if (state == _updateState) return;
            _updateState = state;
            RefreshIfOpen();
        };
    }

    /// <summary>A click on the tray icon: open, or close if it's open (the click itself closes it first).</summary>
    public void Toggle(Win32.RECT anchor)
    {
        Services.AppLog.Debug("popover", $"toggle (open: {IsOpen}, anchor {anchor.Left},{anchor.Top})");
        if (IsOpen || DateTimeOffset.Now - _hiddenAt < TimeSpan.FromMilliseconds(300))
        {
            if (IsOpen) Dismiss();
            return;
        }
        Open(anchor);
    }

    public void Open(Win32.RECT anchor)
    {
        _anchor = anchor;
        _root.Children.Clear();
        _root.Children.Add(BuildContent());
        IsOpen = true;
        Place();
        AppWindow.Show();
        Activate();
        ForegroundHelper.Bring(Hwnd);
        // Measure again once the content is live in the window (fonts are only final then).
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Place);
    }

    public void Dismiss()
    {
        if (!IsOpen) return;
        Services.AppLog.Debug("popover", "dismiss");
        IsOpen = false;
        _hiddenAt = DateTimeOffset.Now;
        AppWindow.Hide();
    }

    private void RefreshIfOpen()
    {
        if (!IsOpen) return;
        _root.Children.Clear();
        _root.Children.Add(BuildContent());
        Place();
    }

    /// <summary>Pins the window to the content's height and anchors it to the tray icon.</summary>
    private void Place()
    {
        var center = new Win32.POINT { X = (_anchor.Left + _anchor.Right) / 2, Y = (_anchor.Top + _anchor.Bottom) / 2 };
        var (work, monitor, scale) = WindowHelpers.MonitorAt(center);
        _root.Measure(new Windows.Foundation.Size(WidthDips, double.PositiveInfinity));
        var heightDips = _root.DesiredSize.Height > 1 ? _root.DesiredSize.Height : 460;
        var width = (int)Math.Round(WidthDips * scale);
        // Never taller than the space above the taskbar.
        var height = Math.Min((int)Math.Round(heightDips * scale), work.Bottom - work.Top - (int)(40 * scale));
        var margin = (int)Math.Round(MarginDips * scale);

        int x, y;
        if (work.Bottom < monitor.Bottom) // taskbar at the bottom (the usual place)
        {
            x = center.X - width / 2;
            y = work.Bottom - margin - height;
        }
        else if (work.Top > monitor.Top) // top
        {
            x = center.X - width / 2;
            y = work.Top + margin;
        }
        else if (work.Left > monitor.Left) // left
        {
            x = work.Left + margin;
            y = center.Y - height / 2;
        }
        else if (work.Right < monitor.Right) // right
        {
            x = work.Right - margin - width;
            y = center.Y - height / 2;
        }
        else // auto-hidden taskbar: assume the bottom
        {
            x = center.X - width / 2;
            y = work.Bottom - margin - height;
        }
        x = Math.Clamp(x, work.Left + margin, work.Right - margin - width);
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin - height));
        WindowHelpers.PlaceClient(this, new RectInt32(x, y, width, height));
    }

    // MARK: Content

    private FrameworkElement BuildContent()
    {
        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(14), Width = WidthDips };
        stack.Children.Add(Header());
        stack.Children.Add(Hero());
        if (Warnings() is { } warnings) stack.Children.Add(warnings);
        stack.Children.Add(Recent());
        stack.Children.Add(Footer());
        // The scroll viewer takes the initial focus, so no row opens with a keyboard focus ring.
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = true };
        scroll.Loaded += (_, _) => scroll.Focus(FocusState.Programmatic);
        return scroll;
    }

    private FrameworkElement Header()
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new AppTile { TileSize = 28 });
        var name = Ui.Text("OpenTypeless", 14, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var pill = _controller.State switch
        {
            SessionController.SessionState.Recording => new StatusPill(L("录音中", "Recording"), Tint.Red),
            SessionController.SessionState.Processing => new StatusPill(L("处理中", "Processing"), Tint.Blue),
            _ => new StatusPill(L("就绪", "Ready"), Tint.Green),
        };
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);
        return grid;
    }

    private FrameworkElement Hero()
    {
        var hold = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        hold.Children.Add(Centered(Ui.Text(L("按住", "Hold"), 13.5)));
        hold.Children.Add(new KeyCaps { Caps = _settings.Hotkey.KeyCaps, VerticalAlignment = VerticalAlignment.Center });
        hold.Children.Add(Centered(Ui.Text(L("说话", "and speak"), 13.5)));
        var tip = Ui.Text(L("轻点一下进入免手持 · Esc 取消", "Tap once for hands-free · Esc to cancel"), 11.5, foreground: Ui.Secondary);
        tip.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 16, 12, 16),
            Background = Palette.Brush(Tint.Accent, 0.1),
            BorderBrush = Palette.Brush(Tint.Accent, 0.18),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 10, Children = { hold, tip } },
        };
    }

    private static TextBlock Centered(TextBlock text)
    {
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

    private FrameworkElement? Warnings()
    {
        var items = new List<(string Text, SettingsPage Page)>();
        if (!_settings.IsConfigured(_settings.SttProvider))
        {
            var name = _settings.SttProvider.DisplayName();
            items.Add((L($"还没有配置 {name} 的 API Key", $"No API key for {name}"), SettingsPage.Providers));
        }
        if (!Permissions.MicrophoneGranted) items.Add((L("需要麦克风权限", "Microphone permission needed"), SettingsPage.General));
        var failed = FailedRecord();
        var update = UpdateBanner();
        if (items.Count == 0 && failed == null && update == null) return null;

        var stack = new StackPanel { Spacing = 8 };
        foreach (var (text, page) in items)
        {
            var fix = new Button { Content = L("设置", "Fix") };
            fix.Click += (_, _) =>
            {
                Dismiss();
                _openSettings(page);
            };
            stack.Children.Add(new Banner(Glyphs.Warning, Tint.Orange, text, fix));
        }
        if (failed != null)
        {
            var duration = Formatting.DurationLabel(failed.Duration);
            var retry = new Button { Content = L("重试", "Retry") };
            retry.Click += (_, _) =>
            {
                Dismiss();
                _controller.Retry(failed, paste: true);
            };
            stack.Children.Add(new Banner(Glyphs.Refresh, Tint.Red,
                L($"上一次转写失败（{duration} 录音已保存）", $"Last dictation failed ({duration} recording saved)"), retry));
        }
        if (update != null) stack.Children.Add(update);
        return stack;
    }

    /// <summary>A downloaded update, or one that has to be downloaded by hand.</summary>
    private Banner? UpdateBanner()
    {
        var updater = Updater.Shared;
        if (updater.Update is not { } update) return null;
        var version = update.Version.ToString();
        if (updater.Phase == Updater.UpdatePhase.Ready)
        {
            var restart = new Button { Content = L("重启更新", "Restart to update"), IsEnabled = !updater.InstallPending };
            restart.Click += (_, _) => updater.InstallAndRestart();
            return new Banner(Glyphs.Download, Tint.Blue, updater.InstallPending
                ? L($"听写结束后自动更新到 {version}", $"Updates to {version} after this dictation")
                : L($"新版本 {version} 已就绪", $"Version {version} is ready"), restart);
        }
        if (updater.Phase == Updater.UpdatePhase.Available)
        {
            var download = new Button { Content = L("下载", "Download") };
            download.Click += (_, _) =>
            {
                Dismiss();
                Updater.OpenInBrowser(update.PageUrl);
            };
            return new Banner(Glyphs.Download, Tint.Blue, L($"有新版本 {version}", $"Version {version} is available"), download);
        }
        return null;
    }

    private DictationRecord? FailedRecord()
    {
        if (_controller.State != SessionController.SessionState.Idle) return null;
        var last = _history.Records.FirstOrDefault();
        return last is { Status: DictationStatus.Failed, HasAudio: true } ? last : null;
    }

    private FrameworkElement Recent()
    {
        var records = _history.Records.Where(r => r.FinalText.Length > 0).Take(5).ToList();
        var stack = new StackPanel { Spacing = 6 };
        var header = new Grid { Padding = new Thickness(4, 0, 4, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Text(L("最近", "Recent"), 11.5, FontWeights.SemiBold, Ui.Secondary));
        if (records.Count > 0)
        {
            var hint = Ui.Text(L("点击复制", "Click to copy"), 11, foreground: Ui.Tertiary);
            Grid.SetColumn(hint, 1);
            header.Children.Add(hint);
        }
        stack.Children.Add(header);

        if (records.Count == 0)
        {
            var empty = Ui.Text(L("还没有记录，按住快捷键试试吧", "Nothing yet — hold the shortcut and try it"), 12.5, foreground: Ui.Secondary);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 14, 0, 14);
            stack.Children.Add(empty);
            return stack;
        }
        var list = new StackPanel { Spacing = 1 };
        foreach (var record in records) list.Children.Add(RecentRow(record));
        stack.Children.Add(list);
        return stack;
    }

    private Button RecentRow(DictationRecord record)
    {
        var copied = _copiedId == record.Id;
        var text = Ui.Text(record.FinalText, 13, wrap: true);
        text.MaxLines = 2;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var meta = Ui.Text($"{Formatting.ShortStamp(record.Date)} · {Formatting.DurationLabel(record.Duration)}", 11, foreground: Ui.Tertiary);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new StackPanel { Spacing = 3, Children = { text, meta } });
        var icon = Ui.Icon(copied ? Glyphs.Completed : Glyphs.Copy, 12, copied ? Palette.Brush(Tint.Green) : Ui.Tertiary);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 7, 8, 7),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, record.FinalText);
        button.Click += (_, _) =>
        {
            TextInserter.CopyToClipboard(record.FinalText);
            _copiedId = record.Id;
            RefreshIfOpen();
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.2);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                if (_copiedId == record.Id)
                {
                    _copiedId = null;
                    RefreshIfOpen();
                }
            };
            timer.Start();
        };
        return button;
    }

    private FrameworkElement Footer()
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var settings = LabeledButton(Glyphs.Settings, L("设置", "Settings"));
        settings.Click += (_, _) =>
        {
            Dismiss();
            _openSettings(null);
        };
        grid.Children.Add(settings);

        var history = LabeledButton(Glyphs.History, L("历史", "History"));
        history.Click += (_, _) =>
        {
            Dismiss();
            _openSettings(SettingsPage.History);
        };
        Grid.SetColumn(history, 1);
        grid.Children.Add(history);

        var quit = new Button { Content = Ui.Icon(Glyphs.Power, 14) };
        Ui.Label(quit, L("退出 OpenTypeless", "Quit OpenTypeless"));
        quit.Click += (_, _) => _quit();
        Grid.SetColumn(quit, 3);
        grid.Children.Add(quit);
        return grid;
    }

    private static Button LabeledButton(string glyph, string text)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { Ui.Icon(glyph, 14), new TextBlock { Text = text } },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        return button;
    }
}
