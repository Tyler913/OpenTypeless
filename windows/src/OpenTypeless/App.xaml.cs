using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenTypeless.Audio;
using OpenTypeless.Input;
using OpenTypeless.Services;
using OpenTypeless.Session;
using OpenTypeless.UI;

namespace OpenTypeless;

/// <summary>
/// A tray app with no main window: the counterpart of the macOS AppDelegate. Owns the session controller,
/// the hotkey hook, the tray icon and popover, and the settings window.
/// </summary>
public partial class App : Application
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly HotkeyMonitor _hotkey = HotkeyMonitor.Shared;
    private SessionController _controller = null!;
    private TrayIcon _tray = null!;
    private PopoverWindow _popover = null!;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboarding;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        InitializeComponent();
        // Hidden windows are all we have most of the time; only Quit ends the app.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            AppLog.Debug("app", "unhandled: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Program.SnapshotDirectory is { } directory)
        {
            _ = UISnapshots.Run(directory);
            return;
        }

        // The speakers a crash left muted mid-recording are turned back on.
        OutputMute.RestoreLeftover();
        _controller = new SessionController();
        _tray = new TrayIcon();
        TextInserter.ClipboardOwner = _tray.Hwnd;
        _popover = new PopoverWindow(_controller, page => ShowSettings(page), Quit);
        _tray.Clicked += rect => _popover.Toggle(rect);

        _hotkey.Hotkey = _settings.Hotkey;
        _hotkey.OnPress = _controller.HotkeyPressed;
        _hotkey.OnRelease = _controller.HotkeyReleased;
        _hotkey.OnOtherKey = _controller.OtherKeyPressed;
        _hotkey.OnEscape = _controller.EscapePressed;
        _hotkey.Start();

        _settings.PropertyChanged += OnSettingsChanged;
        _controller.StateChanged += () =>
        {
            _tray.SetState(_controller.State);
            if (_controller.State == SessionController.SessionState.Idle) Updater.Shared.SessionBecameIdle();
        };
        _controller.OpenSettingsRequested += page => ShowSettings(SettingsPageInfo.Parse(page));
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        // Launching the app again (Start menu, Explorer) shows Home, like reopening it on macOS.
        Program.ActivationRequested += () => dispatcher.TryEnqueue(() =>
        {
            if (_onboarding != null) _onboarding.Show(); else ShowSettings(SettingsPage.Home);
        });

        _controller.RefreshModelInfo();
        LaunchAtLogin.RefreshPathIfRegistered();
        if (!LaunchAtLogin.IsDevelopmentBuild) InstallerRegistration.SyncVersion(Updater.Shared.CurrentVersion.ToString());
        LaunchAtLogin.EnableByDefaultOnce(_settings);
        _ = UsageStore.Shared; // counts the dictations History already has, the first time
        _controller.UpdateWarmMicrophone();
        PriceStore.Shared.RefreshIfStale();
        Updater.Shared.CanInstallNow = () => _controller.State == SessionController.SessionState.Idle;
        Updater.Shared.Quit = Quit;
        Updater.Shared.Start();

        // The very first launch shows the guide; it marks itself as seen as soon as it appears.
        if (Onboarding.ShouldShow(_settings))
        {
            _settings.DidShowOnboarding = true;
            _onboarding = new OnboardingWindow(_controller, () =>
            {
                _onboarding = null;
                ShowSetupOrHome();
            });
            _onboarding.Show();
        }
        // Otherwise ask for what we need up front, so the first dictation just works.
        else if (!Permissions.MicrophoneGranted) ShowSettings(SettingsPage.General);
        else if (!_settings.IsConfigured(_settings.SttProvider)) ShowSettings(SettingsPage.Providers);
        // A manual launch opens Home (new tray icons start hidden in the overflow area, so it also shows the app is
        // running); starting at sign-in stays in the tray unless the user asked for Home then too.
        else if (!Program.LaunchedAtLogin || _settings.ShowHomeAtLogin) ShowSettings(SettingsPage.Home);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.KeepMicrophoneWarm) or nameof(AppSettings.MicrophoneId))
        {
            _dispatcher?.TryEnqueue(_controller.UpdateWarmMicrophone);
        }
        if (e.PropertyName == nameof(AppSettings.Hotkey))
        {
            var hotkey = _settings.Hotkey;
            if (_hotkey.Hotkey != hotkey) _hotkey.Hotkey = hotkey;
        }
    }

    /// <summary>After the guide: the page for whatever is still missing, or Home.</summary>
    private void ShowSetupOrHome()
    {
        if (!Permissions.MicrophoneGranted) ShowSettings(SettingsPage.General);
        else if (!_settings.IsConfigured(_settings.SttProvider)) ShowSettings(SettingsPage.Providers);
        else ShowSettings(SettingsPage.Home);
    }

    private void ShowSettings(SettingsPage? page)
    {
        _settingsWindow ??= new SettingsWindow(_controller);
        _settingsWindow.Show(page);
    }

    private void Quit()
    {
        _popover.Dismiss();
        if (_controller.State != SessionController.SessionState.Idle) _controller.Cancel(silently: true);
        _tray.Dispose();
        _onboarding?.CloseForExit();
        _settingsWindow?.CloseForExit();
        Exit();
    }
}
