using System.Runtime.InteropServices;
using OpenTypeless.Native;
using TypelessCore;

namespace OpenTypeless.Input;

/// <summary>
/// Reads the text around the cursor in the field being dictated into, for the clean-up to spell names by (see
/// <see cref="CursorContext"/>). Through UI Automation, like the edit watcher, but once, as recording starts (the dictated
/// text only goes in at the end, so the field is still the speaker's): each read runs on the thread pool with a short
/// budget, since it goes into the other app, and whatever fails or takes long just means no context. Password fields are
/// never read, and only text fields and editable content are, never a browser's page or a list.
/// </summary>
public static class CursorContextReader
{
    /// <summary>A hung app must never hold anything up.</summary>
    private const int ReadTimeoutMs = 500;
    /// <summary>
    /// A browser or Electron app whose focused element isn't a text field yet (Chromium builds its UI Automation tree a
    /// while after first being asked) is asked again this many times, this far apart: about a second in all.
    /// </summary>
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>Where the field doesn't say where its cursor is, its text is read whole, up to this much.</summary>
    private const int MaxFieldLength = 100_000;

    /// <summary>The excerpt around the cursor in the foreground window's focused field, or null.</summary>
    public static async Task<string?> Read()
    {
        var window = Win32.GetForegroundWindow();
        if (window == 0) return null;
        Win32.GetWindowThreadProcessId(window, out var pid);
        var web = FocusProbe.IsWebBased(window, pid);
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (attempt > 1) await Task.Delay(RetryDelay);
            // Focus moved on meanwhile: whatever has it now isn't where the text goes.
            if (Win32.GetForegroundWindow() != window) return null;
            var read = Task.Run(() => ReadFocused(web));
            if (await Task.WhenAny(read, Task.Delay(ReadTimeoutMs)) != read) return null;
            var (found, text) = read.Result;
            if (found || !web) return text;
        }
        return null;
    }

    /// <summary>Whether the focused element is a text field (or a password field), and if so the excerpt around its cursor.</summary>
    private static (bool Found, string? Text) ReadFocused(bool web)
    {
        try
        {
            if (UIAutomation.FocusedElement() is not { } element) return (false, null);
            try
            {
                if (UIAutomation.IsPassword(element)) return (true, null);
                if (!UIAutomation.IsTextInput(element, web)) return (false, null);
                if (UIAutomation.TextAroundSelection(element, 2 * CursorContext.Limit) is { } around)
                {
                    return (true, CursorContext.Excerpt(around.Before, around.Selected, around.After));
                }
                // No Text pattern, or it doesn't say where the selection is: the whole value, with the cursor at its end.
                return (true, UIAutomation.Text(element, MaxFieldLength) is { } value ? CursorContext.Excerpt(value, null) : null);
            }
            finally
            {
                Marshal.ReleaseComObject(element);
            }
        }
        catch (Exception)
        {
            // Whatever goes wrong reading another app's field, the dictation goes on without the context.
            return (true, null);
        }
    }
}
