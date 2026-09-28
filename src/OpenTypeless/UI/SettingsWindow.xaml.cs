using System.ComponentModel;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenTypeless.Native;
using OpenTypeless.Services;
using OpenTypeless.Session;
using TypelessCore;
using Windows.Graphics;

namespace OpenTypeless.UI;

/// <summary>The settings window: a Mica window with a sidebar of six pages, kept alive while hidden.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SessionController _controller;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly DispatcherQueueTimer _refresh;
    private SettingsPage _page = SettingsPage.General;
    private AppLanguage _language;
    private bool _allowClose;

    public nint Hwnd { get; }

    public SettingsWindow(SessionController controller)
    {
        _controller = controller;
        InitializeComponent();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Scaled(820);
            presenter.PreferredMinimumHeight = Scaled(520);
        }
        AppWindow.Resize(new SizeInt32(Scaled(880), Scaled(640)));
        Center();

        // Closing only hides: the window, its page and its state are kept, like the macOS settings window.
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "dev";

        _language = Localization.Resolved;
        _settings.PropertyChanged += OnSettingsChanged;
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(1.5);
        _refresh.Tick += (_, _) => UpdateReadyPill();
        BuildNavigation();
    }

    public bool IsVisible => AppWindow.IsVisible;

    public void Show(SettingsPage? page = null)
    {
        if (page is { } p) Navigate(p);
        else if (PageHost.Content == null) Navigate(_page);
        AppWindow.Show();
        Activate();
        ForegroundHelper.Bring(Hwnd);
        // Programmatic focus on the selected page's item: no keyboard focus rectangle on the first item.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => (Nav.SelectedItem as Control)?.Focus(FocusState.Programmatic));
        _refresh.Start();
        UpdateReadyPill();
    }

    public void Hide()
    {
        _refresh.Stop();
        AppWindow.Hide();
        PageHost.Content = null; // lets pages stop timers and recorders, like SwiftUI's onDisappear
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _settings.PropertyChanged -= OnSettingsChanged;
        Close();
    }

    public void Navigate(SettingsPage page)
    {
        _page = page;
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (SettingsPage)i.Tag == page);
        if (item != null && !ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item; // raises SelectionChanged → ShowPage
        }
        else
        {
            ShowPage(page);
        }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: SettingsPage page })
        {
            _page = page;
            ShowPage(page);
        }
    }

    private void ShowPage(SettingsPage page)
    {
        PageHost.Content = page switch
        {
            SettingsPage.General => new GeneralPage(),
            SettingsPage.Shortcut => new ShortcutPage(this),
            SettingsPage.Providers => new ProvidersPage(),
            SettingsPage.Models => new ModelsPage(this, _controller),
            SettingsPage.Style => new StylePage(),
            _ => new HistoryPage(_controller),
        };
    }

    private void BuildNavigation()
    {
        Nav.MenuItems.Clear();
        foreach (var page in SettingsPageInfo.All)
        {
            var item = new NavigationViewItem
            {
                Tag = page,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new IconBadge { Glyph = page.Glyph(), Tint = page.Tint(), BadgeSize = 22 },
                        new TextBlock { Text = page.Title(), VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, page.Title());
            Nav.MenuItems.Add(item);
        }
        UpdateReadyPill();
    }

    private void UpdateReadyPill()
    {
        var ready = _settings.IsConfigured(_settings.SttProvider) && Permissions.MicrophoneGranted;
        ReadyPill.Text = ready ? L("一切就绪", "Ready") : L("需要完成设置", "Setup needed");
        ReadyPill.Tint = ready ? Tint.Green : Tint.Orange;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Localization.Resolved != _language)
            {
                // Rebuild everything when the UI language changes so every L(…) string refreshes.
                _language = Localization.Resolved;
                var page = _page;
                BuildNavigation();
                PageHost.Content = null;
                Navigate(page);
            }
            UpdateReadyPill();
        });
    }

    private int Scaled(double dips) => (int)Math.Round(dips * Win32.GetDpiForWindow(Hwnd) / 96.0);

    private void Center()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }
}

public static class ForegroundHelper
{
    /// <summary>
    /// Brings a window to the front even when the request comes from the global hotkey rather than a click on
    /// our own UI (Windows otherwise only flashes the taskbar button).
    /// </summary>
    public static void Bring(nint hwnd)
    {
        if (Win32.GetForegroundWindow() == hwnd) return;
        if (Win32.SetForegroundWindow(hwnd)) return;
        // A synthetic Alt press lifts the foreground lock for this process. It's released only after the switch,
        // so the previous app never sees a lone Alt tap (which would open its menu bar).
        const ushort VK_MENU = 0x12;
        var size = System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>();
        Win32.SendInput(1, [Win32.Key(VK_MENU, up: false)], size);
        Win32.SetForegroundWindow(hwnd);
        Win32.SendInput(1, [Win32.Key(VK_MENU, up: true)], size);
    }
}
