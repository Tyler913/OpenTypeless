using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using OpenTypeless.Native;
using OpenTypeless.Services;
using TypelessCore;
using static OpenTypeless.Native.UIAutomation;

namespace OpenTypeless.Input;

/// <summary>
/// After a dictation is pasted, keeps an eye on that text field to see whether the user fixes any word in it,
/// so the fix can be learned (see CorrectionLearner).
///
/// The field is read through UI Automation, locally; nothing leaves the PC. Where it can't be read, the keys pressed
/// after the paste are followed instead (see TypedEdit). A watch ends when the user leaves the field, sends or clears
/// it, clicks, switches app, starts another dictation, or after two minutes. Only the state at that point is compared,
/// never a half-finished edit. UI-thread only; the UI Automation calls themselves run on the thread pool with a
/// timeout, since they go into the other app.
/// </summary>
public sealed class EditWatcher
{
    /// <summary>
    /// Called with the learnable corrections when a watch ends, and whether it ended quietly (a new dictation is
    /// starting, so nothing should be shown).
    /// </summary>
    public Action<List<Correction>, bool>? OnCorrections;

    private sealed class Session(string? app, string inserted, IUIAutomationElement? element, string baseline, TypedEdit? keys)
    {
        public string? App { get; } = app;
        /// <summary>The field being read; null while following the keys.</summary>
        public IUIAutomationElement? Element { get; } = element;
        public string Inserted { get; } = inserted;
        public string Baseline { get; } = baseline;
        public string Latest { get; set; } = baseline;
        /// <summary>Following the keys, when the field can't be read.</summary>
        public TypedEdit? Keys { get; } = keys;
        public nint Window { get; } = Win32.GetForegroundWindow();
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(120);
    /// <summary>Big documents and terminal scrollback aren't worth re-reading twice a second.</summary>
    private const int MaxFieldLength = 50_000;
    /// <summary>A hung app must never pile up reads.</summary>
    private const int ReadTimeoutMs = 400;

    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private Session? _session;
    private int _generation;
    private bool _polling;

    public EditWatcher()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = PollInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Starts watching the focused field, which should now contain <paramref name="inserted"/>.</summary>
    public void Watch(string inserted)
    {
        Finish(quiet: true, "next paste");
        var generation = _generation;
        var app = ForegroundApp();
        _ = Start();

        void Log(string result, JsonObject? fields = null)
        {
            fields ??= new JsonObject();
            fields["result"] = result;
            fields["insertedLength"] = inserted.Length;
            LearningLog.Write("start", app, fields);
        }

        async Task Start()
        {
            // Give the target app a moment to apply the paste before taking the baseline.
            await Task.Delay(400);
            if (generation != _generation) return;
            var field = await Read<(IUIAutomationElement Element, string? Text, bool Password)>(() =>
            {
                if (FocusedElement() is not { } element) return null;
                if (IsPassword(element)) return (element, null, true);
                return (element, Text(element, MaxFieldLength + 1), false);
            });
            if (generation != _generation) return;
            var details = new JsonObject();
            string unreadable;
            if (field is not { } found)
            {
                unreadable = "no focused field";
            }
            else if (found.Password)
            {
                Log("password field");
                return;
            }
            else if (found.Text is not { } value)
            {
                unreadable = "field text unreadable";
            }
            else if (value.Length > MaxFieldLength)
            {
                Log("field too long", new JsonObject { ["fieldLength"] = value.Length });
                return;
            }
            else if (CorrectionLearner.EditedRegion(inserted, value, value) != null)
            {
                Log("watching", new JsonObject { ["fieldLength"] = value.Length });
                _session = new Session(app, inserted, found.Element, value, null);
                _timer.Start();
                return;
            }
            else
            {
                // Found once spacing is ignored: the app reformatted the text (line breaks, list markers…).
                static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
                details["fieldLength"] = value.Length;
                details["foundIgnoringSpaces"] = Squeeze(value).Contains(Squeeze(inserted));
                unreadable = "dictated text not found in field";
            }
            // The field can't be compared: follow the keys instead.
            details["field"] = unreadable;
            Log("following keys", details);
            _session = new Session(app, inserted, null, inserted, new TypedEdit(inserted));
            var session = _session;
            HotkeyMonitor.Shared.KeyObserver = key => _dispatcher.TryEnqueue(() =>
            {
                if (_session == session) OnKey(session, key);
            });
            HotkeyMonitor.Shared.WatchClicks(() =>
            {
                if (_session == session) Finish(quiet: false, "clicked");
            });
            _timer.Start();
        }
    }

    private void OnKey(Session session, TypedKey key)
    {
        const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_DELETE = 0x2E;
        var keys = session.Keys!;
        // AltGr is Ctrl+Alt: a key that types something with both held is text.
        var altGr = key.Ctrl && key.Alt && key.Text != null;
        var shortcut = (key.Ctrl || key.Alt || key.Win) && !altGr;
        var unit = key.Ctrl ? TypedEdit.Unit.Word : TypedEdit.Unit.Character;
        TypedEdit.Key edit;
        switch (key.VirtualKey)
        {
            case >= 0x10 and <= 0x12 or 0x14 or >= 0xA0 and <= 0xA5 or Hotkey.VK_LWIN or Hotkey.VK_RWIN:
                return; // modifiers on their own
            case VK_ESCAPE:
                return;
            case VK_RETURN:
                Finish(quiet: false, "return");
                return;
            case VK_TAB:
                Finish(quiet: false, "tab");
                return;
            case VK_BACK when !key.Alt && !key.Win:
                edit = new TypedEdit.Key.DeleteBackward(unit);
                break;
            case VK_DELETE when !shortcut:
                edit = new TypedEdit.Key.DeleteForward(unit);
                break;
            // Ctrl+Right lands at the start of the next word, unlike the macOS word jump TypedEdit follows.
            case VK_LEFT when !key.Alt && !key.Win:
                edit = new TypedEdit.Key.Left(unit, key.Shift);
                break;
            case VK_RIGHT when !shortcut:
                edit = new TypedEdit.Key.Right(unit, key.Shift);
                break;
            case 0x43 when key.Ctrl && !key.Alt: // Ctrl+C
                return;
            default:
                if (shortcut || key.IsInjected || key.Text is not { } text || text.Any(char.IsControl))
                {
                    Finish(quiet: false, "key not followed");
                    return;
                }
                if (key.InputMethod)
                {
                    Finish(quiet: false, "input method");
                    return;
                }
                edit = new TypedEdit.Key.Insert(text);
                break;
        }
        if (!keys.Apply(edit)) Finish(quiet: false, "cursor left the dictated text");
    }

    /// <summary>The process name of the foreground window, for the learning log.</summary>
    private static string? ForegroundApp()
    {
        try
        {
            Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Ends the current watch now and reports what was learned. <paramref name="reason"/> is for the learning log.</summary>
    public void Finish(bool quiet, string reason)
    {
        _generation++;
        _timer.Stop();
        HotkeyMonitor.Shared.KeyObserver = null;
        HotkeyMonitor.Shared.WatchClicks(null);
        if (_session is not { } session) return;
        _session = null;
        var fields = new JsonObject
        {
            ["reason"] = reason,
            ["seconds"] = (int)(DateTimeOffset.Now - session.Started).TotalSeconds,
            ["mode"] = session.Keys != null ? "keys" : "field",
        };
        var edited = session.Keys is { } keys
            ? keys.Text != session.Inserted ? keys.Text : null
            : session.Latest != session.Baseline ? CorrectionLearner.EditedRegion(session.Inserted, session.Baseline, session.Latest) : null;
        if (edited == null)
        {
            fields["edited"] = false;
            LearningLog.Write("end", session.App, fields);
            return;
        }
        fields["edited"] = true;
        var review = CorrectionLearner.ReviewEdit(session.Inserted, edited, SpellChecker.IsCommonWord, ChineseWords.Split);
        LearningLog.AddReview(fields, review);
        LearningLog.Write("end", session.App, fields);
        var corrections = review.Learnable;
        if (corrections.Count > 0) OnCorrections?.Invoke(corrections, quiet);
    }

    private async Task Poll()
    {
        if (_polling || _session is not { } session) return;
        if (session.Keys != null)
        {
            if (Win32.GetForegroundWindow() != session.Window) Finish(quiet: false, "switched app");
            else if (DateTimeOffset.Now - session.Started > MaxDuration) Finish(quiet: false, "2 minutes");
            return;
        }
        _polling = true;
        try
        {
            var state = await Read<(bool Focused, string? Text)>(() =>
            {
                var focused = FocusedElement();
                return focused != null && SameElement(focused, session.Element!)
                    ? (true, Text(session.Element!, MaxFieldLength + 1))
                    : (false, null);
            });
            if (_session != session) return;
            if (state is { Focused: false })
            {
                Finish(quiet: false, "focus left the field");
                return;
            }
            if (state is { Text: { } value } && value != session.Latest)
            {
                // Sent, cleared or rewritten beyond the dictated text: judge the last state that still made sense.
                if (value.Length > MaxFieldLength || CorrectionLearner.EditedRegion(session.Inserted, session.Baseline, value) == null)
                {
                    Finish(quiet: false, "sent, cleared or edited outside the dictated text");
                    return;
                }
                session.Latest = value;
            }
            if (DateTimeOffset.Now - session.Started > MaxDuration) Finish(quiet: false, "2 minutes");
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Runs a UI Automation read on the thread pool; null when it failed or the other app didn't answer in time.</summary>
    private static async Task<T?> Read<T>(Func<T?> read) where T : struct
    {
        var task = Task.Run(() =>
        {
            try
            {
                return read();
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                return null;
            }
        });
        return await Task.WhenAny(task, Task.Delay(ReadTimeoutMs)) == task ? task.Result : null;
    }
}
