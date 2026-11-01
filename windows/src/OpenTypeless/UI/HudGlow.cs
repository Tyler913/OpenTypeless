using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTypeless.Native;
using Windows.Graphics;

namespace OpenTypeless.UI;

/// <summary>
/// The glow round the capsule as a recording starts, as on macOS: a halo of halftone dots over a soft wash of colour,
/// cyan-blue above, teal at the sides and yellow-green below, the dots largest next to the capsule. It fades in and out
/// within two seconds. Mica fills the capsule's whole window, so nothing can be drawn outside the capsule there: the
/// glow is a window of its own just behind it, layered (per-pixel alpha), click-through and never activated. It's drawn
/// once for each size, then only faded, so the UI thread (which also runs the keyboard hook) has next to nothing to do.
/// </summary>
internal sealed class HudGlow
{
    /// <summary>How far the glow reaches beyond the capsule, and how far apart its dots are.</summary>
    private const double MarginDips = 22, SpacingDips = 3.4;
    /// <summary>In, held, out: the same timing as the macOS glow.</summary>
    private const double FadeIn = 0.35, Hold = 0.35, FadeOut = 1.1;
    private const string ClassName = "OpenTypelessHudGlow";

    /// <summary>The window class is registered once, with a procedure that is never collected.</summary>
    private static readonly Win32.WNDPROC Procedure = Win32.DefWindowProc;
    private static bool _registered;

    private readonly nint _hwnd;
    private readonly Stopwatch _clock = new();
    private bool _blooming;
    /// <summary>The capsule the glow was last drawn round (physical pixels), and at what scale.</summary>
    private RectInt32 _capsule;
    private double _scale;

    public HudGlow()
    {
        var instance = Win32.GetModuleHandle(null);
        if (!_registered)
        {
            var windowClass = new Win32.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Procedure,
                hInstance = instance,
                lpszClassName = ClassName,
            };
            _registered = Win32.RegisterClassEx(ref windowClass) != 0;
        }
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST,
            ClassName, "", Win32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0) Services.AppLog.Debug("hud", $"no glow window (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Starts the glow round <paramref name="capsule"/> (the capsule window's client area, physical pixels).</summary>
    public void Bloom(RectInt32 capsule, double scale, nint capsuleWindow)
    {
        if (_hwnd == 0) return;
        Draw(capsule, scale, alpha: 0);
        Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        // Just behind the capsule, and like it above everything else.
        Win32.SetWindowPos(_hwnd, capsuleWindow, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        _clock.Restart();
        if (_blooming) return;
        _blooming = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>The capsule changed size while the glow shows (the preview line, a mode badge): the glow follows it.</summary>
    public void Follow(RectInt32 capsule, double scale)
    {
        if (!_blooming || (capsule.Equals(_capsule) && scale == _scale)) return;
        Draw(capsule, scale, Alpha(_clock.Elapsed.TotalSeconds));
    }

    public void Stop()
    {
        if (!_blooming) return;
        _blooming = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
    }

    private void OnFrame(object? sender, object args)
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        if (seconds >= FadeIn + Hold + FadeOut)
        {
            Stop();
            return;
        }
        var blend = Blend(Alpha(seconds));
        Win32.UpdateLayeredWindow(_hwnd, 0, 0, 0, 0, 0, 0, ref blend, Win32.ULW_ALPHA);
    }

    /// <summary>How strong the glow is <paramref name="seconds"/> into it (0…1), eased in and out.</summary>
    private static double Alpha(double seconds)
    {
        static double Ease(double t) => 0.5 - 0.5 * Math.Cos(Math.PI * Math.Clamp(t, 0, 1));
        if (seconds < FadeIn) return Ease(seconds / FadeIn);
        if (seconds < FadeIn + Hold) return 1;
        return 1 - Ease((seconds - FadeIn - Hold) / FadeOut);
    }

    private static Win32.BLENDFUNCTION Blend(double alpha) => new()
    {
        BlendOp = Win32.AC_SRC_OVER,
        SourceConstantAlpha = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
        AlphaFormat = Win32.AC_SRC_ALPHA,
    };

    /// <summary>Draws the glow round the capsule into the window, and puts the window round the capsule.</summary>
    private void Draw(RectInt32 capsule, double scale, double alpha)
    {
        _capsule = capsule;
        _scale = scale;
        var margin = (int)Math.Round(MarginDips * scale);
        var width = capsule.Width + 2 * margin;
        var height = capsule.Height + 2 * margin;
        if (capsule.Width <= 0 || capsule.Height <= 0) return;

        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        var screen = Win32.GetDC(0);
        var memory = Win32.CreateCompatibleDC(screen);
        var bitmap = Win32.CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
        try
        {
            if (bitmap == 0 || bits == 0) return;
            var previous = Win32.SelectObject(memory, bitmap);
            // The capsule's corners: a capsule at the usual height, the same corners when it's taller.
            var corner = Math.Min(capsule.Height / 2.0, 22 * scale);
            Render(bits, width, height, margin, corner, SpacingDips * scale);
            var position = new Win32.POINT { X = capsule.X - margin, Y = capsule.Y - margin };
            var size = new Win32.SIZE { cx = width, cy = height };
            var origin = new Win32.POINT();
            var blend = Blend(alpha);
            Win32.UpdateLayeredWindow(_hwnd, screen, ref position, ref size, memory, ref origin, 0, ref blend, Win32.ULW_ALPHA);
            Win32.SelectObject(memory, previous);
        }
        finally
        {
            if (bitmap != 0) Win32.DeleteObject(bitmap);
            Win32.DeleteDC(memory);
            Win32.ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// The glow's pixels, premultiplied BGRA: round a rounded rectangle <paramref name="margin"/> in from every edge,
    /// a wash fading out over the margin, with dots on a grid turned 45° over it, as large as they get next to the
    /// capsule and shrinking to nothing at the margin. Under the capsule it's covered by the capsule's window.
    /// </summary>
    private static unsafe void Render(nint bits, int width, int height, int margin, double corner, double spacing)
    {
        var pixels = (uint*)bits;
        double centerX = width / 2.0, centerY = height / 2.0;
        var halfWidth = width / 2.0 - margin;
        var halfHeight = height / 2.0 - margin;
        var radius = Math.Min(corner, Math.Min(halfWidth, halfHeight));
        // The dots sit on the points of a square grid (rows and columns `step` apart) whose row and column add up to an
        // even number: a grid turned 45° with neighbours `spacing` apart.
        var step = spacing / Math.Sqrt(2);
        for (var y = 0; y < height; y++)
        {
            var (red, green, blue) = Shade(height > 1 ? (double)y / (height - 1) : 0.5);
            for (var x = 0; x < width; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                // How far outside the capsule (negative inside).
                var qx = Math.Abs(px - centerX) - (halfWidth - radius);
                var qy = Math.Abs(py - centerY) - (halfHeight - radius);
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                var outside = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
                var strength = 1 - Math.Clamp(outside / margin, 0, 1);
                if (strength <= 0)
                {
                    pixels[y * width + x] = 0;
                    continue;
                }
                double u = px / step, v = py / step;
                double a = Math.Round((u + v) / 2), b = Math.Round((u - v) / 2);
                double du = u - (a + b), dv = v - (a - b);
                var distance = step * Math.Sqrt(du * du + dv * dv);
                var dotRadius = 0.42 * spacing * Math.Sqrt(strength);
                var dot = Math.Clamp(dotRadius - distance + 0.5, 0, 1) * 0.9;
                var wash = 0.35 * strength * strength;
                var alpha = dot + wash * (1 - dot);
                var a8 = (uint)Math.Round(alpha * 255);
                var r8 = (uint)Math.Round(red * alpha);
                var g8 = (uint)Math.Round(green * alpha);
                var b8 = (uint)Math.Round(blue * alpha);
                pixels[y * width + x] = (a8 << 24) | (r8 << 16) | (g8 << 8) | b8;
            }
        }
    }

    /// <summary>The glow's colour from top (0) to bottom (1): cyan-blue, teal, green, yellow-green.</summary>
    private static (double Red, double Green, double Blue) Shade(double t)
    {
        ReadOnlySpan<double> stops = [0, 0.4, 0.7, 1];
        ReadOnlySpan<double> colors = [70, 170, 245, 40, 200, 210, 90, 215, 130, 195, 238, 85];
        for (var i = 1; i < stops.Length; i++)
        {
            if (t > stops[i] && i < stops.Length - 1) continue;
            var f = Math.Clamp((t - stops[i - 1]) / (stops[i] - stops[i - 1]), 0, 1);
            var from = (i - 1) * 3;
            var to = i * 3;
            return (colors[from] + (colors[to] - colors[from]) * f,
                    colors[from + 1] + (colors[to + 1] - colors[from + 1]) * f,
                    colors[from + 2] + (colors[to + 2] - colors[from + 2]) * f);
        }
        return (colors[^3], colors[^2], colors[^1]);
    }
}
