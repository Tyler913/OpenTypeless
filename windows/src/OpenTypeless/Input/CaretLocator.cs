using System.Runtime.InteropServices;
using OpenTypeless.Native;
using TypelessCore;

namespace OpenTypeless.Input;

/// <summary>
/// Where the line being typed into is on the screen, for a capsule placed near the text cursor (see
/// <see cref="HudPlacement"/>). The system caret, which Win32 edit boxes and many other apps keep, is asked first
/// (<c>GetGUIThreadInfo</c>: no call into the app); else UI Automation, on the thread pool within a short budget like
/// every other read of another app: the rectangle of the selection or the cursor, else the focused text field's own.
/// Only positions are read, never text, so a password field is no different from any other. Physical pixels (the app
/// is per-monitor DPI aware), y down.
/// </summary>
public static class CaretLocator
{
    private const int UIA_BoundingRectanglePropertyId = 30001;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsTextPatternAvailablePropertyId = 30040;
    private const int UIA_TextPatternId = 10014;
    private const int Edit = 50004, Document = 50030, ComboBox = 50003;
    private const int TextPatternRangeEndpoint_Start = 0, TextUnit_Character = 0;

    /// <summary>
    /// 1 while a read is running on the thread pool. A read a hung app holds up keeps its thread until the app answers
    /// (the budget only stops the waiting), so no other read starts meanwhile.
    /// </summary>
    private static int _outstanding;

    /// <summary>The caret <paramref name="window"/>'s thread shows, or null where it keeps none. No call into the app.</summary>
    public static HudRect? SystemCaret(nint window)
    {
        if (window == 0) return null;
        var thread = Win32.GetWindowThreadProcessId(window, out _);
        var gui = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(thread, ref gui) || gui.hwndCaret == 0) return null;
        var caret = gui.rcCaret;
        if (caret.Bottom <= caret.Top || caret.Right < caret.Left) return null;
        // In the client coordinates of its window, which an app that isn't per-monitor DPI aware counts at its own DPI:
        // Windows stretches it by the monitor's DPI over that.
        var origin = new Win32.POINT();
        if (!Win32.ClientToScreen(gui.hwndCaret, ref origin)) return null;
        var monitor = Win32.MonitorFromWindow(gui.hwndCaret, Win32.MONITOR_DEFAULTTONEAREST);
        var monitorDpi = Win32.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 96u;
        var windowDpi = Win32.GetDpiForWindow(gui.hwndCaret);
        var scale = windowDpi > 0 ? (double)monitorDpi / windowDpi : 1;
        var rect = new HudRect(origin.X + caret.Left * scale, origin.Y + caret.Top * scale,
                               (caret.Right - caret.Left) * scale, (caret.Bottom - caret.Top) * scale);
        // A caret outside its own window isn't where it says.
        if (!Win32.GetWindowRect(gui.hwndCaret, out var bounds)
            || !new HudRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top).Contains(new HudPoint(rect.MidX, rect.MidY)))
        {
            return null;
        }
        return rect;
    }

    /// <summary>
    /// The rectangle of the selection or the cursor in the focused element of <paramref name="window"/>, else of the
    /// focused text field, from UI Automation; null when neither is known within <paramref name="budget"/>, or another
    /// window has come to the front. Never throws.
    /// </summary>
    public static async Task<HudRect?> FromAutomation(nint window, TimeSpan budget)
    {
        if (window == 0 || Interlocked.CompareExchange(ref _outstanding, 1, 0) != 0) return null;
        var work = Task.Run(() =>
        {
            try
            {
                return Read(window);
            }
            finally
            {
                Volatile.Write(ref _outstanding, 0);
            }
        });
        if (await Task.WhenAny(work, Task.Delay(budget)).ConfigureAwait(false) != work) return null;
        return await work.ConfigureAwait(false);
    }

    /// <summary>Every COM object it gets is released before it returns, also when it's abandoned for taking too long.</summary>
    private static HudRect? Read(nint window)
    {
        var held = new List<object>();
        try
        {
            if (Win32.GetForegroundWindow() != window) return null;
            if (Hold(held, UIAutomation.FocusedElement()) is not { } element) return null;
            // The window in front changed while focus was asked for: this element may be another window's.
            if (Win32.GetForegroundWindow() != window) return null;
            if (SelectionBounds(element, held) is { } line) return line;
            Win32.GetWindowThreadProcessId(window, out var pid);
            return FieldBounds(element, FocusProbe.IsWebBased(window, pid));
        }
        catch (Exception)
        {
            // Whatever goes wrong reading another app, the capsule goes where it goes without the cursor.
            return null;
        }
        finally
        {
            foreach (var com in held)
            {
                try
                {
                    Marshal.ReleaseComObject(com);
                }
                catch (Exception)
                {
                    // Already gone with its app.
                }
            }
        }
    }

    /// <summary>Keeps <paramref name="com"/> to be released once the read is done.</summary>
    private static T? Hold<T>(List<object> held, T? com) where T : class
    {
        if (com != null && Marshal.IsComObject(com)) held.Add(com);
        return com;
    }

    /// <summary>
    /// The rectangle of the element's selection, or of its cursor. Many fields have none for an empty range: then the
    /// character after the cursor, or else the one before it (which may end the line above, so the line after it counts
    /// too).
    /// </summary>
    private static HudRect? SelectionBounds(UIAutomation.IUIAutomationElement element, List<object> held)
    {
        if (element.GetCurrentPropertyValue(UIA_IsTextPatternAvailablePropertyId, out var hasText) != 0 || hasText is not true
            || element.GetCurrentPattern(UIA_TextPatternId, out var pattern) != 0 || Hold(held, pattern) is not UIAutomation.IUIAutomationTextPattern textPattern
            || textPattern.GetSelection(out var ranges) != 0 || Hold(held, ranges) is not { } array
            || array.GetLength(out var count) != 0 || count < 1
            || array.GetElement(0, out var first) != 0 || Hold(held, first) is not { } selection)
        {
            return null;
        }
        if (Bounds(selection) is { } whole) return whole;
        if (selection.Clone(out var nextCopy) == 0 && Hold(held, nextCopy) is { } next
            && next.ExpandToEnclosingUnit(TextUnit_Character) == 0 && Bounds(next) is { } after)
        {
            return after;
        }
        if (selection.Clone(out var previousCopy) == 0 && Hold(held, previousCopy) is { } previous
            && previous.MoveEndpointByUnit(TextPatternRangeEndpoint_Start, TextUnit_Character, -1, out var moved) == 0 && moved == -1
            && Bounds(previous) is { } before)
        {
            return before with { Height = 2 * before.Height };
        }
        return null;
    }

    /// <summary>All of a range's line rectangles together; null when it has none.</summary>
    private static HudRect? Bounds(UIAutomation.IUIAutomationTextRange range)
    {
        if (range.GetBoundingRectangles(out var values) != 0 || values is not { Length: >= 4 }) return null;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (var i = 0; i + 3 < values.Length; i += 4)
        {
            if (!(values[i + 3] > 0)) continue;
            left = Math.Min(left, values[i]);
            top = Math.Min(top, values[i + 1]);
            right = Math.Max(right, values[i] + values[i + 2]);
            bottom = Math.Max(bottom, values[i + 1] + values[i + 3]);
        }
        return bottom > top && right >= left ? new HudRect(left, top, right - left, bottom - top) : null;
    }

    /// <summary>
    /// The focused element's own rectangle, if it's a field that takes text: below it is clear of the line being typed
    /// into. Not a browser's page, which is a Document too.
    /// </summary>
    private static HudRect? FieldBounds(UIAutomation.IUIAutomationElement element, bool web)
    {
        var type = element.GetCurrentPropertyValue(UIA_ControlTypePropertyId, out var value) == 0 && value is int i ? i : 0;
        if (type is not (Edit or ComboBox or Document) || (web && type == Document)) return null;
        return element.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId, out var rect) == 0
            && rect is double[] { Length: 4 } r && r[2] >= 0 && r[3] > 0
                ? new HudRect(r[0], r[1], r[2], r[3])
                : null;
    }
}
