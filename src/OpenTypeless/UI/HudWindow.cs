using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenTypeless.Native;
using OpenTypeless.Session;
using Windows.Graphics;

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

        if (autoHideAfter is { } seconds)
        {
            _hideTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
            _hideTimer.IsRepeating = false;
            _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.01, seconds));
            _hideTimer.Tick -= OnHideTimer;
            _hideTimer.Tick += OnHideTimer;
            _hideTimer.Start();
        }
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
    private const double BottomMarginDips = 28;

    private readonly HudModel _model;
    private readonly Grid _root = new() { Padding = new Thickness(16, 0, 16, 0) };
    private readonly Rectangle[] _bars = new Rectangle[18];
    private readonly StackPanel _barsPanel = new() { Orientation = Orientation.Horizontal, Spacing = 2, Height = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = new() { FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
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
        SystemBackdrop = new ActiveAcrylicBackdrop();

        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle { Width = 2.5, Height = 3, RadiusX = 1.25, RadiusY = 1.25, VerticalAlignment = VerticalAlignment.Center };
            _barsPanel.Children.Add(_bars[i]);
        }
        _root.VerticalAlignment = VerticalAlignment.Stretch;
        Content = _root;

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(0.5);
        _clock.Tick += (_, _) => UpdateElapsed();

        _model.PhaseChanged += Render;
        _model.LevelsChanged += UpdateLevels;
        Render();
    }

    public void ShowHud()
    {
        Render();
        Resize(position: !_visible);
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
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
    }

    private void Render()
    {
        // The level bars and timer are reused: release them from the previous row before rebuilding.
        foreach (var old in _root.Children.OfType<Panel>()) old.Children.Clear();
        _root.Children.Clear();
        _clock.Stop();
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        switch (_model.Phase)
        {
            case HudPhase.Recording:
                row.Spacing = 10;
                row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Palette.Brush(Tint.Red), VerticalAlignment = VerticalAlignment.Center });
                foreach (var bar in _bars) bar.Fill = Ui.Primary;
                row.Children.Add(_barsPanel);
                row.Children.Add(_elapsed);
                UpdateLevels();
                UpdateElapsed();
                _clock.Start();
                break;
            case HudPhase.Working:
                row.Spacing = 9;
                row.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
                row.Children.Add(Label(L("处理中", "Working")));
                break;
            case HudPhase.Copied:
                row.Spacing = 8;
                row.Children.Add(Ui.Icon(Glyphs.Paste, 14, Ui.Secondary));
                row.Children.Add(Label(L("已复制到剪贴板", "Copied to clipboard")));
                break;
            case HudPhase.Error error:
                row.Spacing = 8;
                row.Children.Add(Ui.Icon(Glyphs.Warning, 14, Palette.Brush(Tint.Orange)));
                var text = Label(error.Message);
                text.MaxWidth = 340;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.TextWrapping = TextWrapping.NoWrap;
                row.Children.Add(text);
                break;
        }
        _root.Children.Add(row);
        if (_visible) Resize(position: false);
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };

    private void UpdateLevels()
    {
        var levels = _model.Levels;
        for (var i = 0; i < _bars.Length && i < levels.Length; i++) _bars[i].Height = Math.Max(3, levels[i] * 18);
    }

    private void UpdateElapsed()
    {
        var seconds = Math.Max(0, (int)(DateTimeOffset.Now - _model.StartedAt).TotalSeconds);
        _elapsed.Text = $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>Sizes the capsule to its content, bottom-centre of the screen under the mouse.</summary>
    private void Resize(bool position)
    {
        _root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, HeightDips));
        var contentWidth = _root.DesiredSize.Width;
        if (contentWidth <= 1) contentWidth = EstimatedWidth();
        var widthDips = Math.Clamp(contentWidth + 4, 120, 400);

        if (position || _centerX == int.MinValue)
        {
            var (work, _, scale) = WindowHelpers.MonitorAtCursor();
            var width = (int)Math.Round(widthDips * scale);
            var height = (int)Math.Round(HeightDips * scale);
            _centerX = (work.Left + work.Right) / 2;
            _bottom = work.Bottom - (int)Math.Round(BottomMarginDips * scale);
            WindowHelpers.PlaceClient(this, new RectInt32(_centerX - width / 2, _bottom - height, width, height));
        }
        else
        {
            var scale = WindowHelpers.Scale(Hwnd);
            var width = (int)Math.Round(widthDips * scale);
            var height = (int)Math.Round(HeightDips * scale);
            WindowHelpers.PlaceClient(this, new RectInt32(_centerX - width / 2, _bottom - height, width, height));
        }
    }

    private int _bottom;

    private double EstimatedWidth() => _model.Phase switch
    {
        HudPhase.Recording => 160,
        HudPhase.Working => 110,
        HudPhase.Copied => 170,
        HudPhase.Error e => Math.Min(372, 40 + e.Message.Length * 13),
        _ => 120,
    };

    /// <summary>Clicks never activate the capsule (the text field keeps focus) and fall through to what's below.</summary>
    private nint Subclass(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Win32.WM_MOUSEACTIVATE) return Win32.MA_NOACTIVATE;
        if (msg == Win32.WM_NCHITTEST) return Win32.HTTRANSPARENT;
        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
