using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenTypeless.Audio;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

public sealed class GeneralPage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly ContentControl _micControl = new();
    private readonly ToggleSwitch _launchToggle = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly CardSection _preferences;
    private readonly List<UIElement> _showHomeRow = [];
    private readonly CardSection _updates = new() { Title = L("更新", "Updates") };
    private readonly Updater _updater = Updater.Shared;
    private readonly DispatcherQueueTimer _timer;
    private string? _launchError;
    private bool _updating;
    private readonly MicrophoneTester _tester = new();
    private readonly CardSection _microphone = new() { Title = L("麦克风", "Microphone") };
    private readonly ComboBox _micPicker = new() { MinWidth = 220, MaxWidth = 260 };
    private readonly LevelMeter _meter = new() { Width = 130 };
    private readonly Button _testButton = new();
    private readonly CardRow _testRow;
    private List<MicrophoneInfo> _microphones = [];
    private MicrophoneInfo? _defaultMicrophone;
    private string _pickerKey = "";
    private bool _fillingPicker;
    private bool _microphonesKnown;
    private bool _readingMicrophones;
    private bool _closed;

    public GeneralPage() : base(SettingsPage.General)
    {
        // Permissions
        var permissions = new CardSection { Title = L("权限", "Permissions") };
        permissions.Body.Add(new CardRow
        {
            Glyph = Glyphs.Microphone, Title = L("麦克风", "Microphone"), Help = L("用来录下你的语音", "To record your voice"),
            Trailing = _micControl,
        });
        permissions.Body.Add(new CardDivider());
        permissions.Body.Add(new CardRow
        {
            Glyph = Glyphs.Keyboard, Title = L("键盘与粘贴", "Keyboard & paste"),
            Help = L("用来监听快捷键，并把文字粘贴到光标处。Windows 不需要额外授权，但无法粘贴到以管理员身份运行的程序（此时会复制到剪贴板）。",
                     "To listen for the shortcut and paste text at the cursor. Windows needs no extra permission, but can’t paste into apps running as administrator (the text goes to the clipboard instead)."),
        });
        Body.Children.Add(permissions);

        // Dictation
        var dictation = new CardSection { Title = L("听写", "Dictation") };
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Paste, Title = L("恢复剪贴板", "Restore clipboard"),
            Help = L("插入文字后，把剪贴板恢复成原来的内容", "Put your previous clipboard back after inserting"),
            Trailing = Toggle(_settings.RestoreClipboard, on => _settings.RestoreClipboard = on),
        });
        dictation.Body.Add(new CardDivider());
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.TextStyle, Title = L("贴合前后文", "Fit to surrounding text"),
            Subtitle = L("按光标前后的文字调整空格和大小写", "Adjusts spacing and capitals at the cursor"),
            Help = L("插入到已有文字中间时，按光标前后的文字补上缺的空格、把句中的首字母改成小写、去掉会提前结束句子的句号。读不到光标附近的文字时（比如密码框），按原样插入。",
                     "When you dictate into existing text, adds a missing space, lowercases a capital in mid-sentence and drops a full stop that would end the sentence early, going by the text around the cursor. Where that can't be read (a password field, some apps), the text goes in as it is."),
            Trailing = Toggle(_settings.FitToSurroundingText, on => _settings.FitToSurroundingText = on),
        });
        dictation.Body.Add(new CardDivider());
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Enter, Title = L("插入后自动回车", "Press Enter after inserting"),
            Help = L("文字粘贴进去后自动按一次回车，在聊天框或给 AI 写提示词时直接发送。文字只复制到剪贴板时不会按。",
                     "Presses Enter once the text is pasted, so a chat message or AI prompt is sent right away. Not pressed when the text only goes to the clipboard."),
            Trailing = Toggle(_settings.PressEnterAfterInsert, on => _settings.PressEnterAfterInsert = on),
        });
        dictation.Body.Add(new CardDivider());
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Paragraph, Title = L("说“换行”另起一行", "Say “new line” for a line break"),
            Help = L("单独说“换行”或“新段落”（前后稍作停顿），会换成换行或空一行；其他语言的说法也可以，比如 new line、new paragraph。在一句话中间说到这些词时照原样保留；但在列举里单独说出（“空格，换行，制表符”）时也会变成换行。",
                     "Say “new line” or “new paragraph” on its own, with a short pause before and after, for a line break or a blank line; 换行 / 新段落 and the other languages’ words work too. Said within a sentence, the words stay as they are; said on their own in a list (“spaces, new line, tabs”), they become a break too."),
            Trailing = Toggle(_settings.SpokenLayoutCommands, on => _settings.SpokenLayoutCommands = on),
        });
        dictation.Body.Add(new CardDivider());
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Volume, Title = L("提示音", "Sounds"),
            Subtitle = L("开始和结束录音时播放轻提示音", "Soft chime when recording starts and stops"),
            Trailing = Toggle(_settings.PlaySounds, on => _settings.PlaySounds = on),
        });
        dictation.Body.Add(new CardDivider());
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Mute, Title = L("录音时静音", "Mute audio while recording"),
            Help = L("录音期间把扬声器静音，音乐和视频不会打扰你说话，结束后自动恢复。",
                     "Mutes your speakers while you're recording, so music or a video doesn't get in the way, and turns them back on when you stop."),
            Trailing = Toggle(_settings.MuteWhileRecording, on => _settings.MuteWhileRecording = on),
        });
        dictation.Body.Add(new CardDivider());
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Eye, Title = L("实时预览（测试版）", "Live preview (beta)"),
            Subtitle = L("说话时在录音条上方显示文字", "Shows your words above the capsule as you talk"),
            Help = L("说话时在录音条上方显示识别到的文字，由 Windows 语音识别提供：需要安装对应语言的语音包，并在「隐私和安全性 → 语音」里打开在线语音识别（语音会发送给微软识别），它使用默认麦克风。只是预览：插入的文字仍然来自语音转文字服务商。",
                     "Shows what you're saying above the capsule as you talk, recognised by Windows speech recognition. It needs the language's speech pack and online speech recognition on under Privacy & security → Speech (your voice is then sent to Microsoft to recognise), and listens to the default microphone. Only a preview: the inserted text still comes from your speech-to-text provider."),
            Trailing = Toggle(_settings.LivePreview, on => _settings.LivePreview = on),
        });
        dictation.Body.Add(new CardDivider());
        var positions = HudPositions.All;
        var positionPicker = new ComboBox { MinWidth = 150 };
        foreach (var position in positions) positionPicker.Items.Add(position.Title());
        positionPicker.SelectedIndex = Math.Max(0, Array.IndexOf(positions, _settings.HudPosition));
        positionPicker.SelectionChanged += (_, _) =>
        {
            if (positionPicker.SelectedIndex >= 0) _settings.HudPosition = positions[positionPicker.SelectedIndex];
        };
        dictation.Body.Add(new CardRow
        {
            Glyph = Glyphs.Placement, Title = L("录音条位置", "Capsule position"),
            Help = L("录音条显示在你正在用的窗口所在的屏幕上。选「光标附近」时，它出现在正在输入的那一行下方（下方放不下就在上方），不会挡住这一行；读不到光标位置的应用里显示在屏幕底部。",
                     "The capsule shows on the screen of the window you're working in. Near the text cursor puts it just below the line you're typing in (above it when there's no room below), never over it; in apps that don't say where the cursor is, it shows at the bottom of the screen."),
            Trailing = positionPicker,
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
            Glyph = Glyphs.Timer, Title = L("单次最长录音", "Maximum recording"),
            Help = L("到时间会自动结束并处理", "Stops and processes automatically at the limit"),
            Trailing = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { minutes, new TextBlock { Text = L("分钟", "min"), VerticalAlignment = VerticalAlignment.Center } },
            },
        });
        Body.Children.Add(dictation);

        // Microphone: which input to record from, and a live level meter to check it hears you.
        _micPicker.SelectionChanged += (_, _) =>
        {
            if (_fillingPicker || _micPicker.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            _settings.MicrophoneId = id;
            if (_tester.IsRunning) _tester.Start(SelectedMicrophoneId);
            RefreshMicrophoneFooter();
        };
        _microphone.Body.Add(new CardRow
        {
            Glyph = Glyphs.Microphone, Title = L("输入设备", "Input device"),
            Help = L("有的电脑默认用的是虚拟麦克风，可以在这里选真正的麦克风", "Some PCs default to a virtual device; pick your real microphone here"),
            Trailing = _micPicker,
        });
        _microphone.Body.Add(new CardDivider());
        _testButton.Click += (_, _) =>
        {
            if (_tester.IsRunning) _tester.Stop(); else _tester.Start(SelectedMicrophoneId);
        };
        _testRow = new CardRow
        {
            Glyph = Glyphs.MicPulse, Title = L("测试麦克风", "Test microphone"),
            Trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _meter, _testButton } },
        };
        _microphone.Body.Add(_testRow);
        _microphone.Body.Add(new CardDivider());
        _microphone.Body.Add(new CardRow
        {
            Glyph = Glyphs.Flash, Title = L("预热麦克风", "Keep microphone ready"),
            Subtitle = L("按下快捷键立刻开始录音", "Recording starts the instant you press the key"),
            Help = L("按下快捷键立刻开始录音，并带上按键前的一小段，第一个字不会被吞掉（蓝牙耳机尤其明显）。麦克风会一直开着，蓝牙耳机会切到通话模式，音乐音质会下降。",
                     "Recording starts the instant you press the key and includes the moment before it, so the first word isn't clipped (noticeable with Bluetooth headsets). The microphone stays on, and Bluetooth headphones switch to call mode, which lowers music quality."),
            Trailing = Toggle(_settings.KeepMicrophoneWarm, on => _settings.KeepMicrophoneWarm = on),
        });
        _tester.Changed += RefreshTester;
        Body.Children.Add(_microphone);
        RefreshMicrophones();
        RefreshTester();

        // Preferences
        _preferences = new CardSection { Title = L("偏好", "Preferences") };
        var language = new ComboBox { MinWidth = 150 };
        AppLanguage[] languages = [AppLanguage.System, .. Localization.Supported];
        foreach (var option in languages) language.Items.Add(option == AppLanguage.System ? L("跟随系统", "System") : option.NativeName());
        language.SelectedIndex = Math.Max(0, Array.IndexOf(languages, _settings.AppLanguage));
        language.SelectionChanged += (_, _) =>
        {
            var selected = languages[Math.Max(0, language.SelectedIndex)];
            // Deferred: the language switch rebuilds this page.
            DispatcherQueue.TryEnqueue(() => _settings.AppLanguage = selected);
        };
        _preferences.Body.Add(new CardRow { Glyph = Glyphs.Language, Title = L("界面语言", "Language"), Trailing = language });
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
        _preferences.Body.Add(new CardDivider());
        _preferences.Body.Add(new CardRow
        {
            Glyph = Glyphs.Power, Title = L("登录时自动启动", "Open at login"),
            Help = L("登录后在任务栏通知区域待命", "Waits in the notification area after you sign in"),
            Trailing = _launchToggle,
        });
        _showHomeRow.Add(new CardDivider());
        _showHomeRow.Add(new CardRow
        {
            Glyph = Glyphs.Home, Title = L("自动启动时打开主页", "Show Home when opened at login"),
            Help = L("关闭时开机后只在后台待命；手动打开应用总会显示主页", "Off: it waits quietly after you sign in. Opening the app yourself always shows Home"),
            Trailing = Toggle(_settings.ShowHomeAtLogin, on => _settings.ShowHomeAtLogin = on),
        });
        foreach (var element in _showHomeRow) _preferences.Body.Add(element);
        _preferences.Body.Add(new CardDivider());
        var typing = new NumberBox
        {
            Minimum = 10, Maximum = 300, SmallChange = 5, LargeChange = 20,
            Value = _settings.TypingWordsPerMinute,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 120,
        };
        typing.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) _settings.TypingWordsPerMinute = (int)Math.Round(e.NewValue);
            else typing.Value = _settings.TypingWordsPerMinute;
        };
        _preferences.Body.Add(new CardRow
        {
            Glyph = Glyphs.Speed, Title = L("你的打字速度", "Your typing speed"),
            Help = L("用来计算节省的时间（默认每分钟 100 字）", "Used to work out the time saved (100 wpm by default)") + "\n\n"
                + L("「节省的时间」= 按这个速度打出同样的字所需的时间 − 实际说话的时间。",
                    "“Time saved” is how long typing the same words at this speed would take, minus the time you spent talking."),
            Trailing = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { typing, new TextBlock { Text = L("字/分钟", "wpm"), VerticalAlignment = VerticalAlignment.Center } },
            },
        });
        Body.Children.Add(_preferences);

        // Updates
        Body.Children.Add(_updates);
        _updater.Changed += RefreshUpdates;
        RefreshUpdates();

        RefreshPermissions();
        RefreshLaunchState();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.5);
        _timer.Tick += (_, _) =>
        {
            RefreshPermissions();
            RefreshLaunchState();
            _ = ReadMicrophones();
        };
        _timer.Start();
    }

    protected override void OnClosed()
    {
        _closed = true;
        _tester.Changed -= RefreshTester;
        _tester.Stop();
        _timer.Stop();
        _updater.Changed -= RefreshUpdates;
    }

    /// <summary>Current version, update status and the one action that fits it, plus the automatic-check switch.</summary>
    private void RefreshUpdates()
    {
        _updates.Body.Clear();
        _updates.Footer = _updater.Phase == Updater.UpdatePhase.Available ? _updater.ManualReason : null;
        _updates.Body.Add(new CardRow
        {
            Glyph = Glyphs.Rocket,
            Title = $"OpenTypeless {_updater.CurrentVersion}",
            Subtitle = UpdateStatus(), Trailing = UpdateAction(),
        });
        if (_updater.Update is { } update)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (_updater.CanSkip)
            {
                var skip = new Button { Content = L("跳过此版本", "Skip this version") };
                skip.Click += (_, _) => _updater.SkipUpdate();
                buttons.Children.Add(skip);
            }
            var notes = new Button { Content = L("发布说明", "Release notes") };
            notes.Click += (_, _) => Updater.OpenInBrowser(update.PageUrl);
            buttons.Children.Add(notes);
            _updates.Body.Add(new CardDivider());
            _updates.Body.Add(new CardRow
            {
                Glyph = Glyphs.News,
                Title = L($"{update.Version} 更新内容", $"What’s new in {update.Version}"),
                Subtitle = update.Notes.Length > 280 ? update.Notes[..280] + "…" : update.Notes, Trailing = buttons,
            });
        }
        _updates.Body.Add(new CardDivider());
        _updates.Body.Add(new CardRow
        {
            Glyph = Glyphs.Sync, Title = L("自动检查更新", "Check automatically"),
            Help = L("每天一次，在后台下载，由你决定何时安装", "Once a day. Downloads in the background; installs when you choose"),
            Trailing = Toggle(_settings.AutoCheckUpdates, on => _settings.AutoCheckUpdates = on),
        });
    }

    private string UpdateStatus()
    {
        var version = _updater.Update?.Version.ToString() ?? "";
        return _updater.Phase switch
        {
            Updater.UpdatePhase.Idle when _updater.IsDevelopmentBuild => L("开发版本不会检查更新", "Development builds don’t check for updates"),
            Updater.UpdatePhase.Idle => _updater.LastChecked is { } date
                ? L("上次检查：", "Last checked ") + Formatting.ShortStamp(date)
                : L("还没有检查过", "Not checked yet"),
            Updater.UpdatePhase.Checking => L("正在检查…", "Checking…"),
            Updater.UpdatePhase.UpToDate => L("已是最新版本", "You’re up to date"),
            Updater.UpdatePhase.Available => L($"有新版本 {version}", $"Version {version} is available"),
            Updater.UpdatePhase.Downloading => L($"正在下载 {version}… {(int)(_updater.Progress * 100)}%",
                                                 $"Downloading {version}… {(int)(_updater.Progress * 100)}%"),
            Updater.UpdatePhase.Ready => _updater.InstallPending
                ? L("这次听写结束后自动重启并更新", "Restarts to update when this dictation finishes")
                : L($"{version} 已下载，重启即可完成更新", $"{version} is downloaded. Restart to finish updating"),
            Updater.UpdatePhase.Installing => L("正在重启…", "Restarting…"),
            _ => _updater.Error ?? "",
        };
    }

    private UIElement? UpdateAction()
    {
        switch (_updater.Phase)
        {
            case Updater.UpdatePhase.Checking or Updater.UpdatePhase.Installing:
                return new ProgressRing { IsActive = true, Width = 20, Height = 20 };
            case Updater.UpdatePhase.Downloading:
                return new ProgressBar { Value = _updater.Progress * 100, Maximum = 100, Width = 100 };
            case Updater.UpdatePhase.Ready:
                var restart = new Button
                {
                    Content = L("重启并更新", "Restart to update"),
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                    IsEnabled = !_updater.InstallPending,
                };
                restart.Click += (_, _) => _updater.InstallAndRestart();
                return restart;
            case Updater.UpdatePhase.Available when _updater.Update is { } update:
                var download = new Button { Content = L("前往下载…", "Download…") };
                download.Click += (_, _) => Updater.OpenInBrowser(update.PageUrl);
                return download;
            case Updater.UpdatePhase.Idle or Updater.UpdatePhase.UpToDate or Updater.UpdatePhase.Failed when !_updater.IsDevelopmentBuild:
                var check = new Button { Content = L("检查更新", "Check now") };
                check.Click += (_, _) => _updater.CheckNow();
                return check;
            default:
                return null;
        }
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => set(toggle.IsOn);
        return toggle;
    }

    private string? SelectedMicrophoneId => _settings.MicrophoneId.Length == 0 ? null : _settings.MicrophoneId;

    /// <summary>Shows the devices last read, and reads them again (see <see cref="MicrophoneList"/>).</summary>
    private void RefreshMicrophones()
    {
        if (MicrophoneList.Last is { } known) ShowMicrophones(known);
        _ = ReadMicrophones();
    }

    /// <summary>Reads the devices off the UI thread, then shows them; a tick while a read is still going adds none.</summary>
    private async Task ReadMicrophones()
    {
        if (_readingMicrophones) return;
        _readingMicrophones = true;
        try
        {
            var snapshot = await MicrophoneList.ReadAsync();
            if (!_closed) ShowMicrophones(snapshot);
        }
        catch (Exception e)
        {
            AppLog.Debug("microphones", $"couldn't list the microphones: {e.Message}");
        }
        finally
        {
            _readingMicrophones = false;
        }
    }

    /// <summary>Refills the device list when a microphone is connected, removed or becomes the default.</summary>
    private void ShowMicrophones(MicrophoneList.Snapshot snapshot)
    {
        _microphonesKnown = true;
        _microphones = snapshot.All;
        _defaultMicrophone = snapshot.Default;
        var chosen = _settings.MicrophoneId;
        var key = string.Join("|", _microphones.Select(m => m.Id + m.Name)) + "#" + _defaultMicrophone?.Name + "#" + chosen;
        if (key == _pickerKey) return;
        _pickerKey = key;
        _fillingPicker = true;
        _micPicker.Items.Clear();
        var systemDefault = L("跟随系统", "System default") + (_defaultMicrophone is { } d ? L($"（{d.Name}）", $" ({d.Name})") : "");
        _micPicker.Items.Add(new ComboBoxItem { Content = systemDefault, Tag = "" });
        foreach (var microphone in _microphones) _micPicker.Items.Add(new ComboBoxItem { Content = microphone.Label, Tag = microphone.Id });
        if (chosen.Length > 0 && _microphones.All(m => m.Id != chosen))
        {
            _micPicker.Items.Add(new ComboBoxItem { Content = L("未连接的设备", "Disconnected device"), Tag = chosen });
        }
        _micPicker.SelectedItem = _micPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == chosen) ?? _micPicker.Items[0];
        _fillingPicker = false;
        RefreshMicrophoneFooter();
    }

    private void RefreshMicrophoneFooter()
    {
        if (_tester.Error is { } error)
        {
            _microphone.Footer = error;
            return;
        }
        // Not known to be missing before the first read is in.
        if (!_microphonesKnown)
        {
            _microphone.Footer = null;
            return;
        }
        var chosen = _settings.MicrophoneId;
        if (chosen.Length == 0)
        {
            _microphone.Footer = _defaultMicrophone?.IsVirtual == true
                ? L("系统默认的输入是虚拟设备，如果听写没有声音，请在上面选择真正的麦克风。",
                    "The default input is a virtual device. If dictations come out empty, pick your real microphone above.")
                : null;
            return;
        }
        var device = _microphones.FirstOrDefault(m => m.Id == chosen);
        _microphone.Footer = device == null
            ? L("选中的麦克风没有连接，暂时使用系统默认输入。", "The chosen microphone isn't connected, so the default input is used until it's back.")
            : device.IsVirtual
                ? L("这是虚拟设备，如果听写没有声音，请换成真正的麦克风。", "This is a virtual device. If dictations come out empty, pick a real microphone.")
                : null;
    }

    private void RefreshTester()
    {
        _meter.Set(_tester.Level, _tester.Peak);
        _meter.Visibility = _tester.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _testButton.Content = _tester.IsRunning ? L("停止", "Stop") : L("测试", "Test");
        var subtitle = _tester.IsRunning ? L("说几句话，音量条应该随你的声音起伏", "Say something: the bar should rise and fall with your voice") : "";
        if (_testRow.Subtitle != subtitle) _testRow.Subtitle = subtitle;
        RefreshMicrophoneFooter();
    }

    private bool? _micShown;

    private void RefreshPermissions()
    {
        var granted = Permissions.MicrophoneGranted;
        if (_micShown == granted) return;
        _micShown = granted;
        // Nothing once granted: only a missing permission needs a control.
        if (granted)
        {
            _micControl.Content = null;
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
        foreach (var element in _showHomeRow) element.Visibility = _launchToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (_launchError != null)
        {
            _preferences.Footer = L("无法修改：", "Couldn't change it: ") + _launchError;
        }
        else if (LaunchAtLogin.NeedsApproval)
        {
            _preferences.Footer = L("需要在「任务管理器 → 启动应用」中启用 OpenTypeless。",
                                    "Enable OpenTypeless in Task Manager → Startup apps.");
        }
        else
        {
            _preferences.Footer = null;
        }
    }
}
