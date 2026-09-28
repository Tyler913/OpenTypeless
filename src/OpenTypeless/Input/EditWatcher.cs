using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using OpenTypeless.Native;
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

    private sealed class Session(IUIAutomationElement element, string inserted, string baseline)
    {
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
        Finish(quiet: true);
        var generation = _generation;
        _ = Start();

        async Task Start()
        {
            // Give the target app a moment to apply the paste before taking the baseline.
            await Task.Delay(400);
            if (generation != _generation) return;
            var field = await Read(() =>
            {
                if (FocusedElement() is not { } element || IsPassword(element)) return null;
                return Text(element, MaxFieldLength + 1) is { } text ? (element, text) : ((IUIAutomationElement, string)?)null;
            });
            if (generation != _generation || field is not (IUIAutomationElement element, string value)) return;
            if (value.Length > MaxFieldLength || CorrectionLearner.EditedRegion(inserted, value, value) == null) return;
            _session = new Session(element, inserted, value);
            _timer.Start();
        }
    }

    /// <summary>Ends the current watch now and reports what was learned.</summary>
    public void Finish(bool quiet)
    {
        _generation++;
        _timer.Stop();
        if (_session is not { } session) return;
        _session = null;
        if (session.Latest == session.Baseline
            || CorrectionLearner.EditedRegion(session.Inserted, session.Baseline, session.Latest) is not { } edited) return;
        var corrections = CorrectionLearner.Corrections(session.Inserted, edited, SpellChecker.IsCommonWord);
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
                Finish(quiet: false);
                return;
            }
            if (state is { Text: { } value } && value != session.Latest)
            {
                // Sent, cleared or rewritten beyond the dictated text: judge the last state that still made sense.
                if (value.Length > MaxFieldLength || CorrectionLearner.EditedRegion(session.Inserted, session.Baseline, value) == null)
                {
                    Finish(quiet: false);
                    return;
                }
                session.Latest = value;
            }
            if (DateTimeOffset.Now - session.Started > MaxDuration) Finish(quiet: false);
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
