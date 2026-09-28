using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenTypeless.Native;

namespace OpenTypeless.UI;

/// <summary>
/// Desktop Acrylic that stays "active" even when the window never takes focus. The stock backdrop falls back to
/// a flat colour whenever its window is inactive, which a non-activating HUD always is.
/// </summary>
public sealed class ActiveAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        var theme = (xamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        var configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = theme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => Application.Current.RequestedTheme == ApplicationTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            },
        };
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }
}

public static class WindowHelpers
{
    /// <summary>A chrome-less, always-on-top window that doesn't appear in the taskbar or Alt+Tab.</summary>
    public static void MakeFloating(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        window.AppWindow.IsShownInSwitchers = false;
    }

    public static double Scale(nint hwnd) => Win32.GetDpiForWindow(hwnd) / 96.0;

    /// <summary>
    /// Places a window so its *client area* covers <paramref name="client"/> (physical pixels). AppWindow sizes
    /// include the frame and its invisible resize borders, which would otherwise eat into the content.
    /// </summary>
    public static void PlaceClient(Window window, Windows.Graphics.RectInt32 client)
    {
        var appWindow = window.AppWindow;
        var dx = Math.Max(0, appWindow.Size.Width - appWindow.ClientSize.Width);
        var dy = Math.Max(0, appWindow.Size.Height - appWindow.ClientSize.Height);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(client.X - dx / 2, client.Y - dy / 2, client.Width + dx, client.Height + dy));
    }

    public static void AddExtendedStyle(nint hwnd, long style)
    {
        var current = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (nint)(current | style));
    }

    /// <summary>The work area and DPI scale of the monitor under the mouse pointer.</summary>
    public static (Win32.RECT Work, Win32.RECT Monitor, double Scale) MonitorAtCursor()
    {
        Win32.GetCursorPos(out var point);
        return MonitorAt(point);
    }

    public static (Win32.RECT Work, Win32.RECT Monitor, double Scale) MonitorAt(Win32.POINT point)
    {
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        var dpi = Win32.GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : 96u;
        return (info.rcWork, info.rcMonitor, dpi / 96.0);
    }
}
