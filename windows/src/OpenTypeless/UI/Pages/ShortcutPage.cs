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

namespace OpenTypeless.UI;

public sealed class ShortcutPage : PageBase
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly HotkeyRecorder _recorder;

    public ShortcutPage(SettingsWindow window) : base(SettingsPage.Shortcut)
    {
        _recorder = new HotkeyRecorder(() => Win32.GetForegroundWindow() == window.Hwnd);
        _recorder.Changed += Render;
        _settings.PropertyChanged += OnSettingsChanged;
        Render();
    }

    protected override void OnClosed()
    {
        _recorder.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Hotkey)) DispatcherQueue.TryEnqueue(Render);
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
                _recorder.OnRecorded = recorded => _settings.Hotkey = recorded;
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
        Body.Children.Add(how);

        if (hotkey.IsModifierOnly && hotkey.KeyCode == Hotkey.VK_RMENU)
        {
            Body.Children.Add(new Banner(Glyphs.Info, Tint.Blue,
                L("在德语、法语等键盘布局里，右 Alt 是 AltGr，用来输入 @、€ 等字符。如果你要用它打字，请换一个快捷键。",
                  "On keyboard layouts such as German or French, Right Alt is AltGr and types characters like @ and €. If you use it for typing, pick another shortcut.")));
        }
        if (hotkey.ShadowsCommonShortcut)
        {
            Body.Children.Add(new Banner(Glyphs.Warning, Tint.Orange,
                L("这个组合会在所有应用里覆盖同名的快捷键。", "This combination overrides the same shortcut in every app.")));
        }
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
