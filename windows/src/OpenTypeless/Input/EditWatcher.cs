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
/// The field is read through UI Automation, locally; nothing leaves the PC. A watch ends when the user leaves
/// the field, sends or clears it, starts another dictation, or after two minutes. Only the state at that point
/// is compared, never a half-finished edit. UI-thread only; the UI Automation calls themselves run on the thread
/// pool with a timeout, since they go into the other app.
/// </summary>
public sealed class EditWatcher
{
    /// <summary>
    /// Called with the learnable corrections when a watch ends, and whether it ended quietly (a new dictation is
    /// starting, so nothing should be shown).
    /// </summary>
    public Action<List<Correction>, bool>? OnCorrections;

    private sealed class Session(string? app, IUIAutomationElement element, string inserted, string baseline)
    {
        public string? App { get; } = app;
        public IUIAutomationElement Element { get; } = element;
        public string Inserted { get; } = inserted;
        public string Baseline { get; } = baseline;
        public string Latest { get; set; } = baseline;
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(120);
    /// <summary>Big documents and terminal scrollback aren't worth re-reading twice a second.</summary>
    private const int MaxFieldLength = 50_000;
    /// <summary>A hung app must never pile up reads.</summary>
    private const int ReadTimeoutMs = 400;

    private readonly DispatcherQueueTimer _timer;
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
            if (field is not { } found)
            {
                Log("no focused field");
                return;
            }
            var (element, value, password) = found;
            if (password)
            {
                Log("password field");
                return;
            }
            if (value == null)
            {
                Log("field text unreadable");
                return;
            }
            if (value.Length > MaxFieldLength)
            {
                Log("field too long", new JsonObject { ["fieldLength"] = value.Length });
                return;
            }
            if (CorrectionLearner.EditedRegion(inserted, value, value) == null)
            {
                // Found once spacing is ignored: the app reformatted the text (line breaks, list markers…).
                static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
                Log("dictated text not found in field",
                    new JsonObject { ["fieldLength"] = value.Length, ["foundIgnoringSpaces"] = Squeeze(value).Contains(Squeeze(inserted)) });
                return;
            }
            Log("watching", new JsonObject { ["fieldLength"] = value.Length });
            _session = new Session(app, element, inserted, value);
            _timer.Start();
        }
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
        if (_session is not { } session) return;
        _session = null;
        var fields = new JsonObject { ["reason"] = reason, ["seconds"] = (int)(DateTimeOffset.Now - session.Started).TotalSeconds };
        if (session.Latest == session.Baseline
            || CorrectionLearner.EditedRegion(session.Inserted, session.Baseline, session.Latest) is not { } edited)
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
        _polling = true;
        try
        {
            var state = await Read<(bool Focused, string? Text)>(() =>
            {
                var focused = FocusedElement();
                return focused != null && SameElement(focused, session.Element)
                    ? (true, Text(session.Element, MaxFieldLength + 1))
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
