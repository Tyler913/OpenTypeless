using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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

    public void Show(HudPhase phase, double? autoHideAfter = null)
    {
        _hideTimer?.Stop();
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

    /// <summary>The text a notice shows (its length decides how long it stays up).</summary>
    internal static string? Message(HudPhase phase) => phase switch
    {
        HudPhase.Learned learned => L("已加入词汇表：", "Added to vocabulary: ") + learned.Terms,
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

    /// <summary>Transcribing and cleaning up, with the bar starting from empty.</summary>
    public void ShowWorking(bool polishes)
    {
        Model.StartProgress(polishes);
        Show(new HudPhase.Working());
    }

    /// <summary>The text went in: the bar runs to the end, then the capsule goes.</summary>
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
/// Small Acrylic capsule near the bottom of the screen. Never takes focus or mouse clicks, so the target text
/// field stays focused for the paste.
/// </summary>
public sealed class HudWindow : Window
{
    private const double HeightDips = 44;
    /// <summary>Extra height for the live preview line above the level bars.</summary>
    private const double PreviewDips = 26;
    private const double BottomMarginDips = 28;
    /// <summary>Space above and below a notice that wraps onto more lines.</summary>
    private const double NoticePaddingDips = 11;
    /// <summary>A notice's text: up to three lines this wide, so the end of an error (often what to do) still shows.</summary>
    private const double NoticeWidthDips = 340;
    private const int NoticeLines = 3;

    private readonly HudModel _model;
    private readonly ActiveAcrylicBackdrop _backdrop = new();
    /// <summary>The processing bar behind <see cref="_root"/>, which holds the capsule's content.</summary>
    private readonly Grid _frame = new();
    private readonly Grid _root = new() { Padding = new Thickness(16, 0, 16, 0) };
    private readonly Border _fill;
    private readonly ScaleTransform _fillScale = new() { ScaleX = 0 };
    private readonly Stopwatch _frameClock = new();
    private TimeSpan _lastFrame;
    private bool _animating;
    private readonly LevelBars _bars;
    private readonly TextBlock _elapsed = new() { FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _preview = new()
    {
        FontSize = 13, MaxWidth = 380, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly DispatcherQueueTimer _clock;
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
        WindowHelpers.AddExtendedStyle(Hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST);
        _subclass = Subclass;
        Win32.SetWindowSubclass(Hwnd, _subclass, 1, 0);
        SystemBackdrop = _backdrop;

        _bars = new LevelBars();
        // Same-width digits, like the macOS timer's monospacedDigit(): the row doesn't shift every second.
        Typography.SetNumeralAlignment(_elapsed, FontNumeralAlignment.Tabular);
        _root.VerticalAlignment = VerticalAlignment.Stretch;
        _fill = new Border
        {
            RenderTransform = _fillScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _frame.Children.Add(_fill);
        _frame.Children.Add(_root);
        Content = _frame;

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(0.5);
        _clock.Tick += (_, _) => UpdateElapsed();

        _model.PhaseChanged += Render;
        _model.LevelsChanged += UpdateLevels;
        _model.PreviewChanged += OnPreviewChanged;
        _model.BadgeChanged += Render;
        _preview.Foreground = Ui.Secondary;
        _frame.ActualThemeChanged += (_, _) => OnThemeChanged();
        Render();
    }

    /// <summary>
    /// Windows switched between light and dark while the capsule exists: the acrylic follows, and the brushes picked
    /// up in code (which don't change by themselves) are fetched again by redrawing.
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
    }

    public void HideHud()
    {
        _visible = false;
        _clock.Stop();
        SetAnimating(false);
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
    }

    private void Render()
    {
        // The level bars, timer and preview line are reused: release them from the previous layout before rebuilding.
        foreach (var old in _root.Children.OfType<Panel>())
        {
            foreach (var inner in old.Children.OfType<Panel>()) inner.Children.Clear();
            old.Children.Clear();
        }
        _root.Children.Clear();
        _clock.Stop();
        var working = _model.Phase is HudPhase.Working;
        _fill.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        SetAnimating(working);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        switch (_model.Phase)
        {
            case HudPhase.Recording:
                row.Spacing = 10;
                row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Palette.Brush(Tint.Red), VerticalAlignment = VerticalAlignment.Center });
                _bars.SetColor(Ui.Primary is SolidColorBrush primary ? primary.Color : _root.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White);
                _bars.SetScale(WindowHelpers.Scale(Hwnd));
                // A new recording starts from its own levels, not from the end of the previous one.
                _bars.SetLevels(_model.Levels, animated: false);
                row.Children.Add(_bars.Element);
                row.Children.Add(_elapsed);
                if (_model.Badge is { } recordingBadge) row.Children.Add(ModeBadge(recordingBadge));
                UpdateElapsed();
                _clock.Start();
                break;
            case HudPhase.Working:
                row.Spacing = 10;
                // About as wide as the recording row, so the capsule keeps its size when the key is released.
                row.Children.Add(new Grid { Width = 128, Children = { HoppingDots() } });
                if (_model.Badge is { } workingBadge) row.Children.Add(ModeBadge(workingBadge));
                break;
            case HudPhase.Copied:
                row.Spacing = 8;
                row.Children.Add(Ui.Icon(Glyphs.Paste, 14, Ui.Secondary));
                row.Children.Add(Label(L("已复制到剪贴板", "Copied to clipboard")));
                break;
            case HudPhase.Learned:
                row.Spacing = 8;
                row.Children.Add(Ui.Icon(Glyphs.Dictionary, 14, Palette.Theme("AccentTextFillColorPrimaryBrush")));
                row.Children.Add(Notice(HudController.Message(_model.Phase) ?? ""));
                break;
            case HudPhase.Error error:
                row.Spacing = 8;
                row.Children.Add(Ui.Icon(Glyphs.Warning, 14, Palette.Brush(Tint.Orange)));
                row.Children.Add(Notice(error.Message));
                break;
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
        if (_visible) Resize(position: false);
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

    /// <summary>Starts or stops advancing the processing bar on every frame.</summary>
    private void SetAnimating(bool on)
    {
        if (on == _animating) return;
        _animating = on;
        if (on)
        {
            // The accent colour may have changed since the last dictation.
            var accent = Palette.Of(Tint.Accent);
            _fill.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.5),
                EndPoint = new Windows.Foundation.Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(31, accent.R, accent.G, accent.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(77, accent.R, accent.G, accent.B), Offset = 1 },
                },
            };
            _fillScale.ScaleX = _model.Progress.Shown;
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
        _fillScale.ScaleX = _model.Progress.Tick((now - _lastFrame).TotalSeconds);
        _lastFrame = now;
    }

    /// <summary>
    /// Three dots hopping in turn, like a voice still talking, while the text is on its way. The compositor runs the
    /// hops, with the same rhythm as the macOS dots: each one hops for 0.36 s, 0.14 s after the one before, every 1.1 s.
    /// </summary>
    private static StackPanel HoppingDots()
    {
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Primary, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            var delay = TimeSpan.FromSeconds(0.14 * i);
            dot.Loaded += (_, _) => Hop(dot, delay);
            dots.Children.Add(dot);
        }
        return dots;
    }

    private static void Hop(UIElement dot, TimeSpan delay)
    {
        const float period = 1.1f, top = 0.18f / period, landed = 0.36f / period;
        ElementCompositionPreview.SetIsTranslationEnabled(dot, true);
        var visual = ElementCompositionPreview.GetElementVisual(dot);
        var compositor = visual.Compositor;
        var up = compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 0), new Vector2(0.67f, 1));

        ScalarKeyFrameAnimation Cycle(float rest, float peak)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, rest);
            animation.InsertKeyFrame(top, peak, up);
            animation.InsertKeyFrame(landed, rest, up);
            animation.InsertKeyFrame(1, rest);
            animation.Duration = TimeSpan.FromSeconds(period);
            animation.DelayTime = delay;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            return animation;
        }

        visual.StartAnimation("Translation.Y", Cycle(0, -4.5f));
        visual.StartAnimation("Opacity", Cycle(0.6f, 0.9f));
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

    private void UpdateLevels() => _bars.SetLevels(_model.Levels, animated: true);

    private void UpdateElapsed()
    {
        var seconds = Math.Max(0, (int)(DateTimeOffset.Now - _model.StartedAt).TotalSeconds);
        _elapsed.Text = $"{seconds / 60}:{seconds % 60:00}";
    }

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
        var widthDips = Math.Clamp(contentWidth + 4, 120, 420);
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
        }
    }

    private int _bottom;
    private RectInt32 _placed;

    private double EstimatedWidth() => _model.Phase switch
    {
        HudPhase.Recording => 160,
        HudPhase.Working => 160,
        HudPhase.Copied => 170,
        HudPhase.Learned l => Math.Min(372, 150 + l.Terms.Length * 9),
        HudPhase.Error e => Math.Min(372, 40 + e.Message.Length * 13),
        _ => 120,
    };

    /// <summary>A notice's height before XAML has measured it: about 13 dips per character, 18 per line.</summary>
    private double EstimatedHeight() => HudController.Message(_model.Phase) is { } message
        ? Math.Min(NoticeLines, Math.Ceiling(message.Length * 13 / NoticeWidthDips)) * 18
        : 0;

    /// <summary>Clicks never activate the capsule (the text field keeps focus) and fall through to what's below.</summary>
    private nint Subclass(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Win32.WM_MOUSEACTIVATE) return Win32.MA_NOACTIVATE;
        if (msg == Win32.WM_NCHITTEST) return Win32.HTTRANSPARENT;
        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}

/// <summary>
/// The capsule's 18 level bars, drawn and animated by the compositor instead of XAML layout. A new level only
/// starts short linear animations of each bar's rounded rectangle, which the compositor interpolates at the
/// display's refresh rate on its own thread, like the macOS bars' <c>.animation(.linear(duration: 0.08))</c>.
/// (Resizing XAML shapes on every 40 ms level re-ran layout and re-rasterised each bar on the UI thread, and the
/// bars jumped in uneven steps.) Resting positions are snapped to physical pixels, as XAML layout rounding did.
/// </summary>
internal sealed class LevelBars
{
    private const int Count = 18;
    private const double BarWidth = 2.5, Spacing = 2, MaxHeight = 18, MinHeight = 3;
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(80);

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
