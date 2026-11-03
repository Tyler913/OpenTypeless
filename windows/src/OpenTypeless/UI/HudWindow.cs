using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenTypeless.Native;
using OpenTypeless.Session;
using TypelessCore;
using Windows.Graphics;
using Windows.UI;

namespace OpenTypeless.UI;

/// <summary>Shows and hides the recording capsule. UI-thread only.</summary>
public sealed class HudController
{
    public HudModel Model { get; } = new();
    private HudWindow? _window;
    private DispatcherQueueTimer? _hideTimer;
    /// <summary>A notice waiting for the capsule to be free (see <see cref="ShowWhenFree"/>).</summary>
    private (HudPhase Phase, double HideAfter)? _waiting;

    public void Show(HudPhase phase, double? autoHideAfter = null)
    {
        _hideTimer?.Stop();
        // What was waiting for the capsule to be free goes up instead.
        if (phase is HudPhase.Hidden && _waiting is { } next)
        {
            _waiting = null;
            Show(next.Phase, next.HideAfter);
            return;
        }
        Model.Phase = phase;
        if (phase is HudPhase.Hidden)
        {
            _window?.HideHud();
            return;
        }
        _window ??= new HudWindow(Model);
        _window.ShowHud();

        if (autoHideAfter is { } seconds) HideAfter(Message(phase) is { } message ? DisplayTime(message, seconds) : seconds);
    }

    /// <summary>
    /// A notice that waits its turn: shown now when the capsule is empty, otherwise once what it shows has gone (the text
    /// of a dictation on its way, a notice about it, or a recording), so it never covers or cuts short any of them. A
    /// newer one takes the place of one still waiting.
    /// </summary>
    public void ShowWhenFree(HudPhase phase, double autoHideAfter)
    {
        if (Model.Phase is HudPhase.Hidden) Show(phase, autoHideAfter);
        else _waiting = (phase, autoHideAfter);
    }

    /// <summary>The text a notice shows (its length decides how long it stays up).</summary>
    internal static string? Message(HudPhase phase) => phase switch
    {
        HudPhase.Learned learned => L("已加入词汇表：", "Added to vocabulary: ") + learned.Terms,
        HudPhase.Answer answer => L("答案已复制：", "Answer copied: ") + answer.Text,
        HudPhase.Spending spending => spending.Reminder.Message,
        HudPhase.Error error => error.Message,
        _ => null,
    };

    /// <summary>
    /// How long a notice stays up: what the caller asked for, plus reading time (about 15 characters a second) for
    /// whatever goes past the first line, up to 12 s. Chinese, Japanese and Korean characters count double: they are
    /// twice as wide. The same rule as the macOS capsule.
    /// </summary>
    internal static double DisplayTime(string message, double requested)
    {
        var width = 0;
        foreach (var rune in message.EnumerateRunes()) width += IsWide(rune.Value) ? 2 : 1;
        var extra = Math.Max(0, width - 50) / 15.0;
        return Math.Min(requested + extra, Math.Max(requested, 12));
    }

    private static bool IsWide(int c) =>
        c is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6);

    /// <summary>Transcribing and cleaning up, with the ring round the ■ button starting from empty.</summary>
    public void ShowWorking(bool polishes)
    {
        Model.StartProgress(polishes);
        Show(new HudPhase.Working());
    }

    /// <summary>The text went in: the ring runs to the end, then the capsule goes.</summary>
    public void FinishWorking()
    {
        if (Model.Phase is not HudPhase.Working)
        {
            Show(new HudPhase.Hidden());
            return;
        }
        Model.Progress.Reach(new ProcessingProgress.Milestone.Delivered());
        HideAfter(0.3);
    }

    private void HideAfter(double seconds)
    {
        _hideTimer?.Stop();
        _hideTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.01, seconds));
        _hideTimer.Tick -= OnHideTimer;
        _hideTimer.Tick += OnHideTimer;
        _hideTimer.Start();
    }

    private void OnHideTimer(DispatcherQueueTimer sender, object args) => Show(new HudPhase.Hidden());

    /// <summary>Used by the UI snapshot tool.</summary>
    internal HudWindow Window => _window ??= new HudWindow(Model);
}

/// <summary>
/// Small Mica capsule near the bottom of the screen: ✕, a waveform and ↑. Never takes focus, so the
/// target text field stays focused for the paste. Clicks go through to what's below, except on the capsule while it
/// shows its ✕ and ↑ / ■ buttons.
/// </summary>
public sealed class HudWindow : Window
{
    private const double HeightDips = 44;
    /// <summary>Extra height for the live preview line above the waveform.</summary>
    private const double PreviewDips = 26;
    private const double BottomMarginDips = 28;
    /// <summary>Space above and below a notice that wraps onto more lines.</summary>
    private const double NoticePaddingDips = 11;
    /// <summary>A notice's text: up to three lines this wide, so the end of an error (often what to do) still shows.</summary>
    private const double NoticeWidthDips = 340;
    private const int NoticeLines = 3;
    /// <summary>
    /// The window is cut to a capsule at the usual height, and to a rounded rectangle with the same corners when a
    /// notice wraps or the preview shows (Windows itself would only round its corners by 8 pixels).
    /// </summary>
    private const double CornerDips = HeightDips / 2;
    /// <summary>The ✕ and ↑ / ■ circles, their inset from the ends, the room either side of the waveform, and its width.</summary>
    private const double ButtonDips = 26, ButtonInsetDips = 9, GapDips = 14, WaveDips = 64;

    private readonly HudModel _model;
    private readonly ActiveMicaBackdrop _backdrop = new();
    /// <summary>Holds <see cref="_root"/>, the capsule's content.</summary>
    private readonly Grid _frame = new();
    private readonly Grid _root = new();
    /// <summary>The glow round the capsule as a recording starts, in a window of its own behind this one.</summary>
    private readonly HudGlow _glow = new();
    /// <summary>A recording has started: the glow blooms once the capsule is placed and showing.</summary>
    private bool _bloomPending;
    private readonly Stopwatch _frameClock = new();
    private TimeSpan _lastFrame;
    private bool _animating;
    private readonly LevelBars _bars;
    /// <summary>The ring round the ■ button while working (a new one each time it's drawn).</summary>
    private ProgressArc? _ring;
    /// <summary>The phase drawn last, to see a recording start (and let the glow bloom).</summary>
    private HudPhase? _rendered;
    private readonly TextBlock _preview = new()
    {
        FontSize = 13, MaxWidth = 380, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
    };
    private readonly Win32.SUBCLASSPROC _subclass; // kept alive for the window's lifetime
    private bool _visible;
    private int _centerX = int.MinValue;

    public nint Hwnd { get; }

    public HudWindow(HudModel model)
    {
        _model = model;
        Title = "OpenTypeless HUD";
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowHelpers.MakeFloating(this);
        // No frame at all: the window region cuts the capsule out, and Windows would otherwise still draw its thin border
        // (a white ring round the capsule).
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(false, false);
        var noBorder = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint)); // Windows 11 only
        WindowHelpers.AddExtendedStyle(Hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST);
        _subclass = Subclass;
        Win32.SetWindowSubclass(Hwnd, _subclass, 1, 0);
        SystemBackdrop = _backdrop;

        _bars = new LevelBars();
        _root.VerticalAlignment = VerticalAlignment.Stretch;
        _frame.Children.Add(_root);
        Content = _frame;

        _model.PhaseChanged += Render;
        _model.LevelsChanged += UpdateLevels;
        _model.PreviewChanged += OnPreviewChanged;
        _model.BadgeChanged += Render;
        _preview.Foreground = Ui.Secondary;
        _frame.ActualThemeChanged += (_, _) => OnThemeChanged();
        Render();
    }

    /// <summary>
    /// Windows switched between light and dark while the capsule exists: the Mica follows, and the brushes picked up
    /// in code (which don't change by themselves) are fetched again by redrawing.
    /// </summary>
    private void OnThemeChanged()
    {
        _backdrop.SetTheme(_frame.ActualTheme);
        _preview.Foreground = Ui.Secondary;
        Render();
    }

    public void ShowHud()
    {
        Render();
        Resize(position: !_visible);
        // The capsule may have just moved to a monitor with a different scale.
        _bars.SetScale(WindowHelpers.Scale(Hwnd));
        if (!_visible)
        {
            _visible = true;
            Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
            Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        if (_bloomPending)
        {
            _bloomPending = false;
            _glow.Bloom(_placed, WindowHelpers.Scale(Hwnd), Hwnd);
        }
    }

    public void HideHud()
    {
        _visible = false;
        _bloomPending = false;
        _glow.Stop();
        SetAnimating(false);
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
    }

    /// <summary>Whether the capsule shows its ✕ and ↑ / ■ buttons (and so takes clicks on itself).</summary>
    private bool HasButtons => _model.Phase is HudPhase.Recording or HudPhase.Working;

    private void Render()
    {
        // The waveform and preview line are reused: release them from the previous layout before rebuilding.
        Release(_root);
        var phase = _model.Phase;
        var bloom = phase is HudPhase.Recording && _rendered is not HudPhase.Recording;
        _rendered = phase;
        var working = phase is HudPhase.Working;
        _ring = null;
        FrameworkElement row;
        if (HasButtons)
        {
            _root.Padding = new Thickness(ButtonInsetDips, 0, ButtonInsetDips, 0);
            row = Controls(working);
        }
        else
        {
            _root.Padding = new Thickness(16, 0, 16, 0);
            row = NoticeRow(phase);
        }
        if (ShowsPreview)
        {
            _preview.Text = _model.Preview;
            _root.Children.Add(new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { _preview, row } });
        }
        else
        {
            _root.Children.Add(row);
        }
        SetAnimating(working);
        if (bloom) _bloomPending = true;
        if (_visible) Resize(position: false);
    }

    private static void Release(Panel panel)
    {
        foreach (var inner in panel.Children.OfType<Panel>()) Release(inner);
        panel.Children.Clear();
    }

    /// <summary>
    /// ✕, the waveform (dots riding a wave once the key is let go) and ↑ (■ while the text is on its way).
    /// Recording and working are the same size, so the capsule doesn't move when the key is released; the
    /// buttons stay at the ends when the preview line makes the capsule wider.
    /// </summary>
    private Grid Controls(bool working)
    {
        var row = new Grid { Height = HeightDips, VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // A solid black circle (white in dark mode).
        row.Children.Add(CircleButton(L("取消", "Cancel"), Ui.Primary,
                                      Ui.Icon(Glyphs.Cancel, 10, Palette.Theme("TextOnAccentFillColorPrimaryBrush")), _model.RequestCancel));

        var wave = new Grid { Width = WaveDips, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        if (working)
        {
            wave.Children.Add(WaveDots());
        }
        else
        {
            _bars.SetColor(Ui.Primary is SolidColorBrush primary ? primary.Color : _root.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White);
            _bars.SetScale(WindowHelpers.Scale(Hwnd));
            // A new recording starts from its own levels, not from the end of the previous one.
            _bars.SetLevels(HudModel.Waveform(_model.Levels), animated: false);
            wave.Children.Add(_bars.Element);
        }
        var middle = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(GapDips, 0, GapDips, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { wave },
        };
        if (_model.Badge is { } badge) middle.Children.Add(ModeBadge(badge));
        Grid.SetColumn(middle, 1);
        row.Children.Add(middle);

        Grid send;
        if (working)
        {
            // ■, with a ring filling round it as the text comes back.
            _ring = new ProgressArc(ButtonDips, Palette.Of(Tint.Accent));
            var stop = new Grid { Width = ButtonDips, Height = ButtonDips };
            stop.Children.Add(_ring.Element);
            stop.Children.Add(new Rectangle
            {
                Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Stroke = Ui.Primary, StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            send = CircleButton(L("停止", "Stop"), Palette.Brush(Tint.Accent, 0.3), stop, _model.RequestCancel);
        }
        else
        {
            send = CircleButton(L("完成", "Done"), Palette.Brush(Tint.Accent, 0.3), Ui.Icon(Glyphs.ArrowUp, 12, Ui.Primary), _model.RequestFinish);
        }
        Grid.SetColumn(send, 2);
        row.Children.Add(send);
        return row;
    }

    /// <summary>A round button: dims while pressed, and acts on a click without activating the capsule.</summary>
    private static Grid CircleButton(string label, Brush fill, FrameworkElement icon, Action action)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = new Grid
        {
            Width = ButtonDips, Height = ButtonDips, VerticalAlignment = VerticalAlignment.Center,
            // Transparent rather than none, so the corners outside the circle take the click too.
            Background = new SolidColorBrush(Colors.Transparent),
            // A click never moves keyboard focus here (nor shows a focus rectangle).
            AllowFocusOnInteraction = false,
            Children = { new Ellipse { Width = ButtonDips, Height = ButtonDips, Fill = fill }, icon },
        };
        AutomationProperties.SetName(button, label);
        button.PointerPressed += (_, _) => button.Opacity = 0.7;
        button.PointerReleased += (_, _) => button.Opacity = 1;
        button.PointerExited += (_, _) => button.Opacity = 1;
        button.PointerCaptureLost += (_, _) => button.Opacity = 1;
        button.Tapped += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        return button;
    }

    private StackPanel NoticeRow(HudPhase phase)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        switch (phase)
        {
            case HudPhase.Copied:
                row.Children.Add(Ui.Icon(Glyphs.Paste, 14, Ui.Secondary));
                row.Children.Add(Label(L("已复制到剪贴板", "Copied to clipboard")));
                break;
            case HudPhase.Learned:
                row.Children.Add(Ui.Icon(Glyphs.Dictionary, 14, Palette.Theme("AccentTextFillColorPrimaryBrush"), filled: true));
                row.Children.Add(Notice(HudController.Message(phase) ?? ""));
                break;
            case HudPhase.Answer:
                // The start of it; the whole answer is on the clipboard.
                row.Children.Add(Ui.Icon(Glyphs.Message, 14, Palette.Theme("AccentTextFillColorPrimaryBrush"), filled: true));
                row.Children.Add(Notice(HudController.Message(phase) ?? ""));
                break;
            case HudPhase.Spending spending:
                var tint = spending.Reminder.Level == SpendLimit.Level.Reached ? Tint.Red : Tint.Orange;
                row.Children.Add(Ui.Icon(Glyphs.Money, 14, Palette.Brush(tint), filled: true));
                row.Children.Add(Notice(spending.Reminder.Message));
                break;
            case HudPhase.Error error:
                row.Children.Add(Ui.Icon(Glyphs.Warning, 14, Palette.Brush(Tint.Orange), filled: true));
                row.Children.Add(Notice(error.Message));
                break;
        }
        return row;
    }

    private bool ShowsPreview => _model.Phase is HudPhase.Recording && _model.Preview.Length > 0;
    private bool _previewShown;

    private void OnPreviewChanged()
    {
        // Growing or shrinking the capsule needs a new layout; otherwise only the text changes.
        if (ShowsPreview != _previewShown) Render();
        else if (ShowsPreview)
        {
            _preview.Text = _model.Preview;
            if (_visible) Resize(position: false);
        }
    }

    /// <summary>Starts or stops advancing the ring round the ■ button on every frame.</summary>
    private void SetAnimating(bool on)
    {
        // Drawn again (a badge, the theme): the new ring picks up where the old one was.
        _ring?.SetProgress(_model.Progress.Shown);
        if (on == _animating) return;
        _animating = on;
        if (on)
        {
            _frameClock.Restart();
            _lastFrame = TimeSpan.Zero;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, object args)
    {
        var now = _frameClock.Elapsed;
        var shown = _model.Progress.Tick((now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        _ring?.SetProgress(shown);
    }

    /// <summary>
    /// Three dots riding a wave from left to right while the text is on its way. The compositor runs the
    /// wave, the same as the macOS dots: 1.15 s long, each dot a fifth of it behind the one before.
    /// </summary>
    private static StackPanel WaveDots()
    {
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 3.5, Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 4, Height = 4, Fill = Ui.Primary, Opacity = 0.9, VerticalAlignment = VerticalAlignment.Center };
            var index = i;
            dot.Loaded += (_, _) => Wave(dot, index);
            dots.Children.Add(dot);
        }
        return dots;
    }

    private static void Wave(UIElement dot, int index)
    {
        const float period = 1.15f, lag = 0.2f, amplitude = 4.5f;
        const int samples = 24;
        ElementCompositionPreview.SetIsTranslationEnabled(dot, true);
        var visual = ElementCompositionPreview.GetElementVisual(dot);
        var compositor = visual.Compositor;
        var linear = compositor.CreateLinearEasingFunction();
        var animation = compositor.CreateScalarKeyFrameAnimation();
        // One wave, sampled finely enough to look smooth.
        for (var k = 0; k <= samples; k++)
        {
            var t = (float)k / samples;
            animation.InsertKeyFrame(t, -amplitude * MathF.Sin(2 * MathF.PI * (t - index * lag)), linear);
        }
        animation.Duration = TimeSpan.FromSeconds(period);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation.Y", animation);
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The dictation's mode in the capsule ("→ EN"): a small pill in the accent colour, like the status pills elsewhere.</summary>
    private static Border ModeBadge(string text) => new()
    {
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 1, 7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Background = Palette.Brush(Tint.Accent, 0.18),
        Child = new TextBlock
        {
            Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.Theme("AccentTextFillColorPrimaryBrush"),
        },
    };

    /// <summary>A notice's text, wrapping onto up to three lines (the capsule grows to fit, see <see cref="Resize"/>).</summary>
    private static TextBlock Notice(string text)
    {
        var block = Label(text);
        block.MaxWidth = NoticeWidthDips;
        block.TextWrapping = TextWrapping.Wrap;
        block.MaxLines = NoticeLines;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    private void UpdateLevels() => _bars.SetLevels(HudModel.Waveform(_model.Levels), animated: true);

    /// <summary>Sizes the capsule to its content, bottom-centre of the screen under the mouse.</summary>
    private void Resize(bool position)
    {
        _previewShown = ShowsPreview;
        var heightDips = _previewShown ? HeightDips + PreviewDips : HeightDips;
        // Unbounded height, so a notice that wraps reports all of its lines.
        _root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var contentWidth = _root.DesiredSize.Width;
        var contentHeight = _root.DesiredSize.Height;
        if (contentWidth <= 1)
        {
            contentWidth = EstimatedWidth();
            contentHeight = EstimatedHeight();
        }
        // The buttons sit exactly at the ends; a notice gets a little slack.
        var widthDips = HasButtons ? Math.Clamp(Math.Ceiling(contentWidth), 120, 420) : Math.Clamp(contentWidth + 4, 120, 420);
        // One line keeps the usual height; more lines grow the capsule upwards from its bottom edge.
        if (HudController.Message(_model.Phase) != null) heightDips = Math.Max(heightDips, Math.Ceiling(contentHeight) + 2 * NoticePaddingDips);

        if (position || _centerX == int.MinValue)
        {
            var (work, _, scale) = WindowHelpers.MonitorAtCursor();
            var width = (int)Math.Round(widthDips * scale);
            var height = (int)Math.Round(heightDips * scale);
            _centerX = (work.Left + work.Right) / 2;
            _bottom = work.Bottom - (int)Math.Round(BottomMarginDips * scale);
            _placed = new RectInt32(_centerX - width / 2, _bottom - height, width, height);
            WindowHelpers.PlaceClient(this, _placed);
            Shape(width, height, scale);
            _glow.Follow(_placed, scale);
        }
        else
        {
            var scale = WindowHelpers.Scale(Hwnd);
            var width = (int)Math.Round(widthDips * scale);
            var height = (int)Math.Round(heightDips * scale);
            var rect = new RectInt32(_centerX - width / 2, _bottom - height, width, height);
            // The live preview lands here several times a second, mostly without changing the size: moving the window
            // onto itself would still cost a round of window messages and layout.
            if (rect.Equals(_placed)) return;
            _placed = rect;
            WindowHelpers.PlaceClient(this, rect);
            Shape(width, height, scale);
            _glow.Follow(_placed, scale);
        }
    }

    private int _bottom;
    private RectInt32 _placed;
    private (int Width, int Height, int Diameter) _shape;

    /// <summary>
    /// Cuts the window to the capsule: its client area (<paramref name="width"/> × <paramref name="height"/> physical
    /// pixels) with corners of <see cref="CornerDips"/>.
    /// </summary>
    private void Shape(int width, int height, double scale)
    {
        var diameter = Math.Min(height, (int)Math.Round(2 * CornerDips * scale));
        if (_shape == (width, height, diameter)) return;
        _shape = (width, height, diameter);
        // A window region is in window coordinates, which start at the frame, not the client area.
        Win32.GetWindowRect(Hwnd, out var window);
        var client = new Win32.POINT();
        Win32.ClientToScreen(Hwnd, ref client);
        var x = client.X - window.Left;
        var y = client.Y - window.Top;
        var region = Win32.CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, diameter, diameter);
        // Once set, the region belongs to the window (which frees the one it had); only a refused one is ours to free.
        if (region != 0 && Win32.SetWindowRgn(Hwnd, region, true) == 0) Win32.DeleteObject(region);
    }

    private double EstimatedWidth() => _model.Phase switch
    {
        HudPhase.Recording or HudPhase.Working =>
            2 * (ButtonInsetDips + ButtonDips + GapDips) + WaveDips + (_model.Badge is { } badge ? 24 + badge.Length * 7 : 0),
        HudPhase.Copied => 170,
        HudPhase.Learned l => Math.Min(372, 150 + l.Terms.Length * 9),
        HudPhase.Answer a => Math.Min(372, 150 + a.Text.Length * 13),
        HudPhase.Spending s => Math.Min(372, 40 + s.Reminder.Message.Length * 13),
        HudPhase.Error e => Math.Min(372, 40 + e.Message.Length * 13),
        _ => 120,
    };

    /// <summary>A notice's height before XAML has measured it: about 13 dips per character, 18 per line.</summary>
    private double EstimatedHeight() => HudController.Message(_model.Phase) is { } message
        ? Math.Min(NoticeLines, Math.Ceiling(message.Length * 13 / NoticeWidthDips)) * 18
        : 0;

    /// <summary>
    /// Clicks never activate the capsule (the text field keeps focus). They fall through to what's below, except while
    /// the capsule shows its buttons.
    /// </summary>
    private nint Subclass(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Win32.WM_MOUSEACTIVATE) return Win32.MA_NOACTIVATE;
        if (msg == Win32.WM_NCHITTEST && !HasButtons) return Win32.HTTRANSPARENT;
        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}

/// <summary>
/// The ring round the capsule's ■ button, drawn by the compositor: an arc in the accent colour from the top, as far as
/// the work has got (see <see cref="ProcessingProgress"/>).
/// </summary>
internal sealed class ProgressArc
{
    private const float StrokeWidth = 2;

    /// <summary>Placeholder in the XAML tree that the ring is drawn on.</summary>
    public FrameworkElement Element { get; }

    private readonly CompositionEllipseGeometry _geometry;
    private readonly CompositionSpriteShape _shape;

    public ProgressArc(double size, Color color)
    {
        var host = new Border { Width = size, Height = size, IsHitTestVisible = false };
        Element = host;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var center = new Vector2((float)size / 2);
        _geometry = compositor.CreateEllipseGeometry();
        _geometry.Center = center;
        _geometry.Radius = center - new Vector2(StrokeWidth / 2 + 0.5f);
        _geometry.TrimEnd = 0;
        _shape = compositor.CreateSpriteShape(_geometry);
        _shape.StrokeBrush = compositor.CreateColorBrush(color);
        _shape.StrokeThickness = 0;
        _shape.StrokeStartCap = CompositionStrokeCap.Round;
        _shape.StrokeEndCap = CompositionStrokeCap.Round;
        // The ellipse starts at three o'clock: turned a quarter back, it starts at the top.
        _shape.CenterPoint = center;
        _shape.RotationAngleInDegrees = -90;
        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2((float)size);
        visual.Shapes.Add(_shape);
        ElementCompositionPreview.SetElementChildVisual(host, visual);
    }

    public void SetProgress(double value)
    {
        var progress = double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : 0;
        _geometry.TrimEnd = progress;
        // Nothing at all until it has started (a round cap would draw a dot).
        _shape.StrokeThickness = progress > 0.001f ? StrokeWidth : 0;
    }
}

/// <summary>
/// The capsule's 9 waveform bars (see <see cref="HudModel.Waveform"/>), dots while it's quiet, drawn and animated by
/// the compositor instead of XAML layout. A new level only starts short linear animations of each bar's rounded
/// rectangle, which the compositor interpolates at the display's refresh rate on its own thread, like the macOS bars'
/// <c>.animation(.easeOut(duration: 0.1))</c>.
/// (Resizing XAML shapes on every 40 ms level re-ran layout and re-rasterised each bar on the UI thread, and the
/// bars jumped in uneven steps.) Resting positions are snapped to physical pixels, as XAML layout rounding did.
/// </summary>
internal sealed class LevelBars
{
    private const int Count = 9;
    /// <summary>A bar at its shortest is as tall as it is wide: a dot.</summary>
    private const double BarWidth = 2.5, Spacing = 4, MaxHeight = 20, MinHeight = BarWidth;
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>Placeholder in the XAML row that the bars are drawn on.</summary>
    public FrameworkElement Element { get; }

    private readonly CompositionColorBrush _fill;
    private readonly CompositionSpriteShape[] _shapes = new CompositionSpriteShape[Count];
    private readonly CompositionRoundedRectangleGeometry[] _geometries = new CompositionRoundedRectangleGeometry[Count];
    private readonly Vector2KeyFrameAnimation _animation;
    private readonly float[] _levels = new float[Count];
    private readonly float[] _targetHeights = new float[Count];
    private double _scale;
    private float _barWidth;
    private int _totalPixels;

    public LevelBars()
    {
        var width = Count * BarWidth + (Count - 1) * Spacing;
        var host = new Border { Width = width, Height = MaxHeight, VerticalAlignment = VerticalAlignment.Center };
        Element = host;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _fill = compositor.CreateColorBrush(Colors.White);
        var visual = compositor.CreateShapeVisual();
        // A little wider than the placeholder so pixel snapping can never clip the last bar.
        visual.Size = new Vector2((float)width + 2, (float)MaxHeight + 2);
        for (var i = 0; i < Count; i++)
        {
            _geometries[i] = compositor.CreateRoundedRectangleGeometry();
            _shapes[i] = compositor.CreateSpriteShape(_geometries[i]);
            _shapes[i].FillBrush = _fill;
            visual.Shapes.Add(_shapes[i]);
        }
        ElementCompositionPreview.SetElementChildVisual(host, visual);

        // One template for every bar: StartAnimation copies it, so only the "target" parameter changes per start.
        // With no keyframe at 0 each animation starts from the bar's current (possibly still animating) value.
        _animation = compositor.CreateVector2KeyFrameAnimation();
        _animation.InsertExpressionKeyFrame(1f, "target", compositor.CreateLinearEasingFunction());
        _animation.Duration = AnimationDuration;

        SetScale(1);
    }

    public void SetColor(Color color) => _fill.Color = color;

    /// <summary>Lays the bars out on the physical pixel grid of <paramref name="scale"/> (DPI / 96).</summary>
    public void SetScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || scale == _scale) return;
        _scale = scale;
        _totalPixels = Math.Max(1, (int)Math.Round(MaxHeight * scale));
        _barWidth = (float)(Math.Max(1, Math.Round(BarWidth * scale)) / scale);
        for (var i = 0; i < Count; i++)
        {
            _shapes[i].Offset = new Vector2((float)(Math.Round(i * (BarWidth + Spacing) * scale) / scale), 0);
            _geometries[i].CornerRadius = new Vector2(_barWidth / 2);
            Apply(i, animated: false);
        }
    }

    public void SetLevels(float[] levels, bool animated)
    {
        for (var i = 0; i < Count; i++)
        {
            var level = i < levels.Length ? levels[i] : 0;
            _levels[i] = float.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
            Apply(i, animated);
        }
    }

    private void Apply(int i, bool animated)
    {
        // Height and top edge in whole pixels, centred like the XAML bars were.
        var pixels = Math.Clamp((int)Math.Round(Math.Max(MinHeight, _levels[i] * MaxHeight) * _scale), 1, _totalPixels);
        var top = Math.Round((_totalPixels - pixels) / 2.0);
        var height = (float)(pixels / _scale);
        var geometry = _geometries[i];
        if (!animated)
        {
            geometry.StopAnimation("Size");
            geometry.StopAnimation("Offset");
            geometry.Size = new Vector2(_barWidth, height);
            geometry.Offset = new Vector2(0, (float)(top / _scale));
            _targetHeights[i] = height;
            return;
        }
        if (height == _targetHeights[i]) return; // Nothing new: leave the running animation alone.
        _targetHeights[i] = height;
        _animation.SetVector2Parameter("target", new Vector2(_barWidth, height));
        geometry.StartAnimation("Size", _animation);
        _animation.SetVector2Parameter("target", new Vector2(0, (float)(top / _scale)));
        geometry.StartAnimation("Offset", _animation);
    }
}
