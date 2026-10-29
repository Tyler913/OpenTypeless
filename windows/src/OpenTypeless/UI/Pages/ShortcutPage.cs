using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using OpenTypeless.Input;
using OpenTypeless.Native;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.UI;

public sealed class ShortcutPage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly HotkeyRecorder _recorder;
    /// <summary>Records the actions' shortcuts; <see cref="_recordingAction"/> says whose.</summary>
    private readonly HotkeyRecorder _actionRecorder;
    private ShortcutAction? _recordingAction;

    public ShortcutPage(SettingsWindow window) : base(SettingsPage.Shortcut)
    {
        _recorder = new HotkeyRecorder(() => Win32.GetForegroundWindow() == window.Hwnd);
        _recorder.Changed += Render;
        _actionRecorder = new HotkeyRecorder(() => Win32.GetForegroundWindow() == window.Hwnd) { AllowsModifierOnly = false };
        _actionRecorder.Changed += Render;
        _settings.PropertyChanged += OnSettingsChanged;
        Render();
    }

    protected override void OnClosed()
    {
        _recorder.Stop();
        _actionRecorder.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.Hotkey) or nameof(AppSettings.Shortcuts) or nameof(AppSettings.TranslationEnabled)
            or nameof(AppSettings.TranslationLanguage) or nameof(AppSettings.TranslationOtherLanguage) or nameof(AppSettings.TranslationChord)
            or nameof(AppSettings.CommandModeEnabled) or nameof(AppSettings.CommandChord))
        {
            DispatcherQueue.TryEnqueue(Render);
        }
    }

    /// <summary>
    /// Command mode: on or off, and the key that, pressed while the dictation shortcut is held, makes the recording an
    /// instruction for the text selected in the app in front (see <see cref="SelectionCommand"/>).
    /// </summary>
    private CardSection CommandModeSection()
    {
        var key = _settings.CommandChord.Label();
        var section = new CardSection
        {
            Title = L("用语音编辑选中的文字", "Edit selected text by voice"),
            Footer = L($"选中任意应用里的文字，按住听写快捷键时再按一下 {key}，说出怎么改（“正式一点”“翻译成英文”“修正语法”），松开后选中的文字就会被替换，可以用撤销恢复。问关于它的问题（“这是什么意思”），答案会显示并复制。没有选中文字时什么都不会改；密码框和终端不会被读取。",
                       $"Select text in any app, hold the dictation shortcut and press {key} too, and say how to change it (“more formal”, “translate to English”, “fix the grammar”): when you let go, the selection is replaced, and Undo brings it back. Ask about it (“what does this mean”) and the answer is shown and copied. Nothing changes when no text is selected; password fields and terminals are never read."),
        };
        var enabled = new ToggleSwitch { IsOn = _settings.CommandModeEnabled, OnContent = "", OffContent = "", MinWidth = 0 };
        enabled.Toggled += (_, _) => _settings.CommandModeEnabled = enabled.IsOn;
        section.Body.Add(new CardRow
        {
            Glyph = Glyphs.Edit, Tint = Tint.Pink, Title = L("编辑模式", "Command mode"),
            Subtitle = L($"按住听写快捷键时再按一下 {key}，这次说的话会用来修改选中的文字",
                         $"Hold the dictation shortcut and press {key} too: what you say edits the selected text"),
            Trailing = enabled,
        });
        if (!_settings.CommandModeEnabled) return section;
        var chords = TranslationChords.All;
        var chord = new ComboBox { MinWidth = 120 };
        foreach (var option in chords) chord.Items.Add(option.Label());
        chord.SelectedIndex = Math.Max(0, Array.IndexOf(chords, _settings.CommandChord));
        chord.SelectionChanged += (_, _) => _settings.CommandChord = chords[Math.Max(0, chord.SelectedIndex)];
        section.Body.Add(new CardDivider());
        section.Body.Add(new CardRow
        {
            Glyph = Glyphs.Keyboard, Tint = Tint.Blue, Title = L("切换键", "Switch key"),
            Subtitle = _settings.CommandChordClashes
                ? L("和翻译模式的切换键相同，请换一个", "Translation mode uses this key too — pick another")
                : L("按住听写快捷键时按下它（左右两边都可以）", "Pressed while you hold the dictation shortcut (either side)"),
            Trailing = chord,
        });
        return section;
    }

    /// <summary>When it goes, and a warning when the dictation hotkey takes the send key's presses (see <see cref="SendKeys.IsTakenByHotkey"/>).</summary>
    private string SendKeyFooter()
    {
        var footer = L("只在文字粘贴进录音时所在的应用后才按；文字只复制到剪贴板、按了 Esc 或锁屏时不会发送。",
                       "Only pressed once the text is pasted into the app you were dictating in: not when it only goes to the clipboard, after Esc, or once the screen locks.");
        var hotkey = _settings.Hotkey;
        if (!_settings.SendKey.IsTakenByHotkey(hotkey.KeyCode, hotkey.IsModifierOnly, SendKeyText.Codes)) return footer;
        return footer + "\n" + L("听写快捷键也用到了这个键，录音时会先被当作听写快捷键，发送键就不起作用了。请换一个发送键或快捷键。",
                                 "Your dictation shortcut uses this key too, and while recording it's read as the shortcut first, so the send key won't work. Pick another send key or shortcut.");
    }

    private void Render()
    {
        var hotkey = _settings.Hotkey;
        Body.Children.Clear();

        // Current shortcut / recorder
        var center = new StackPanel { Spacing = 16, Padding = new Thickness(20, 26, 20, 26), HorizontalAlignment = HorizontalAlignment.Stretch };
        center.Children.Add(Centered(Ui.Text(_recorder.IsRecording ? L("请按下新的快捷键…", "Press the new shortcut…") : L("当前快捷键", "Current shortcut"),
                                             12, FontWeights.Medium, Ui.Secondary)));
        center.Children.Add(_recorder.IsRecording ? RecordingPlaceholder() : new KeyCaps { Caps = hotkey.KeyCaps, Large = true, HorizontalAlignment = HorizontalAlignment.Center });
        if (_recorder.IsRecording)
        {
            var cancel = new Button { Content = L("取消", "Cancel"), HorizontalAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => _recorder.Stop();
            center.Children.Add(cancel);
        }
        else
        {
            var record = new Button
            {
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { Ui.Icon(Glyphs.Keyboard, 14), new TextBlock { Text = L("录制快捷键", "Record shortcut") } },
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(record, L("录制快捷键", "Record shortcut"));
            record.Click += (_, _) =>
            {
                _actionRecorder.Stop();
                _recorder.OnRecorded = recorded =>
                {
                    // An action's keys would start a dictation instead, and the action would never fire.
                    if (!recorded.IsModifierOnly && _settings.Shortcuts.ActionFor(recorded.Chord.KeyCode, recorded.Chord.Modifiers) is { } taken)
                    {
                        _recorder.Message = new ShortcutConflict.Action(taken).Message();
                        Render();
                    }
                    else
                    {
                        _settings.Hotkey = recorded;
                    }
                };
                _recorder.Start();
            };
            center.Children.Add(record);
        }
        center.Children.Add(Centered(_recorder.Message is { } message
            ? Ui.Text(message, 12, foreground: Palette.Brush(Tint.Orange), wrap: true)
            : Ui.Text(L("可以是单个修饰键（如 右 Ctrl、右 Alt），也可以是组合键（如 Alt + Space、F5）",
                        "A single modifier (Right Ctrl, Right Alt…) or a combination (Alt + Space, F5…)"), 12, foreground: Ui.Secondary, wrap: true)));
        Body.Children.Add(Card.Make(center));

        // Presets
        var presets = new CardSection { Title = L("常用", "Presets") };
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(12) };
        for (var i = 0; i < Hotkey.Presets.Length; i++)
        {
            var preset = Hotkey.Presets[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var selected = hotkey == preset;
            var chip = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8),
                Content = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        Centered(Ui.Text(preset.DisplayName, 13.5, FontWeights.SemiBold)),
                        Centered(Ui.Text(preset == Hotkey.Default ? L("默认", "Default") : " ", 11,
                                         foreground: selected ? null : Ui.Secondary)),
                    },
                },
            };
            if (selected) chip.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, preset.DisplayName);
            chip.Click += (_, _) =>
            {
                _recorder.Stop();
                _settings.Hotkey = preset;
            };
            Grid.SetColumn(chip, i);
            grid.Children.Add(chip);
        }
        presets.Body.Add(grid);
        Body.Children.Add(presets);

        // How it works
        var how = new CardSection { Title = L("使用方法", "How it works") };
        how.Body.Add(new CardRow
        {
            Glyph = Glyphs.Microphone, Tint = Tint.Blue, Title = L("按住说话", "Hold to talk"),
            Subtitle = L("按住快捷键说话，松开后自动整理并插入到光标处", "Hold the shortcut while you speak; release to clean up and insert"),
        });
        how.Body.Add(new CardDivider());
        how.Body.Add(new CardRow
        {
            Glyph = Glyphs.TouchPointer, Tint = Tint.Purple, Title = L("轻点一下：免手持", "Tap once: hands-free"),
            Subtitle = L("适合长段口述，说完再按一次结束", "For long dictation — press again when done"),
        });
        how.Body.Add(new CardDivider());
        how.Body.Add(new CardRow
        {
            Glyph = Glyphs.Cancel, Tint = Tint.Gray, Title = L("Esc 取消", "Esc to cancel"),
            Subtitle = L("录音或处理过程中随时取消", "Cancel any time while recording or processing"),
        });
        if (_settings.TranslationTarget is { } target)
        {
            var key = _settings.TranslationChord.KeyCap();
            how.Body.Add(new CardDivider());
            how.Body.Add(new CardRow
            {
                Glyph = Glyphs.Speech, Tint = Tint.Indigo, Title = L($"按住 + {key}：翻译", $"Hold + {key}: translate"),
                Subtitle = L($"插入时翻译成：{target.DisplayName}（在「词汇与风格」里设置）",
                             $"Inserted translated into {target.DisplayName} (set up under Vocabulary & Style)"),
            });
        }
        if (_settings.CommandModeEnabled && !_settings.CommandChordClashes)
        {
            var key = _settings.CommandChord.KeyCap();
            how.Body.Add(new CardDivider());
            how.Body.Add(new CardRow
            {
                Glyph = Glyphs.Edit, Tint = Tint.Pink, Title = L($"按住 + {key}：编辑选中的文字", $"Hold + {key}: edit the selection"),
                Subtitle = L("说出怎么改，选中的文字会被替换", "Say how to change it, and the selection is replaced"),
            });
        }
        Body.Children.Add(how);
        Body.Children.Add(CommandModeSection());

        // Finish and send
        var send = new CardSection { Title = L("说完即发送", "Finish and send"), Footer = SendKeyFooter() };
        var sendKeys = SendKeys.All;
        var sendPicker = new ComboBox { MinWidth = 140 };
        foreach (var key in sendKeys) sendPicker.Items.Add(key.Title());
        sendPicker.SelectedIndex = Math.Max(0, Array.IndexOf(sendKeys, _settings.SendKey));
        sendPicker.SelectionChanged += (_, _) =>
        {
            if (sendPicker.SelectedIndex < 0 || sendKeys[sendPicker.SelectedIndex] == _settings.SendKey) return;
            _settings.SendKey = sendKeys[sendPicker.SelectedIndex];
            send.Footer = SendKeyFooter();
        };
        send.Body.Add(new CardRow
        {
            Glyph = Glyphs.Send, Tint = Tint.Green, Title = L("发送键", "Send key"),
            Subtitle = L("录音时按下它会结束听写，文字插入后再在同一个应用里按一次这个键把它发出去。免手持时可以一步说完并发送。",
                         "Press it while recording to end the dictation; once the text is in, the same key is pressed in that app to send it. Hands-free, that ends and sends in one go."),
            Trailing = sendPicker,
        });
        Body.Children.Add(send);

        // Actions with a shortcut of their own
        var actions = new CardSection
        {
            Title = L("更多快捷键", "More shortcuts"),
            Footer = L("在两次听写之间按下时生效。可以是 Ctrl / Alt / Shift / Win 组合键（如 Alt + Shift + Z），也可以是 F1–F24。",
                       "They work between dictations. Use a combination with Ctrl / Alt / Shift / Win (Alt + Shift + Z…) or F1–F24."),
        };
        foreach (var action in ShortcutActions.All)
        {
            if (actions.Body.Count > 0) actions.Body.Add(new CardDivider());
            actions.Body.Add(new CardRow { Glyph = Glyphs.Paste, Tint = Tint.Teal, Title = action.Title(), Subtitle = action.Subtitle(), Trailing = ActionControls(action) });
        }
        Body.Children.Add(actions);
        if (_actionRecorder.Message is { } actionMessage) Body.Children.Add(new Banner(Glyphs.Warning, Tint.Orange, actionMessage));
        var actionShortcuts = ShortcutActions.All.Select(_settings.Shortcut).OfType<Hotkey>().ToList();

        if (hotkey.IsModifierOnly && hotkey.KeyCode == Hotkey.VK_RMENU)
        {
            Body.Children.Add(new Banner(Glyphs.Info, Tint.Blue,
                L("在德语、法语等键盘布局里，右 Alt 是 AltGr，用来输入 @、€ 等字符。如果你要用它打字，请换一个快捷键。",
                  "On keyboard layouts such as German or French, Right Alt is AltGr and types characters like @ and €. If you use it for typing, pick another shortcut.")));
        }
        if (actionShortcuts.Any(shortcut => shortcut.BlocksAltGr))
        {
            Body.Children.Add(new Banner(Glyphs.Info, Tint.Blue,
                L("在德语、法语等键盘布局里，Ctrl + Alt 加字母就是 AltGr，用来输入 @、€ 等字符，这个快捷键会挡住它们。如果你要用它们打字，请换一个快捷键。",
                  "On keyboard layouts such as German or French, Ctrl + Alt with a letter is AltGr, which types characters like @ and €, so this shortcut would block them. If you type those, pick another shortcut.")));
        }
        if (hotkey.ShadowsCommonShortcut || actionShortcuts.Any(shortcut => shortcut.ShadowsCommonShortcut))
        {
            Body.Children.Add(new Banner(Glyphs.Warning, Tint.Orange,
                L("这个组合会在所有应用里覆盖同名的快捷键。", "This combination overrides the same shortcut in every app.")));
        }
    }

    /// <summary>An action's shortcut, and buttons to record or clear it.</summary>
    private FrameworkElement ActionControls(ShortcutAction action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (_actionRecorder.IsRecording && _recordingAction == action)
        {
            var prompt = Ui.Text(L("请按下新的快捷键…", "Press the new shortcut…"), 12.5, foreground: Ui.Secondary);
            prompt.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(prompt);
            var cancel = new Button { Content = L("取消", "Cancel") };
            cancel.Click += (_, _) => _actionRecorder.Stop();
            row.Children.Add(cancel);
        }
        else if (_settings.Shortcut(action) is { } shortcut)
        {
            row.Children.Add(new KeyCaps { Caps = shortcut.KeyCaps, VerticalAlignment = VerticalAlignment.Center });
            var change = new Button { Content = L("更改", "Change") };
            change.Click += (_, _) => Record(action);
            row.Children.Add(change);
            var clear = new Button { Content = Ui.Icon(Glyphs.Cancel, 12) };
            Ui.Label(clear, L("清除快捷键", "Clear shortcut"));
            clear.Click += (_, _) => _settings.SetShortcut(action, null);
            row.Children.Add(clear);
        }
        else
        {
            var record = new Button { Content = L("录制快捷键", "Record shortcut") };
            record.Click += (_, _) => Record(action);
            row.Children.Add(record);
        }
        return row;
    }

    /// <summary>Records <paramref name="action"/>'s shortcut; one that clashes (see <see cref="ShortcutConflict"/>) is refused, with the reason shown.</summary>
    private void Record(ShortcutAction action)
    {
        _recorder.Stop();
        _recordingAction = action;
        _actionRecorder.OnRecorded = recorded =>
        {
            if (_settings.Shortcuts.Conflict(recorded.Chord, action, _settings.Hotkey.Chord, Hotkey.VK_ESCAPE) is { } conflict)
            {
                _actionRecorder.Message = conflict.Message();
                Render();
            }
            else
            {
                _settings.SetShortcut(action, recorded);
            }
        };
        _actionRecorder.Start();
    }

    private static FrameworkElement Centered(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        return text;
    }

    private static FrameworkElement RecordingPlaceholder()
    {
        var dot = new Ellipse { Width = 10, Height = 10, Fill = Palette.Brush(Tint.Red) };
        var pulse = new DoubleAnimation { From = 0.3, To = 1, Duration = TimeSpan.FromSeconds(0.7), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(pulse, dot);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        var storyboard = new Storyboard { Children = { pulse } };
        dot.Loaded += (_, _) => storyboard.Begin();
        dot.Unloaded += (_, _) => storyboard.Stop();
        return new Grid
        {
            Width = 180, Height = 50, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Rectangle
                {
                    RadiusX = 10, RadiusY = 10, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 },
                    Stroke = Palette.Brush(Tint.Accent),
                },
                dot,
            },
        };
    }
}
