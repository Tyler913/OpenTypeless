using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

public sealed class GeneralPage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly ContentControl _micControl = new();
    private readonly ToggleSwitch _launchToggle = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly CardSection _startup;
    private readonly DispatcherQueueTimer _timer;
    private string? _launchError;
    private bool _updating;

    public GeneralPage() : base(SettingsPage.General)
    {
        // Permissions
        var permissions = new CardSection { Title = L("权限", "Permissions") };
        permissions.Body.Add(new CardRow
        {
            Glyph = Glyphs.Microphone, Tint = Tint.Red, Title = L("麦克风", "Microphone"),
            Subtitle = L("用来录下你的语音", "To record your voice"),
            Trailing = _micControl,
        });
        permissions.Body.Add(new CardDivider());
        permissions.Body.Add(new CardRow
        {
            Glyph = Glyphs.EaseOfAccess, Tint = Tint.Blue, Title = L("键盘与粘贴", "Keyboard & paste"),
            Subtitle = L("用来监听快捷键，并把文字粘贴到光标处。Windows 不需要额外授权，但无法粘贴到以管理员身份运行的程序（此时会复制到剪贴板）。",
                         "To listen for the shortcut and paste text at the cursor. Windows needs no extra permission, but can’t paste into apps running as administrator (the text goes to the clipboard instead)."),
            Trailing = new StatusPill(L("无需授权", "Not required"), Tint.Green),
        });
        Body.Children.Add(permissions);

        // Appearance
        var appearance = new CardSection { Title = L("外观", "Appearance") };
        var language = new ComboBox { MinWidth = 150 };
        language.Items.Add(L("跟随系统", "System"));
        language.Items.Add("简体中文");
        language.Items.Add("English");
        language.SelectedIndex = _settings.AppLanguage switch { AppLanguage.Zh => 1, AppLanguage.En => 2, _ => 0 };
        language.SelectionChanged += (_, _) =>
        {
            var selected = language.SelectedIndex switch { 1 => AppLanguage.Zh, 2 => AppLanguage.En, _ => AppLanguage.System };
            // Deferred: the language switch rebuilds this page.
            DispatcherQueue.TryEnqueue(() => _settings.AppLanguage = selected);
        };
        appearance.Body.Add(new CardRow { Glyph = Glyphs.Globe, Tint = Tint.Teal, Title = L("界面语言", "Language"), Trailing = language });
        Body.Children.Add(appearance);

        // Startup
        _startup = new CardSection { Title = L("启动", "Startup") };
        _launchToggle.Toggled += (_, _) =>
        {
            if (_updating) return;
            try
            {
                LaunchAtLogin.Set(_launchToggle.IsOn);
                _launchError = null;
            }
            catch (Exception error)
            {
                _launchError = error.Message;
            }
            RefreshLaunchState();
        };
        _startup.Body.Add(new CardRow
        {
            Glyph = Glyphs.Power, Tint = Tint.Green, Title = L("登录时自动启动", "Open at login"),
            Subtitle = L("登录后在任务栏通知区域待命", "Waits in the notification area after you sign in"),
            Trailing = _launchToggle,
        });
        Body.Children.Add(_startup);

        // Dictation
        var dictation = new CardSection { Title = L("听写", "Dictation") };
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Paste, Tint = Tint.Brown, Title = L("恢复剪贴板", "Restore clipboard"),
            Subtitle = L("插入文字后，把剪贴板恢复成原来的内容", "Put your previous clipboard back after inserting"),
            Trailing = Toggle(_settings.RestoreClipboard, on => _settings.RestoreClipboard = on),
        });
        dictation.Body.Add(new CardDivider());
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Volume, Tint = Tint.Pink, Title = L("提示音", "Sounds"),
            Subtitle = L("开始和结束录音时播放轻提示音", "Soft chime when recording starts and stops"),
            Trailing = Toggle(_settings.PlaySounds, on => _settings.PlaySounds = on),
        });
        dictation.Body.Add(new CardDivider());
        var minutes = new NumberBox
        {
            Minimum = 1, Maximum = 60, SmallChange = 1, LargeChange = 5,
            Value = _settings.MaxRecordingMinutes,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 120,
        };
        minutes.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) _settings.MaxRecordingMinutes = (int)Math.Round(e.NewValue);
            else minutes.Value = _settings.MaxRecordingMinutes;
        };
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Stopwatch, Tint = Tint.Orange, Title = L("单次最长录音", "Maximum recording"),
            Subtitle = L("到时间会自动结束并处理", "Stops and processes automatically at the limit"),
            Trailing = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { minutes, new TextBlock { Text = L("分钟", "min"), VerticalAlignment = VerticalAlignment.Center } },
            },
        });
        Body.Children.Add(dictation);

        RefreshPermissions();
        RefreshLaunchState();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.5);
        _timer.Tick += (_, _) =>
        {
            RefreshPermissions();
            RefreshLaunchState();
        };
        _timer.Start();
    }

    protected override void OnClosed() => _timer.Stop();

    private static ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => set(toggle.IsOn);
        return toggle;
    }

    private bool? _micShown;

    private void RefreshPermissions()
    {
        var granted = Permissions.MicrophoneGranted;
        if (_micShown == granted) return;
        _micShown = granted;
        if (granted)
        {
            _micControl.Content = new StatusPill(L("已授权", "Granted"), Tint.Green);
        }
        else
        {
            var button = new Button { Content = L("授权…", "Grant…") };
            button.Click += (_, _) => Permissions.OpenMicrophoneSettings();
            _micControl.Content = button;
        }
    }

    private void RefreshLaunchState()
    {
        _updating = true;
        _launchToggle.IsOn = LaunchAtLogin.IsEnabled;
        _updating = false;
        if (_launchError != null)
        {
            _startup.Footer = L("无法修改：", "Couldn't change it: ") + _launchError;
        }
        else if (LaunchAtLogin.NeedsApproval)
        {
            _startup.Footer = L("需要在「任务管理器 → 启动应用」中启用 OpenTypeless。",
                                "Enable OpenTypeless in Task Manager → Startup apps.");
        }
        else
        {
            _startup.Footer = null;
        }
    }
}
