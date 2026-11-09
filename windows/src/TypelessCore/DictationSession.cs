namespace TypelessCore;

/// <summary>
/// What a recording is for: plain dictation, a translation (see <see cref="TranslationTarget"/>), or an edit of the text
/// selected in the app in front (see <see cref="SelectionCommand"/>), which the translation and command chords (see
/// <see cref="ModeChords{TMode}"/>) switch it to while recording.
/// </summary>
public enum RecordingMode { Dictation, Translate, Command }

public static class RecordingModes
{
    public static readonly RecordingMode[] All = Enum.GetValues<RecordingMode>();

    /// <summary>Its name, the same as on macOS.</summary>
    public static string RawValue(this RecordingMode mode) => mode switch
    {
        RecordingMode.Translate => "translate",
        RecordingMode.Command => "command",
        _ => "dictation",
    };
}

/// <summary>Where a dictation stands (see <see cref="DictationSession"/>).</summary>
public enum SessionPhase { Idle, Recording, Processing }

public static class SessionPhases
{
    public static readonly SessionPhase[] All = Enum.GetValues<SessionPhase>();

    /// <summary>Its name, the same as on macOS.</summary>
    public static string RawValue(this SessionPhase phase) => phase switch
    {
        SessionPhase.Recording => "recording",
        SessionPhase.Processing => "processing",
        _ => "idle",
    };
}

/// <summary>
/// What the app is to do next about a dictation, as <see cref="DictationSession"/> decides it. The app carries each out
/// with the microphone, the capsule, History and the paste; the session only says which, and in what order.
/// </summary>
public abstract record SessionEffect
{
    /// <summary>
    /// Open the microphone and start a dictation; the app reports <see cref="DictationSession.Began"/> once it is
    /// recording (nothing, when it couldn't start).
    /// </summary>
    public sealed record StartRecording : SessionEffect;
    /// <summary>
    /// Stop recording and process what was said, or throw it away as an accidental tap
    /// (<see cref="DictationSession.IsAccidental"/>).
    /// </summary>
    public sealed record FinishRecording : SessionEffect;
    /// <summary>
    /// The running recording becomes this mode's, while that mode is still switched on; the app reports
    /// <see cref="DictationSession.Switched"/> when it did.
    /// </summary>
    public sealed record SwitchMode(RecordingMode Mode) : SessionEffect;
    /// <summary>
    /// Stop without inserting anything: a recording is thrown away, a dictation being processed keeps what was
    /// transcribed, as cancelled. Silently: without "Cancelled" in the capsule.
    /// </summary>
    public sealed record Cancel(bool Silently) : SessionEffect;
    /// <summary>Esc on a long recording: stop and insert nothing, but keep it (see <see cref="CancelPolicy"/>).</summary>
    public sealed record KeepCancelled : SessionEffect;
    /// <summary>The "Paste last dictation" that was waiting has been dropped, and does nothing more.</summary>
    public sealed record PasteLastDropped : SessionEffect;
    /// <summary>Carry out an action's shortcut.</summary>
    public sealed record Perform(ShortcutAction Action) : SessionEffect;

    /// <summary>The effect by name, the same on both platforms (<c>testdata/session-cases.json</c> and the session log).</summary>
    public string Name => this switch
    {
        StartRecording => "startRecording",
        FinishRecording => "finishRecording",
        SwitchMode switched => "switchMode:" + switched.Mode.RawValue(),
        Cancel cancelled => cancelled.Silently ? "cancel:silently" : "cancel",
        KeepCancelled => "keepCancelled",
        PasteLastDropped => "pasteLastDropped",
        Perform performed => "perform:" + performed.Action.RawValue(),
        _ => GetType().Name,
    };
}

/// <summary>
/// Where one dictation stands, and what each key, click and system event does to it: the decisions of the app's
/// <c>SessionController</c>, without the microphone, the capsule, History or the paste, so both apps make them the same
/// way and <c>testdata/session-cases.json</c> can check them.
///
/// The app tells it what happened (<see cref="HotkeyDown"/>, <see cref="Escape"/>, <see cref="Locked"/>…) and carries
/// out the effects it answers with, in order. What the app alone can know it reports as it happens: that the microphone
/// opened (<see cref="Began"/>), that a recording is being processed (<see cref="Processing"/>), that the dictation is
/// over (<see cref="Ended"/>). Nothing here waits, reads a clock or touches the system: every time and length comes in
/// with the call.
/// </summary>
public sealed class DictationSession
{
    private static readonly SessionEffect[] Nothing = Array.Empty<SessionEffect>();

    private static SessionEffect[] Only(SessionEffect effect) => new[] { effect };

    /// <summary>The phase has changed. Raised last, once everything that changes with it has.</summary>
    public event Action? PhaseChanged;

    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;
    /// <summary>
    /// Which dictation is current: bumped whenever one ends or is cancelled, so work still out for an earlier one (its
    /// paste, its Enter, its reset) can tell it's no longer wanted and leaves the next dictation alone.
    /// </summary>
    public int Generation { get; private set; }
    /// <summary>When the hotkey went down, by the key event's own time (see <see cref="HotkeyPress"/>).</summary>
    public double? PressedAt { get; private set; }
    public bool HandsFree { get; private set; }
    /// <summary>What the running recording is for; plain dictation at every start.</summary>
    public RecordingMode Mode { get; private set; } = RecordingMode.Dictation;
    /// <summary>
    /// The send key that ended this recording (see <see cref="TypelessCore.SendKey"/>): pressed in the app once the text
    /// is pasted there, for this dictation only. Off unless it was pressed.
    /// </summary>
    public SendKey SendKey { get; private set; } = SendKey.Off;
    /// <summary>Between the session locking (or sleep) and unlocking.</summary>
    public bool IsLocked { get; private set; }
    /// <summary>
    /// False when retrying from History, once the screen locked, or when the key was let go of in an app with other
    /// settings: the result goes to the clipboard instead.
    /// </summary>
    public bool DeliversByPaste { get; private set; } = true;
    /// <summary>
    /// Why <see cref="DeliversByPaste"/> is false, for whether the text is also kept in the Scratchpad (see
    /// <see cref="ScratchpadCapture"/>).
    /// </summary>
    public ScratchpadCapture.Outcome NoPasteReason { get; private set; } = ScratchpadCapture.Outcome.FromHistory;
    /// <summary>
    /// The "Paste last dictation" waiting for its keys to be let go, or pasting, by its number. A new dictation, Esc or
    /// the session locking drops it (null), and what it still had to do is left undone.
    /// </summary>
    public int? PendingPasteLast { get; private set; }
    private int _pasteLastCount;
    /// <summary>Second keys that switch the recording's mode while the hotkey is held (see <see cref="ModeChords{TMode}"/>).</summary>
    public ModeChords<RecordingMode> Chords { get; set; } = new();

    // MARK: - What happened

    /// <summary>
    /// The dictation hotkey went down. Between dictations it starts one, and whoever pressed it is at the keyboard: the
    /// session is unlocked, whatever notification was missed, and a "Paste last dictation" still waiting gives way. In a
    /// hands-free recording it ends it. Otherwise (held down already, or the last dictation still being processed)
    /// nothing.
    /// </summary>
    public IReadOnlyList<SessionEffect> HotkeyDown()
    {
        switch (Phase)
        {
            case SessionPhase.Idle:
                IsLocked = false;
                var effects = new List<SessionEffect>(DropPasteLast());
                effects.Add(new SessionEffect.StartRecording());
                return effects;
            case SessionPhase.Recording when HandsFree:
                return Only(new SessionEffect.FinishRecording());
            default:
                return Nothing;
        }
    }

    /// <summary>The dictation hotkey was let go of at <paramref name="at"/>: a tap makes the recording hands-free, a hold ends it.</summary>
    public IReadOnlyList<SessionEffect> HotkeyUp(double at)
    {
        if (Phase != SessionPhase.Recording || HandsFree || PressedAt is not { } pressedAt) return Nothing;
        if (HotkeyPress.IsTap(pressedAt, at))
        {
            HandsFree = true;
            return Nothing;
        }
        return Only(new SessionEffect.FinishRecording());
    }

    /// <summary>
    /// Another key went down at <paramref name="at"/> while recording: a registered chord switches the recording's mode
    /// (not to the one it has); otherwise, soon after the press, it makes a key combination with the hotkey, which isn't
    /// a dictation.
    /// </summary>
    public IReadOnlyList<SessionEffect> OtherKey(int keyCode, double at)
    {
        if (Phase != SessionPhase.Recording || PressedAt is not { } pressedAt) return Nothing;
        switch (Chords.OtherKey(keyCode, pressedAt, at, HandsFree))
        {
            case OtherKeyOutcome<RecordingMode>.SwitchMode chord when chord.Mode != Mode:
                return Only(new SessionEffect.SwitchMode(chord.Mode));
            case OtherKeyOutcome<RecordingMode>.Cancel:
                return Only(new SessionEffect.Cancel(true));
            default:
                return Nothing;
        }
    }

    /// <summary>
    /// The recording is to end as letting go of the key would end it: the capsule's ↑, the longest recording reached, or
    /// the microphone gone.
    /// </summary>
    public IReadOnlyList<SessionEffect> FinishRequested() =>
        Phase == SessionPhase.Recording ? Only(new SessionEffect.FinishRecording()) : Nothing;

    /// <summary>
    /// The send key went down while recording (<paramref name="key"/>: the one chosen in Settings): the recording ends,
    /// and that key is pressed in the app once the text is in.
    /// </summary>
    public IReadOnlyList<SessionEffect> SendKeyDown(SendKey key)
    {
        if (Phase != SessionPhase.Recording || key == SendKey.Off) return Nothing;
        SendKey = key;
        return Only(new SessionEffect.FinishRecording());
    }

    /// <summary>Esc, or the capsule's ✕.</summary>
    /// <param name="recordedSeconds">How long the recording is so far, the pre-roll aside.</param>
    public IReadOnlyList<SessionEffect> Escape(double recordedSeconds)
    {
        switch (Phase)
        {
            case SessionPhase.Idle:
                return DropPasteLast();
            case SessionPhase.Recording when CancelPolicy.Keeps(recordedSeconds):
                return Only(new SessionEffect.KeepCancelled());
            default:
                return Only(new SessionEffect.Cancel(false));
        }
    }

    /// <summary>
    /// The session locked, or the computer is going to sleep. A recording stops as with Esc (a long one is kept), so the
    /// microphone isn't left open on the lock screen; a dictation being processed goes to the clipboard instead of into
    /// whatever has focus afterwards; a "Paste last dictation" still waiting is dropped.
    /// </summary>
    public IReadOnlyList<SessionEffect> Locked(double recordedSeconds)
    {
        IsLocked = true;
        var effects = new List<SessionEffect>(DropPasteLast());
        switch (Phase)
        {
            case SessionPhase.Recording:
                effects.AddRange(Escape(recordedSeconds));
                break;
            case SessionPhase.Processing:
                DeliverToClipboard(ScratchpadCapture.Outcome.Locked);
                break;
        }
        return effects;
    }

    /// <summary>The session was unlocked. False when it wasn't locked, and nothing changes.</summary>
    public bool Unlocked()
    {
        if (!IsLocked) return false;
        IsLocked = false;
        return true;
    }

    /// <summary>An action's shortcut was pressed: only between dictations.</summary>
    public IReadOnlyList<SessionEffect> ActionPressed(ShortcutAction action) =>
        Phase == SessionPhase.Idle ? Only(new SessionEffect.Perform(action)) : Nothing;

    /// <summary>
    /// Command mode found the selection can't be edited, for the dictation that was <paramref name="generation"/>: while
    /// that one is still recording, in command mode, it stops as if it never started.
    /// </summary>
    public IReadOnlyList<SessionEffect> SelectionRefused(int generation) =>
        IsCurrent(generation) && Phase == SessionPhase.Recording && Mode == RecordingMode.Command
            ? Only(new SessionEffect.Cancel(true))
            : Nothing;

    // MARK: - What the app reports

    /// <summary>The microphone is open and the dictation is recording, its hotkey pressed at <paramref name="pressedAt"/>.</summary>
    public void Began(double pressedAt)
    {
        PressedAt = pressedAt;
        HandsFree = false;
        SendKey = SendKey.Off;
        Mode = RecordingMode.Dictation;
        DeliversByPaste = true;
        Enter(SessionPhase.Recording);
    }

    /// <summary>The running recording is <paramref name="mode"/>'s from now on.</summary>
    public void Switched(RecordingMode mode) => Mode = mode;

    /// <summary>The recording has ended and is being transcribed.</summary>
    public void Processing() => Enter(SessionPhase.Processing);

    /// <summary>A saved dictation is being run again, as <paramref name="mode"/>.</summary>
    /// <param name="paste">Whether its text goes to the cursor, or (from History) to the clipboard.</param>
    public void Retrying(bool paste, RecordingMode mode)
    {
        DeliversByPaste = paste;
        NoPasteReason = ScratchpadCapture.Outcome.FromHistory;
        Mode = mode;
        Enter(SessionPhase.Processing);
    }

    /// <summary>This dictation's text goes to the clipboard, not to the cursor.</summary>
    public void DeliverToClipboard(ScratchpadCapture.Outcome reason)
    {
        DeliversByPaste = false;
        NoPasteReason = reason;
    }

    /// <summary>The dictation is over: delivered, failed, cancelled or thrown away. Work still out for it isn't current any more.</summary>
    public void Ended()
    {
        Generation++;
        PressedAt = null;
        HandsFree = false;
        SendKey = SendKey.Off;
        Mode = RecordingMode.Dictation;
        Enter(SessionPhase.Idle);
    }

    /// <summary>Whether <paramref name="generation"/> is still the dictation under way (or, between dictations, the one to come).</summary>
    public bool IsCurrent(int generation) => generation == Generation;

    private void Enter(SessionPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        PhaseChanged?.Invoke();
    }

    // MARK: - An accidental tap

    /// <summary>A recording shorter than this was a slip of the finger: it's thrown away, with no entry in History.</summary>
    public const double AccidentalSeconds = 0.4;

    /// <param name="recordedSeconds">How long the recording is, the pre-roll aside.</param>
    public static bool IsAccidental(double recordedSeconds) => recordedSeconds < AccidentalSeconds;

    // MARK: - Paste last dictation

    /// <summary>
    /// A "Paste last dictation" was asked for: its number, or null while a dictation is under way or another one is
    /// still waiting.
    /// </summary>
    public int? BeginPasteLast()
    {
        if (Phase != SessionPhase.Idle || PendingPasteLast != null) return null;
        PendingPasteLast = ++_pasteLastCount;
        return PendingPasteLast;
    }

    /// <summary>The "Paste last dictation" <paramref name="id"/> has done what it was going to.</summary>
    public void EndPasteLast(int id)
    {
        if (PendingPasteLast == id) PendingPasteLast = null;
    }

    /// <summary>
    /// Whether the "Paste last dictation" <paramref name="id"/> is still wanted: not dropped, no dictation under way, the
    /// session unlocked.
    /// </summary>
    public bool PasteLastIsCurrent(int id) => PendingPasteLast == id && Phase == SessionPhase.Idle && !IsLocked;

    private SessionEffect[] DropPasteLast()
    {
        if (PendingPasteLast == null) return Nothing;
        PendingPasteLast = null;
        return Only(new SessionEffect.PasteLastDropped());
    }
}
