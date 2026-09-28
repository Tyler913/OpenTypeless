using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenTypeless.Native;
using OpenTypeless.Session;

namespace OpenTypeless.UI;

/// <summary>
/// The notification-area icon: the Windows counterpart of the macOS menu-bar status item.
/// A hidden top-level window receives its clicks (and doubles as the clipboard owner); it is top-level rather
/// than message-only so it also hears "TaskbarCreated" when Explorer restarts, and re-adds the icon.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Clicked (left or right) with the icon's screen rectangle, for anchoring the popover.</summary>
    public event Action<Win32.RECT>? Clicked;

    public nint Hwnd { get; }

    private const uint CallbackMessage = Win32.WM_APP + 1;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private static readonly Guid IconGuid = new("7f1e2c4a-4b1d-4d6e-9a53-0e3c5a1f7b21");

    private readonly uint _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
    private readonly Win32.WNDPROC _wndProc; // kept alive for the window's lifetime
    private SessionController.SessionState _state = SessionController.SessionState.Idle;
    private nint _icon;
    private bool _added;

    public TrayIcon()
    {
        _wndProc = WndProc;
        var instance = Win32.GetModuleHandle(null);
        var windowClass = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = "OpenTypelessTrayWindow",
        };
        var atom = Win32.RegisterClassEx(ref windowClass);
        var registerError = Marshal.GetLastWin32Error();
        Hwnd = Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW, "OpenTypelessTrayWindow", "OpenTypeless", unchecked((long)0x80000000) /* WS_POPUP */,
                                    0, 0, 0, 0, 0, 0, instance, 0);
        Services.AppLog.Debug("tray", $"class atom {atom} (error {registerError}), window {Hwnd} (error {Marshal.GetLastWin32Error()})");
        Add();
    }

    public void SetState(SessionController.SessionState state)
    {
        _state = state;
        Update(Win32.NIM_MODIFY);
    }

    /// <summary>Screen rectangle of the icon (physical pixels), or null if the shell can't tell.</summary>
    public Win32.RECT? IconRect()
    {
        var id = new Win32.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Win32.NOTIFYICONIDENTIFIER>(), hWnd = Hwnd, uID = 1 };
        return Win32.Shell_NotifyIconGetRect(ref id, out var rect) == 0 ? rect : null;
    }

    private void Add()
    {
        _added = Update(Win32.NIM_ADD);
        if (_added)
        {
            var data = Data();
            data.uVersion = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref data);
        }
    }

    private bool Update(uint message)
    {
        var old = _icon;
        _icon = TrayGlyphs.Render(_state, IconSize(), TaskbarIsLight());
        var data = Data();
        var ok = Win32.Shell_NotifyIcon(message, ref data);
        if (old != 0) Win32.DestroyIcon(old);
        return ok;
    }

    private Win32.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = Hwnd,
        uID = 1,
        uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = "OpenTypeless",
        szInfo = "",
        szInfoTitle = "",
    };

    private static int IconSize()
    {
        var monitor = Win32.MonitorFromPoint(new Win32.POINT(), Win32.MONITOR_DEFAULTTONEAREST);
        var dpi = Win32.GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : 96u;
        return Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi);
    }

    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the event is in the low word of lParam.
            var e = (uint)(lParam & 0xFFFF);
            if (e is Win32.NIN_SELECT or Win32.NIN_KEYSELECT or Win32.WM_CONTEXTMENU)
            {
                var rect = IconRect() ?? CursorRect();
                Clicked?.Invoke(rect);
            }
            return 0;
        }
        if (msg == _taskbarCreated)
        {
            Add(); // Explorer restarted
            return 0;
        }
        if (msg is WM_SETTINGCHANGE or WM_DPICHANGED or WM_DISPLAYCHANGE)
        {
            Update(Win32.NIM_MODIFY); // light/dark taskbar or scale changed
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static Win32.RECT CursorRect()
    {
        Win32.GetCursorPos(out var p);
        return new Win32.RECT { Left = p.X, Top = p.Y, Right = p.X, Bottom = p.Y };
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != 0) Win32.DestroyIcon(_icon);
        _icon = 0;
        Win32.DestroyWindow(Hwnd);
    }
}

/// <summary>
/// Monochrome tray glyphs drawn with antialiased signed-distance shapes: idle (waveform), recording (filled
/// circle with the waveform cut out), processing (circle with an ellipsis), the three SF Symbols the mac app uses.
/// </summary>
public static class TrayGlyphs
{
    public static nint Render(SessionController.SessionState state, int size, bool lightTaskbar)
    {
        var pixels = new uint[size * size];
        var ink = lightTaskbar ? 0x000000u : 0xFFFFFFu;
        const int samples = 4;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var covered = 0;
                for (var sy = 0; sy < samples; sy++)
                {
                    for (var sx = 0; sx < samples; sx++)
                    {
                        // Normalised coordinates in 0…1 across the icon.
                        var u = (x + (sx + 0.5) / samples) / size;
                        var v = (y + (sy + 0.5) / samples) / size;
                        if (Inside(state, u, v)) covered++;
                    }
                }
                var alpha = (uint)(covered * 255 / (samples * samples));
                // BGRA with straight (non-premultiplied) alpha, as icons expect.
                pixels[y * size + x] = alpha == 0 ? 0 : (alpha << 24) | ink;
            }
        }
        return CreateIcon(pixels, size);
    }

    private static bool Inside(SessionController.SessionState state, double u, double v)
    {
        switch (state)
        {
            case SessionController.SessionState.Recording:
                // Filled disc with the waveform knocked out.
                return Distance(u, v, 0.5, 0.5) <= 0.47 && !Waveform(u, v, 0.56, 0.5);
            case SessionController.SessionState.Processing:
                var ring = Math.Abs(Distance(u, v, 0.5, 0.5) - 0.41) <= 0.055;
                var dots = Distance(u, v, 0.29, 0.5) <= 0.07 || Distance(u, v, 0.5, 0.5) <= 0.07 || Distance(u, v, 0.71, 0.5) <= 0.07;
                return ring || dots;
            default:
                return Waveform(u, v, 0.92, 0.5);
        }
    }

    /// <summary>Six rounded bars (the app icon's waveform) centred on (0.5, cy), spanning <paramref name="width"/>.</summary>
    private static bool Waveform(double u, double v, double width, double cy)
    {
        var bars = AppTile.Bars;
        var pitch = width / (bars.Length - 0.35);
        var radius = pitch * 0.27;
        var x0 = 0.5 - pitch * (bars.Length - 1) / 2;
        for (var i = 0; i < bars.Length; i++)
        {
            var half = Math.Max(0, width * bars[i] / 2 - radius);
            var cx = x0 + i * pitch;
            // Capsule: distance to the vertical segment.
            var dy = Math.Max(0, Math.Abs(v - cy) - half);
            if (Math.Sqrt((u - cx) * (u - cx) + dy * dy) <= radius) return true;
        }
        return false;
    }

    private static double Distance(double u, double v, double cx, double cy) => Math.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy));

    private static nint CreateIcon(uint[] pixels, int size)
    {
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = Win32.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        Marshal.Copy(Array.ConvertAll(pixels, p => unchecked((int)p)), 0, bits, pixels.Length);
        var mask = Win32.CreateBitmap(size, size, 1, 1, 0);
        var info = new Win32.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = Win32.CreateIconIndirect(ref info);
        Win32.DeleteObject(color);
        Win32.DeleteObject(mask);
        return icon;
    }
}
