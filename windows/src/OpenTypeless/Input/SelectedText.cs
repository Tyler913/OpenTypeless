using System.Runtime.InteropServices;
using OpenTypeless.Native;
using TypelessCore;

namespace OpenTypeless.Input;

/// <summary>
/// Reads the text selected in the app in front, for editing it by voice (see <see cref="SelectionReader"/> for the order):
/// from UI Automation (<c>TextPattern.GetSelection</c>), which presses nothing; else with Ctrl+C once the hotkey and the
/// chord are let go of, the clipboard put back as it was (see <see cref="TextInserter.CopySelection"/>). The macOS app
/// does the same with Accessibility and ⌘C, and presses the app's Edit ▸ Copy in between, which Windows apps have no
/// common counterpart of. UI Automation calls go into the other app's process, so they run off the UI thread, each with
/// a deadline; the rest is UI thread only.
/// </summary>
internal static class SelectedText
{
    /// <summary>How long a UI Automation call into the other app may take before it counts as unanswered.</summary>
    private const int ReadTimeoutMs = 500;

    /// <summary>
    /// The longest text read from a range: well past <see cref="SelectionCommand.MaxCharacters"/>, so a selection over it
    /// is refused with its real length, and not so long that a whole book comes over.
    /// </summary>
    private const int MaxReadLength = 120_000;

    /// <summary>
    /// Where a selection was read: the foreground window, its process, and the element with keyboard focus (null where UI
    /// Automation doesn't say). The edit goes back only while they're still the same (see <see cref="IsInFront"/>).
    /// </summary>
    internal sealed class Place(nint window, uint processId, UIAutomation.IUIAutomationElement? element)
    {
        public nint Window { get; } = window;
        public uint ProcessId { get; } = processId;
        public UIAutomation.IUIAutomationElement? Element { get; } = element;

        /// <summary>The foreground window now, unless it's this app's: its own windows (Settings, say) aren't edited.</summary>
        public static async Task<Place?> Front()
        {
            var window = Win32.GetForegroundWindow();
            if (window == 0) return null;
            Win32.GetWindowThreadProcessId(window, out var pid);
            if (pid == (uint)Environment.ProcessId) return null;
            var element = await Off<UIAutomation.IUIAutomationElement?>(UIAutomation.FocusedElement, null);
            return new Place(window, pid, element);
        }

        /// <summary>
        /// Whether the same window is still in front, with the same element focused (where that was known then and can be
        /// told now; when UI Automation doesn't answer in time, it isn't).
        /// </summary>
        public async Task<bool> IsInFront()
        {
            if (Win32.GetForegroundWindow() != Window) return false;
            if (Element is not { } element) return true;
            if (await Off<UIAutomation.IUIAutomationElement?>(UIAutomation.FocusedElement, null) is not { } now) return true;
            return await Off(() => UIAutomation.SameElement(element, now), false) && Win32.GetForegroundWindow() == Window;
        }
    }

    /// <summary>
    /// The selection at <paramref name="place"/>. <paramref name="hotkey"/> and <paramref name="chordKeys"/>: the keys let
    /// go of before Ctrl+C is sent, since a Ctrl+C read together with them would be another shortcut; it waits for up to
    /// <paramref name="releaseTimeout"/> (as long as the recording may last), and gives up once
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task<SelectionRead> Read(Place place, Hotkey hotkey, IReadOnlyCollection<int> chordKeys, TimeSpan releaseTimeout,
                                                 CancellationToken cancellationToken)
    {
        var app = SelectionApps.Windows(Win32.ProcessImagePath(place.ProcessId), FocusProbe.IsTerminal(place.Window, place.ProcessId));
        var element = place.Element;
        var secure = element is { } field && await Off(() => UIAutomation.IsPassword(field), false);
        Func<Task<SelectionProbe>> accessibility = element is { } focused
            ? () => Off<SelectionProbe>(() => UIAutomation.SelectedText(focused, MaxReadLength), new SelectionProbe.Unavailable())
            : () => Task.FromResult<SelectionProbe>(new SelectionProbe.Unavailable());
        return await SelectionReader.Read(app, secure, accessibility,
            menuCopy: null,
            copyShortcut: async () =>
            {
                if (!await HotkeyMonitor.Shared.WaitForRelease(hotkey, releaseTimeout, chordKeys, cancellationToken)
                    || cancellationToken.IsCancellationRequested || !await place.IsInFront())
                {
                    return new SelectionProbe.Unavailable();
                }
                var copied = await TextInserter.CopySelection();
                return copied == null ? new SelectionProbe.Unavailable() : (SelectionProbe)new SelectionProbe.Text(copied);
            });
    }

    /// <summary>
    /// The selection in the element focused now, as UI Automation reports it, to check it's still the one read; null when
    /// it can't tell.
    /// </summary>
    public static async Task<string?> SelectionNow()
    {
        if (await Off<UIAutomation.IUIAutomationElement?>(UIAutomation.FocusedElement, null) is not { } element) return null;
        var probe = await Off<SelectionProbe>(() => UIAutomation.SelectedText(element, MaxReadLength), new SelectionProbe.Unavailable());
        return probe switch
        {
            SelectionProbe.Text text => text.Value,
            SelectionProbe.Empty => "",
            _ => null,
        };
    }

    /// <summary>Runs a UI Automation call on the thread pool; <paramref name="unanswered"/> when it failed or the other app didn't answer in time.</summary>
    private static async Task<T> Off<T>(Func<T> read, T unanswered)
    {
        var task = Task.Run(() =>
        {
            try
            {
                return read();
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                return unanswered;
            }
        });
        return await Task.WhenAny(task, Task.Delay(ReadTimeoutMs)) == task ? task.Result : unanswered;
    }
}
