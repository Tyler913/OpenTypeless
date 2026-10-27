using System.Diagnostics;
using Microsoft.UI.Dispatching;
using OpenTypeless.Audio;
using OpenTypeless.Input;
using OpenTypeless.Native;
using OpenTypeless.Services;
using OpenTypeless.UI;
using TypelessCore;

namespace OpenTypeless.Session;

/// <summary>Thread-safe running sample count (written on the capture thread, read on the UI thread).</summary>
internal sealed class SampleCounter
{
    private long _count;
    public void Add(int n) => Interlocked.Add(ref _count, n);
    public double Seconds => AudioFormat.Seconds((int)Interlocked.Read(ref _count));
}

/// <summary>
/// What a recording is for: plain dictation, a translation (see <see cref="TranslationTarget"/>), or an edit of the text
/// selected in the app in front (see <see cref="SelectionCommand"/>), which the translation and command chords (see
/// <see cref="ModeChords{TMode}"/>) switch it to while recording.
/// </summary>
public enum RecordingMode { Dictation, Translate, Command }

internal static class TranslationFallbackText
{
    /// <summary>Why a translation couldn't be used, for History.</summary>
    public static string TranslationReason(this PolishFallback fallback) => fallback switch
    {
        PolishFallback.Truncated => L("翻译被截断了", "the translation was cut off"),
        PolishFallback.Answered => L("模型回答了内容，没有翻译", "the model answered instead of translating"),
        PolishFallback.Summarised => L("翻译漏掉了太多内容", "the translation left too much out"),
        PolishFallback.Untranslated => L("结果不是目标语言", "it didn't come back in the target language"),
        _ => L("没有可用的翻译", "no usable translation came back"),
    };
}

internal static class CommandText
{
    /// <summary>Why an edit of a selection couldn't be used, for History and the capsule.</summary>
    public static string CommandReason(this PolishFallback fallback) => fallback switch
    {
        PolishFallback.Truncated => L("改写被截断了", "the rewrite was cut off"),
        PolishFallback.Answered => L("模型回复了别的内容，没有改写", "the model replied instead of editing"),
        _ => L("没有可用的改写", "no usable rewrite came back"),
    };

    /// <summary>What the capsule says when the selection can't be edited by voice.</summary>
    public static string Message(this SelectionRefusal refusal) => refusal switch
    {
        SelectionRefusal.Terminal => L("终端里的文字不能用语音编辑", "Text in a terminal can't be edited by voice"),
        SelectionRefusal.SecureField => L("密码框不能用语音编辑", "Password fields can't be edited by voice"),
        SelectionRefusal.UnreadableInEditor => L("读不到这个编辑器里选中的文字", "Can't read the selection in this editor"),
        SelectionRefusal.TooLong tooLong => L($"选中的文字太长（{tooLong.Count} 字符，最多 {SelectionCommand.MaxCharacters}）",
                                              $"The selection is too long ({tooLong.Count} characters; up to {SelectionCommand.MaxCharacters})"),
        _ => L("没有选中文字：先选中要修改的文字，再按住听写快捷键说出怎么改",
               "No text selected — select the text to change, then hold the dictation shortcut and say how"),
    };

    /// <summary>Why an edit went to the clipboard instead of over the selection.</summary>
    public static string Message(this SelectionCommand.ClipboardReason reason) => reason switch
    {
        SelectionCommand.ClipboardReason.Locked => L("屏幕已锁定，改写后的文字已复制", "Screen locked — the edited text is on the clipboard"),
        SelectionCommand.ClipboardReason.MovedAway => L("已切换到别的窗口，改写后的文字已复制",
                                                        "You moved to another window — the edited text is on the clipboard"),
        SelectionCommand.ClipboardReason.SelectionChanged => L("选中的文字变了，改写后的文字已复制",
                                                               "The selection changed — the edited text is on the clipboard"),
        _ => L("这里不能粘贴，改写后的文字已复制", "Can't paste there — the edited text is on the clipboard"),
    };
}

/// <summary>Orchestrates one dictation: hotkey → record → chunked STT → polish → paste. UI-thread only.</summary>
public sealed class SessionController
{
    public enum SessionState { Idle, Recording, Processing }

    public event Action? StateChanged;
    /// <summary>Asks the app to open the settings window (on a page, or null for the current one).</summary>
    public event Action<string?>? OpenSettingsRequested;

    private SessionState _state = SessionState.Idle;
    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke();
        }
    }

    public string? LastError { get; private set; }

    public HudController Hud { get; } = new();
    private readonly EditWatcher _editWatcher = new();
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly AudioRecorder _recorder = new();
    /// <summary>Receives every microphone sample; hands them to the running dictation, or keeps a pre-roll in between.</summary>
    private readonly AudioSink _sink = new();
    /// <summary>The input the running recorder was opened on (it may keep running between dictations when warm).</summary>
    private string? _openDeviceId;
    /// <summary>Audio from before the key was pressed at the start of this dictation (only with a warm microphone).</summary>
    private double _preRollSeconds;
    /// <summary>Windows' own recognition shown above the capsule while recording, when Live preview is on.</summary>
    private LivePreview? _preview;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private TranscriptionPipeline? _pipeline;
    private WavFileWriter? _writer;
    private DictationRecord? _record;
    private SampleCounter _counter = new();
    /// <summary>When the hotkey went down, by the key event's own time (seconds on the tick clock, see <see cref="HotkeyPress"/>).</summary>
    private double? _pressedAt;
    private bool _handsFree;
    /// <summary>
    /// Second keys that switch the recording's mode while the hotkey is held (see <see cref="ModeChords{TMode}"/>): the
    /// translation chord's, while translation mode is on (see <see cref="UpdateModeChords"/>, which also gives the hotkey
    /// hook the keys to keep from the focused app).
    /// </summary>
    public ModeChords<RecordingMode> ModeChords { get; private set; } = new();
    /// <summary>What the running recording is for; plain dictation at every start.</summary>
    public RecordingMode Mode { get; private set; } = RecordingMode.Dictation;
    /// <summary>
    /// Command mode: the selection being read (or read) when the chord was pressed, where, and how many clicks and keys
    /// had reached apps by then (see <see cref="ReadSelection"/>). Dropped with the recording, or when another chord
    /// switches it to another mode.
    /// </summary>
    private Task<(SelectedText.Place? Place, SelectionRead Read, long Mark)>? _selection;
    private CancellationTokenSource? _selectionCancel;
    /// <summary>
    /// The "Paste last dictation" waiting for its keys to be let go, or pasting, by its number. A new dictation, Esc or the
    /// session locking drops it (null), and what it still had to do is left undone. Pressing it again meanwhile does nothing.
    /// </summary>
    private int? _pendingPasteLast;
    private int _pasteLastCount;
    /// <summary>
    /// Which dictation is current: bumped whenever one ends or is cancelled, so work still out for an earlier one (its
    /// paste, its Enter, its reset) can tell it's no longer wanted and leaves the next dictation alone.
    /// </summary>
    private int _generation;
    /// <summary>
    /// The foreground window when the recording stopped, where the text is meant to go (for a retry, the window "Paste last
    /// dictation" was asked for in, or 0).
    /// </summary>
    private nint _pasteTarget;
    /// <summary>Between the session locking (or sleep) and unlocking: no warm microphone meanwhile.</summary>
    private bool _isLocked;
    /// <summary>
    /// Cancelled recordings still being transcribed in the background (see <see cref="KeepCancelledRecording"/>). A retry
    /// from History takes the record over, and the background result is then dropped rather than written over the retry's.
    /// </summary>
    private readonly HashSet<string> _keptInBackground = new();
    private CancellationTokenSource? _processing;
    private DispatcherQueueTimer? _maxDurationTimer;
    /// <summary>Keeps the connections warm while recording (see <see cref="WarmConnections"/>).</summary>
    private DispatcherQueueTimer? _warmTimer;
    private readonly Preconnector _preconnector = new();
    /// <summary>Silences the speakers while recording, when that's switched on.</summary>
    private readonly OutputMute _outputMute = new();
    /// <summary>A clean-up started before the key was released, on the transcript a speculative tail gave.</summary>
    private SpeculativePolish? _speculativePolish;
    /// <summary>Clean-ups started ahead of time that finished but were then dropped because the speaker went on: they were billed too.</summary>
    private readonly List<SegmentedPolishResult> _discardedPolishes = new();
    private Dictionary<string, ModelInfo> _modelInfo = new();
    /// <summary>False when retrying from the history page, or once the screen locked: the result goes to the clipboard instead.</summary>
    private bool _deliverByPaste = true;

    public SessionController()
    {
        _editWatcher.OnCorrections = (corrections, quiet) =>
        {
            if (!_settings.LearnFromEdits) return;
            var added = _settings.Learn(corrections);
            Note($"learned from edits: {string.Join(", ", corrections.Select(c => $"{c.Heard} → {c.Corrected}"))}, new: {string.Join(", ", added)}");
            if (added.Count > 0 && !quiet && State == SessionState.Idle)
            {
                Hud.Show(new HudPhase.Learned(string.Join(L("、", ", "), added)), 2.5);
            }
        };
        _recorder.OnFailure = error => _dispatcher.TryEnqueue(() =>
        {
            // The recorder has stopped: a warm microphone is opened again once idle (or by the next dictation).
            _openDeviceId = null;
            Abort(L("录音中断：", "Recording interrupted: ") + error.Message);
        });
        // Set once: while the microphone is kept warm the recorder runs between dictations too.
        _recorder.OnSamples = _sink.Deliver;
        var hudModel = Hud.Model;
        _recorder.OnLevel = level =>
        {
            if (_sink.IsRecording) _dispatcher.TryEnqueue(() => hudModel.Push(level));
        };
    }

    private string? ChosenDeviceId => _settings.MicrophoneId.Length == 0 ? null : _settings.MicrophoneId;

    /// <summary>
    /// The running dictation is still capturing: recording, or in the moment after the key is released while the last
    /// of the audio comes in (see <see cref="FinishRecording"/>).
    /// </summary>
    private bool IsCapturing => _writer != null;

    /// <summary>
    /// With "Keep microphone ready" on, keeps the recorder running between dictations (on the chosen input), so a
    /// dictation starts at once and includes the moment before the key; otherwise lets it stop. Called at launch,
    /// when settings change and after each dictation.
    /// </summary>
    public void UpdateWarmMicrophone()
    {
        if (State != SessionState.Idle) return;
        if (_settings.KeepMicrophoneWarm && Permissions.MicrophoneGranted && !_isLocked)
        {
            if (_recorder.IsRecording && _openDeviceId == ChosenDeviceId) return;
            _recorder.Stop();
            _recorder.DeviceId = ChosenDeviceId;
            try
            {
                _recorder.Start();
                _openDeviceId = ChosenDeviceId;
                Note("microphone kept warm");
            }
            catch (Exception error)
            {
                Note("couldn't keep the microphone warm: " + error.Message);
            }
        }
        else if (_recorder.IsRecording)
        {
            _recorder.Stop();
            _openDeviceId = null;
            // Nor is what it heard last kept for the next dictation (from before the session locked, say).
            _sink.End();
        }
    }

    // MARK: - Hotkey events

    private static void Note(string text) => AppLog.Debug("session", text);

    /// <param name="at">When the key went down (the event's own time, see <see cref="HotkeyPress"/>).</param>
    public void HotkeyPressed(double at)
    {
        switch (State)
        {
            case SessionState.Idle:
                Note("start recording");
                StartRecording(at);
                break;
            case SessionState.Recording when _handsFree:
                Note("stop (hands-free)");
                FinishRecording();
                break;
            case SessionState.Recording:
                Note("press ignored — already recording");
                break;
            default:
                Note("press ignored — still processing the previous dictation");
                break;
        }
    }

    public void HotkeyReleased(double at)
    {
        if (State != SessionState.Recording || _handsFree || _pressedAt is not { } pressedAt) return;
        var held = at - pressedAt;
        var tap = HotkeyPress.IsTap(pressedAt, at);
        Note(tap ? $"tap ({held:0.00}s) → hands-free" : $"released after {held:0.00}s → stop");
        if (tap) _handsFree = true;
        else FinishRecording();
    }

    /// <summary>
    /// Another key went down while the hotkey was held: a registered chord switches the recording's mode; otherwise
    /// Right Ctrl + C and similar combos shouldn't start a dictation.
    /// </summary>
    public void OtherKeyPressed(int keyCode, double at)
    {
        if (State != SessionState.Recording || _pressedAt is not { } pressedAt) return;
        switch (ModeChords.OtherKey(keyCode, pressedAt, at, _handsFree))
        {
            case OtherKeyOutcome<RecordingMode>.SwitchMode chord when chord.Mode != Mode:
                Note($"mode → {chord.Mode} (chord key {keyCode})");
                SwitchMode(chord.Mode);
                break;
            case OtherKeyOutcome<RecordingMode>.Cancel:
                Note("cancelled — another key was pressed with the shortcut (treated as a key combo)");
                Cancel(silently: true);
                break;
        }
    }

    /// <summary>
    /// The running recording becomes <paramref name="mode"/>'s: the record keeps it (a retry runs the same way), the
    /// capsule shows it, and a clean-up started ahead of time with the other prompt starts again with this one. Command
    /// mode reads the selection now, while the app it's in is in front (see <see cref="ReadSelection"/>).
    /// </summary>
    private void SwitchMode(RecordingMode mode)
    {
        var translation = mode == RecordingMode.Translate ? _settings.TranslationTarget : null;
        // Translation or command mode turned off a moment ago: its chord is on its way out.
        if (mode == RecordingMode.Translate && translation is null) return;
        if (mode == RecordingMode.Command && !_settings.CommandModeEnabled) return;
        if (Mode == RecordingMode.Command) DropSelection();
        Mode = mode;
        if (_record != null)
        {
            _record.Translation = translation;
            if (mode == RecordingMode.Command) _record.Mode = SelectionCommand.RecordMode;
        }
        Hud.Model.Badge = translation?.Badge ?? (mode == RecordingMode.Command ? L("✎ 编辑", "✎ Edit") : null);
        if (mode == RecordingMode.Command) ReadSelection();
        if (_speculativePolish?.Raw is { } raw)
        {
            DropSpeculativePolish();
            // Never for an edit, which only starts once its instruction is complete.
            SpeculationChanged(raw);
        }
    }

    /// <summary>
    /// Command mode: reads the selection in the app in front, the one the edit goes back to (see <see cref="SelectedText"/>).
    /// One that can't be edited (none, in a password field or a terminal, too long) stops the recording at once and says
    /// why; nothing is pasted or copied. Read by Ctrl+C, it's only known once the keys are let go of, after a push-to-talk
    /// recording: then <see cref="EditSelection"/> says so.
    /// </summary>
    private void ReadSelection()
    {
        var generation = _generation;
        var cancel = new CancellationTokenSource();
        _selectionCancel = cancel;
        var hotkey = _settings.Hotkey;
        var chordKeys = ModeChords.Keys.ToList();
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _settings.MaxRecordingMinutes)) + TimeSpan.FromSeconds(10);
        // Clicks count from now on too, until the edit is done (see DeliverEdit).
        HotkeyMonitor.Shared.CountInput(true);
        var reading = Run();
        // Unless it was refused straight away (this app in front), which has already ended the recording.
        if (_selectionCancel == cancel) _selection = reading;

        async Task<(SelectedText.Place? Place, SelectionRead Read, long Mark)> Run()
        {
            var place = await SelectedText.Place.Front();
            SelectionRead read = place == null
                ? new SelectionRead.Refused(new SelectionRefusal.NoSelection())
                : await SelectedText.Read(place, hotkey, chordKeys, timeout, cancel.Token);
            var mark = HotkeyMonitor.Shared.InputCount;
            if (read is SelectionRead.Refused refused) SelectionRefused(refused.Reason, generation);
            return (place, read, mark);
        }
    }

    /// <summary>While still recording in command mode: the recording stops as if it never started, and the capsule says why.</summary>
    private void SelectionRefused(SelectionRefusal reason, int generation)
    {
        if (generation != _generation || State != SessionState.Recording || Mode != RecordingMode.Command) return;
        Note($"command mode: selection refused ({reason}) — recording stopped");
        Cancel(silently: true);
        Hud.Show(new HudPhase.Error(reason.Message()), 3);
    }

    private void DropSelection()
    {
        _selectionCancel?.Cancel();
        _selectionCancel = null;
        _selection = null;
        HotkeyMonitor.Shared.CountInput(false);
    }

    /// <summary>
    /// Registers the translation chord's keys while translation mode is on, and command mode's while that is (none
    /// otherwise), and returns every chord key,
    /// for the hotkey hook to keep from the focused app while the hotkey is held. A modifier-only hotkey's own key is
    /// never its chord. At launch, and whenever the settings it depends on change.
    /// </summary>
    public IReadOnlySet<int> UpdateModeChords()
    {
        var chords = new ModeChords<RecordingMode>();
        var hotkey = _settings.Hotkey.IsModifierOnly ? _settings.Hotkey.KeyCode : (int?)null;
        var translating = _settings.TranslationTarget != null;
        if (translating)
        {
            foreach (var key in _settings.TranslationChord.KeyCodes(hotkey))
            {
                chords.Register(key, RecordingMode.Translate);
            }
        }
        if (_settings.CommandModeEnabled)
        {
            foreach (var key in SelectionCommand.ChordKeys(_settings.CommandChord, translating ? _settings.TranslationChord : (TranslationChord?)null, hotkey))
            {
                chords.Register(key, RecordingMode.Command);
            }
        }
        ModeChords = chords;
        return chords.Keys;
    }

    /// <summary>
    /// The screen was locked or the PC is going to sleep. A hands-free recording mustn't keep the microphone open on the
    /// lock screen, nor its text land in whatever has focus afterwards: a recording stops as with Esc (a long one is kept
    /// in History), and one being processed goes to the clipboard instead of being pasted.
    /// </summary>
    public void SystemLocked(string reason)
    {
        // A warm microphone doesn't stay on behind the lock screen either; see SystemUnlocked.
        _isLocked = true;
        // Nor does a "Paste last dictation" still waiting go ahead once the session is unlocked again.
        DropPendingPasteLast(reason);
        switch (State)
        {
            case SessionState.Recording:
                Note($"{reason} while recording — cancelled as with Esc");
                EscapePressed();
                break;
            case SessionState.Processing:
                Note($"{reason} while processing — the text goes to the clipboard");
                _deliverByPaste = false;
                break;
        }
        UpdateWarmMicrophone();
        SilenceWarmMicrophoneIfLocked();
    }

    /// <summary>The session was unlocked: a warm microphone can come back on.</summary>
    public void SystemUnlocked()
    {
        if (!_isLocked) return;
        _isLocked = false;
        UpdateWarmMicrophone();
    }

    public void EscapePressed()
    {
        if (State == SessionState.Idle)
        {
            DropPendingPasteLast("Esc");
            return;
        }
        if (State == SessionState.Recording && CancelPolicy.Keeps(_counter.Seconds - _preRollSeconds)) KeepCancelledRecording();
        else Cancel(silently: false);
    }

    /// <summary>
    /// Esc on a long recording: stop and insert nothing, but keep it. The rest is transcribed in the background
    /// (no clean-up), so the text is in History for a day, where it can also be re-transcribed.
    /// </summary>
    private void KeepCancelledRecording()
    {
        if (State != SessionState.Recording || _record is not { } record || _pipeline is not { } pipeline) return;
        StopCapture();
        PlaySound(Sounds.Kind.Stop);
        record.Duration = _counter.Seconds;
        record.Status = DictationStatus.Cancelled;
        _history.Update(record);
        Note($"cancelled after {record.Duration:0.0}s — kept in History");
        var discarded = TakeDiscardedPolishes();
        Reset();
        Hud.Show(new HudPhase.Error(L("已取消，录音在历史记录中保留 24 小时", "Cancelled — kept in History for 24 hours")), 2.5);
        _keptInBackground.Add(record.Id);
        _ = Finish();

        async Task Finish()
        {
            string raw;
            try
            {
                raw = await pipeline.Finish();
            }
            catch (PipelineFailure failure)
            {
                raw = failure.PartialText;
            }
            catch
            {
                raw = TranscriptJoiner.Join(pipeline.CompletedTranscripts().OrderBy(p => p.Key).Select(p => p.Value));
            }
            if (!_keptInBackground.Remove(record.Id))
            {
                // Retried from History meanwhile: that run owns the record now. Only what this one spent is counted.
                Account(record, pipeline, null, discarded);
                return;
            }
            if (_history.Records.All(r => r.Id != record.Id)) return; // deleted meanwhile
            record.RawText = raw;
            record.ChunkTexts = pipeline.CompletedTranscripts();
            Account(record, pipeline, null, discarded);
            _history.Update(record);
        }
    }

    // MARK: - Recording

    /// <param name="pressedAt">
    /// The key event's own time: opening a cold microphone (a Bluetooth one can take a second or more) mustn't count
    /// against how long the key was held.
    /// </param>
    private void StartRecording(double pressedAt)
    {
        // First of all, so the connections are ready by the time the first request goes out.
        WarmConnections();
        // Someone is dictating, so the session is unlocked, whatever notification was missed (a resume without a lock).
        _isLocked = false;
        // The new dictation is what the user is after now.
        DropPendingPasteLast("a dictation started");
        if (_settings.SttEndpoint is not { } sttEndpoint || !_settings.IsConfigured(_settings.SttProvider))
        {
            Note("not started — speech-to-text provider not configured");
            var name = _settings.SttProvider.DisplayName();
            Hud.Show(new HudPhase.Error(L($"请先在设置里配置语音转文字服务商（{name}）", $"Set up the speech-to-text provider ({name}) in Settings first")), 3);
            OpenSettingsRequested?.Invoke("providers");
            return;
        }
        if (!Permissions.MicrophoneGranted)
        {
            Note("not started — no microphone permission");
            Hud.Show(new HudPhase.Error(L("需要麦克风权限：设置 → 隐私和安全性 → 麦克风",
                                          "Microphone access needed: Settings → Privacy & security → Microphone")), 4);
            return;
        }

        // Whatever the user did to the previous dictation is final now.
        _editWatcher.Finish(quiet: true, "next dictation");

        var record = _history.Create();
        record.Status = DictationStatus.Recording;
        _history.Update(record);

        WavFileWriter writer;
        try
        {
            writer = new WavFileWriter(record.AudioPath);
        }
        catch (Exception error)
        {
            // Otherwise the record stays "recording" and turns into a failed entry with no audio at the next launch.
            _history.Delete(record);
            Hud.Show(new HudPhase.Error(L("无法创建录音文件：", "Can't create the recording file: ") + error.Message), 4);
            return;
        }

        TranscriptionPipeline? pipeline = null;
        pipeline = MakePipeline(sttEndpoint, new Dictionary<int, string>(), onSpeculation: raw => _dispatcher.TryEnqueue(() =>
        {
            if (_pipeline == pipeline) SpeculationChanged(raw);
        }));
        _discardedPolishes.Clear();
        var counter = new SampleCounter();
        // A warm recorder on another input (the choice changed) is reopened on the right one.
        if (_recorder.IsRecording && _openDeviceId != ChosenDeviceId) _recorder.Stop();
        Hud.Model.Preview = "";
        if (_settings.LivePreview)
        {
            var hudModel = Hud.Model;
            _preview = new LivePreview(text =>
            {
                var line = LivePreviewText.Tail(text);
                _dispatcher.TryEnqueue(() => { if (_preview != null) hudModel.Preview = line; });
            });
            _preview.Start(_settings.SttLanguage);
        }
        // Starts with the pre-roll when the microphone was already running.
        var preRoll = _sink.Begin(samples =>
        {
            writer.Append(samples);
            pipeline.Append(samples);
            counter.Add(samples.Length);
        });
        _preRollSeconds = AudioFormat.Seconds(preRoll);
        try
        {
            if (!_recorder.IsRecording)
            {
                _recorder.DeviceId = ChosenDeviceId;
                _recorder.Start();
                _openDeviceId = ChosenDeviceId;
            }
        }
        catch (Exception error)
        {
            _sink.End();
            _preview?.Stop();
            _preview = null;
            writer.Close();
            _history.Delete(record);
            Hud.Show(new HudPhase.Error(L("无法开始录音：", "Can't start recording: ") + error.Message), 4);
            return;
        }

        _record = record;
        _writer = writer;
        _pipeline = pipeline;
        _counter = counter;
        _deliverByPaste = true;
        _pressedAt = pressedAt;
        _handsFree = false;
        Mode = RecordingMode.Dictation;
        State = SessionState.Recording;
        LastError = null;
        Hud.Model.ResetLevels();
        Hud.Model.StartedAt = DateTimeOffset.Now;
        Hud.Model.Badge = null;
        Hud.Show(new HudPhase.Recording());
        PlaySound(Sounds.Kind.Start);
        // After the start sound, which muting would cut off.
        if (_settings.MuteWhileRecording) _outputMute.Mute(TimeSpan.FromMilliseconds(_settings.PlaySounds ? 300 : 0));

        _maxDurationTimer = _dispatcher.CreateTimer();
        _maxDurationTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.MaxRecordingMinutes));
        _maxDurationTimer.IsRepeating = false;
        _maxDurationTimer.Tick += (_, _) => FinishRecording();
        _maxDurationTimer.Start();

        // A long dictation keeps the clean-up's connection (idle while speech-to-text works) from timing out.
        _warmTimer = _dispatcher.CreateTimer();
        _warmTimer.Interval = TimeSpan.FromSeconds(25);
        _warmTimer.IsRepeating = true;
        _warmTimer.Tick += (_, _) => WarmConnections();
        _warmTimer.Start();
    }

    /// <summary>
    /// Opens the connections this dictation's requests will use (speech-to-text and clean-up, main and backup routes)
    /// ahead of them, so the first request after the key is released doesn't wait for DNS, TCP and TLS. Each host once.
    /// </summary>
    private void WarmConnections()
    {
        var endpoints = new List<ProviderEndpoint?> { _settings.SttEndpoint, _settings.SttBackupEndpoint };
        if (_settings.PolishEnabled || _settings.TranslationTarget != null || _settings.CommandModeEnabled)
        {
            endpoints.Add(_settings.PolishEndpoint);
            endpoints.Add(_settings.PolishBackupEndpoint);
        }
        _preconnector.Warm(endpoints.Select(endpoint => endpoint is null ? null : new ApiClient(endpoint)));
    }

    /// <summary>How long each speech-to-text route takes, kept while the app runs, so the backup is asked when a chunk is late.</summary>
    private readonly Dictionary<string, TranscriptionLatency> _latencies = new();

    private TranscriptionPipeline MakePipeline(ProviderEndpoint endpoint, IReadOnlyDictionary<int, string> preset,
                                               Action<string?>? onSpeculation = null)
    {
        var language = _settings.SttLanguage.Length == 0 ? null : _settings.SttLanguage;
        var backup = _settings.SttBackupEndpoint is { } backupEndpoint
            ? new TranscriptionRoute(new ApiClient(backupEndpoint), new TranscriptionOptions(_settings.SttBackupModel.Trim(), language, _settings.VocabularyList))
            : null;
        var key = ModelPrice.Key(_settings.SttProvider, _settings.SttModel);
        if (!_latencies.TryGetValue(key, out var latency)) _latencies[key] = latency = new TranscriptionLatency();
        return new TranscriptionPipeline(new ApiClient(endpoint), new TranscriptionOptions(_settings.SttModel, language, _settings.VocabularyList), preset: preset,
                                         backup: backup, latency: latency, onSpeculation: onSpeculation);
    }

    private void StopCapture()
    {
        _maxDurationTimer?.Stop();
        _maxDurationTimer = null;
        _warmTimer?.Stop();
        _warmTimer = null;
        _outputMute.Restore();
        // Stopping delivers what the recorder still holds, so a cold recorder stops first. After End nothing reaches
        // the writer; a warm recorder goes back to filling the pre-roll.
        if (!_settings.KeepMicrophoneWarm)
        {
            _recorder.Stop();
            _openDeviceId = null;
        }
        _sink.End();
        _preview?.Stop();
        _preview = null;
        Hud.Model.Preview = "";
        _writer?.Close();
        // The dictation went on from the audio in memory; only a retry from History would miss what didn't reach the file.
        if (_writer?.Failure is { } failure) Note("recording file incomplete: " + failure.Message);
        _writer = null;
        SilenceWarmMicrophoneIfLocked();
    }

    /// <summary>
    /// While locked, a warm microphone goes off as soon as no dictation is capturing, also mid-processing: kept warm, it
    /// would go on filling the pre-roll behind the lock screen until the text was in.
    /// </summary>
    private void SilenceWarmMicrophoneIfLocked()
    {
        if (!_isLocked || IsCapturing || !_recorder.IsRecording) return;
        _recorder.Stop();
        _openDeviceId = null;
        _sink.End();
    }

    /// <summary>
    /// How long a warm microphone goes on delivering after the key is released. The capture thread hands its audio over
    /// in blocks: the one with the last syllable would otherwise land in the pre-roll and be thrown away. (A cold
    /// recorder delivers everything it has as it stops.)
    /// </summary>
    private static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(100);

    private void FinishRecording()
    {
        if (State != SessionState.Recording || _record is not { } record || _pipeline is not { } pipeline) return;
        // A cold recorder hands over everything it still holds as it stops, so it stops before the length is judged;
        // a warm one goes on for a moment instead (below), which a recording this short doesn't need.
        var warm = _settings.KeepMicrophoneWarm && _recorder.IsRecording;
        if (!warm) StopCapture();
        record.Duration = _counter.Seconds;
        if (record.Duration - _preRollSeconds < 0.4)
        {
            if (warm) StopCapture();
            PlaySound(Sounds.Kind.Stop);
            Note($"discarded — only {record.Duration:0.00}s of audio");
            // Accidental tap.
            pipeline.Cancel();
            _history.Delete(record);
            Reset();
            Hud.Show(new HudPhase.Hidden());
            return;
        }

        record.Status = DictationStatus.Processing;
        _history.Update(record);
        _record = record;
        State = SessionState.Processing;
        // Where the text should go: if another window is in front by the time it's ready, it goes to the clipboard.
        _pasteTarget = Win32.GetForegroundWindow();
        Hud.ShowWorking(_settings.PolishEnabled || record.Translation != null || record.IsCommand);

        // No waiting when the speaker paused before letting go and that pause is already being transcribed as the last
        // chunk: the moment's (quiet) audio changes nothing then, and the text may be ready at once.
        var waitsForTail = warm && !pipeline.EndsInCoveredPause;
        var processing = new CancellationTokenSource();
        _processing = processing;
        var generation = _generation;
        _ = Run();

        async Task Run()
        {
            if (waitsForTail)
            {
                // Cancelled meanwhile: Cancel stopped the capture itself.
                try { await Task.Delay(ReleaseTail, processing.Token); } catch (OperationCanceledException) { return; }
            }
            // Then the stop sound, which the microphone would have picked up (and muting would have cut off).
            if (warm) StopCapture();
            PlaySound(Sounds.Kind.Stop);
            record.Duration = _counter.Seconds;
            await Process(record, pipeline, generation, processing.Token);
        }
    }

    // MARK: - Processing

    private async Task Process(DictationRecord record, TranscriptionPipeline pipeline, int generation, CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        // Moves the bar as the speech-to-text chunks still out come back.
        var progress = Hud.Model.Progress;
        var watch = new CancellationTokenSource();
        _ = WatchTranscription();
        try
        {
            var raw = await pipeline.Finish();
            watch.Cancel();
            // Cancelled (and maybe a new dictation started) while the text came back: Cancel has dealt with this one.
            if (!IsCurrent(generation, token)) return;
            progress.Reach(new ProcessingProgress.Milestone.Transcribed());
            record.Timing = new DictationTiming { Transcription = started.Elapsed.TotalSeconds };
            if (pipeline.BackupChunkCount() is var backupChunks and > 0) record.Timing.TranscriptionBackupChunks = backupChunks;
            record.Timing.SpeculativeTail = pipeline.TailWasSpeculative;
            record.Timing.LastChunkRoute = pipeline.LastChunkUsedBackup is { } backup ? backup ? "backup" : "primary" : null;
            if (pipeline.TailWasSpeculative) Note($"last segment transcribed ahead of time: text {started.Elapsed.TotalSeconds:0.00}s after release");
            record.ChunkTexts = pipeline.CompletedTranscripts();
            record.RawText = raw;
            // A new transcript supersedes a clean-up from an earlier run (a retry): History mustn't show that one if this
            // run inserts the raw text (clean-up off or failed). A retry that fails before this keeps the old result.
            record.PolishedText = null;
            record.Error = null;
            if (raw.Trim().Length == 0)
            {
                record.Status = DictationStatus.Done;
                Account(record, pipeline, null, TakeDiscardedPolishes());
                _history.Update(record);
                Finish(new HudPhase.Error(L("没有识别到语音", "No speech detected")), 2);
                return;
            }
            if (record.IsCommand) await EditSelection(record, pipeline, generation, token);
            else await PolishAndDeliver(record, pipeline, generation, token);
        }
        catch (PipelineFailure failure)
        {
            if (!IsCurrent(generation, token)) return;
            record.ChunkTexts = pipeline.CompletedTranscripts();
            record.RawText = failure.PartialText;
            record.Status = DictationStatus.Failed;
            record.Error = failure.Message;
            Account(record, pipeline, null, TakeDiscardedPolishes());
            _history.Update(record);
            LastError = failure.Message;
            // An edit can't be retried: its selection isn't kept.
            Finish(new HudPhase.Error(record.IsCommand
                                          ? L("转写失败，选中的文字没有改动", "Transcription failed — your text is unchanged")
                                          : L("转写失败，可在托盘菜单重试", "Failed — retry from the tray menu")), 4);
        }
        catch (Exception error)
        {
            if (ApiException.From(error).IsCancelled || !IsCurrent(generation, token)) return;
            record.Status = DictationStatus.Failed;
            record.Error = error.Message;
            Account(record, pipeline, null, TakeDiscardedPolishes());
            _history.Update(record);
            LastError = error.Message;
            Finish(new HudPhase.Error(error.Message), 5);
        }
        finally
        {
            watch.Cancel();
        }

        async Task WatchTranscription()
        {
            while (!watch.IsCancellationRequested)
            {
                if (pipeline.TranscriptionProgress() is { } counts) progress.Reach(new ProcessingProgress.Milestone.Transcribing(counts.Done, counts.Total));
                try { await Task.Delay(50, watch.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task PolishAndDeliver(DictationRecord record, TranscriptionPipeline pipeline, int generation, CancellationToken token)
    {
        var begun = Stopwatch.StartNew();
        var text = record.RawText;
        string? notice = null;
        SegmentedPolishResult? polished = null;
        // A translation that couldn't be used, set aside for the clean-up: billed all the same.
        SegmentedPolishResult? setAside = null;
        // The translation's (and clean-up's) answers as they come in, counted even if Esc then stops the clean-up.
        var ledger = new PolishLedger();
        // The text going in is a translation: the user's edits to it say nothing about what speech recognition misheard.
        var translated = false;
        var translation = record.Translation;

        if (_settings.PolishEnabled || translation != null)
        {
            // When the speaker paused before letting go and said nothing more, the clean-up (or translation) of this very
            // transcript started while they were still holding the key: take it over (often it's already done).
            Task<SegmentedPolishResult> polishing;
            var ahead = 0.0;
            CancellationTokenRegistration adopted = default;
            // One that already failed (while recording) gets a fresh attempt instead.
            if (_speculativePolish is { } speculative && speculative.Raw == record.RawText && speculative.Translation == translation
                && !speculative.Task.IsFaulted && !speculative.Task.IsCanceled)
            {
                _speculativePolish = null;
                polishing = speculative.Task;
                // What it streams from now on moves this dictation's bar.
                speculative.Partials.Target = OnPolishPartial(record.RawText);
                ahead = speculative.Started.Elapsed.TotalSeconds;
                if (record.Timing is { } timing) timing.PolishHeadStart = Math.Max(0, ahead - (timing.Transcription ?? 0));
                adopted = token.Register(speculative.Cancel.Cancel);
                Note($"clean-up started {ahead:0.00}s ago, while recording");
            }
            else
            {
                DropSpeculativePolish();
                polishing = Polish(record.RawText, translation, OnPolishPartial(record.RawText), token);
            }
            // Saved while the clean-up runs, so the transcript survives a crash.
            _history.Update(record);
            try
            {
                if (translation != null)
                {
                    // The translation, or when it can't be used as it is, the clean-up (when that's on): one more request.
                    var raw = record.RawText;
                    var onPartial = OnPolishPartial(raw);
                    Func<Task<SegmentedPolishResult>>? cleanUp = _settings.PolishEnabled ? () => Polish(raw, null, onPartial, token) : null;
                    var outcome = await TranslatedPolish.Run(() => polishing, cleanUp, token, ledger);
                    if (outcome.Translated && outcome.Translation is { } translatedResult)
                    {
                        polished = translatedResult;
                        NoteTiming(translatedResult, ahead, record, $"translation → {translation.Id}");
                        text = translatedResult.Text;
                        record.PolishedText = translatedResult.Text;
                        record.Status = DictationStatus.Done;
                        translated = true;
                    }
                    else
                    {
                        setAside = outcome.Translation;
                        var fallback = outcome.TranslationFallback ?? PolishFallback.Failed;
                        var reason = outcome.TranslationError?.Message ?? fallback.TranslationReason();
                        record.Error = L($"未能翻译：{reason}", $"Couldn't translate: {reason}");
                        var answered = outcome.CleanUp ?? outcome.Translation;
                        if (record.Timing is { } timing)
                        {
                            // The wait after the key was released, both requests together.
                            timing.Polish = begun.Elapsed.TotalSeconds;
                            timing.PolishModel = answered?.Model;
                            timing.UsedBackup = answered?.UsedBackup;
                        }
                        polished = outcome.CleanUp;
                        if (outcome.CleanUp is { } cleaned && cleaned.Fallbacks.Count < cleaned.Parts.Count)
                        {
                            Note($"translation unusable ({fallback.ToString().ToLowerInvariant()}) → clean-up inserted");
                            notice = L("未能翻译，已插入整理后的原文", "Couldn't translate — inserted the clean-up");
                            text = cleaned.Text;
                            record.PolishedText = cleaned.Text;
                            record.Status = DictationStatus.Done;
                        }
                        else
                        {
                            Note($"translation unusable ({fallback.ToString().ToLowerInvariant()}) → inserted as spoken");
                            notice = L("未能翻译，已插入原文", "Couldn't translate — inserted as spoken");
                            record.Status = DictationStatus.PolishFailed;
                        }
                    }
                }
                else
                {
                    var outcome = await polishing;
                    polished = outcome;
                    NoteTiming(outcome, ahead, record, "polish");
                    var fallbacks = outcome.Fallbacks;
                    if (fallbacks.Count == outcome.Parts.Count)
                    {
                        // Cut off, an answer, a summary or a translation rather than the speaker's text, part after part.
                        notice = L("未整理，已插入原文", "Inserted without clean-up");
                        record.Error = fallbacks.Select(p => p.Error).FirstOrDefault(e => e != null)?.Message;
                        record.Status = DictationStatus.PolishFailed;
                    }
                    else
                    {
                        // A part whose clean-up was unusable goes in as spoken, between the parts that were cleaned up.
                        if (fallbacks.Count > 0) notice = L("部分未整理，已插入原文", "Partly inserted without clean-up");
                        text = outcome.Text;
                        record.PolishedText = outcome.Text;
                        record.Status = DictationStatus.Done;
                    }
                }
            }
            catch (Exception error)
            {
                if (ApiException.From(error).IsCancelled)
                {
                    // Esc while the clean-up ran after a translation that couldn't be used: nothing goes in, but that
                    // translation was billed. (Cancel has kept the record as cancelled; so it stays, now with its cost.)
                    var billed = ledger.All;
                    if (billed.Count == 0) return;
                    record.Status = DictationStatus.Cancelled;
                    Account(record, pipeline, null, billed);
                    _history.Update(record);
                    return;
                }
                notice = L("整理失败，已插入原文", "Clean-up failed — inserted raw text");
                record.Error = error.Message;
                record.Status = DictationStatus.PolishFailed;
            }
            finally
            {
                adopted.Dispose();
            }
        }
        else
        {
            record.Status = DictationStatus.Done;
        }
        if (record.Status == DictationStatus.PolishFailed) record.PolishedText = null;

        // Taken now: once the text is in, the next dictation may already be collecting its own.
        var discarded = TakeDiscardedPolishes();
        if (setAside != null) discarded.Add(setAside);
        if (IsCurrent(generation, token))
        {
            Hud.Model.Progress.Reach(new ProcessingProgress.Milestone.Delivered());
            if (record.Timing is { } timing) timing.Delivered = (timing.Transcription ?? 0) + begun.Elapsed.TotalSeconds;
            var delivery = await Deliver(text, notice, generation, token, watchEdits: !translated);
            if (record.Timing != null) record.Timing.Delivery = delivery;
        }
        // Esc came before the text was in place (or while it went in): Cancel has kept the record as cancelled, and so
        // it stays, now with its clean-up.
        if (token.IsCancellationRequested) record.Status = DictationStatus.Cancelled;
        // Costs, the usage ledger and History are written once the text is in place: nobody is waiting on them.
        Account(record, pipeline, polished, discarded);
        _history.Update(record);
    }

    // MARK: - Editing a selection

    /// <summary>
    /// Command mode, once the instruction is transcribed: the selection read when the chord was pressed is edited by it
    /// (see <see cref="SelectionCommand"/>), and the rewrite pasted over it (see <see cref="DeliverEdit"/>); an answer to a
    /// question about the text is shown and copied instead. Nothing is pasted when the selection couldn't be read, the
    /// rewrite can't be used (cut off, a refusal, nothing), or the model changed nothing; nor once Esc or the screen locking
    /// came first.
    /// </summary>
    private async Task EditSelection(DictationRecord record, TranscriptionPipeline pipeline, int generation, CancellationToken token)
    {
        var begun = Stopwatch.StartNew();
        SelectedText.Place? place = null;
        SelectionRead read = new SelectionRead.Refused(new SelectionRefusal.NoSelection());
        long mark = 0;
        // Read by Ctrl+C, the selection is only being read now that the keys are up.
        if (_selection is { } selection) (place, read, mark) = await selection;
        // Esc (or a new dictation) meanwhile: Cancel has dealt with this one.
        if (!IsCurrent(generation, token)) return;
        if (read is not SelectionRead.Selection selected || place == null)
        {
            var reason = read is SelectionRead.Refused refused ? refused.Reason : new SelectionRefusal.NoSelection();
            Note($"command mode: selection refused ({reason}) after recording");
            record.Status = DictationStatus.PolishFailed;
            record.Error = reason.Message();
            Account(record, pipeline, null, TakeDiscardedPolishes());
            _history.Update(record);
            Finish(new HudPhase.Error(reason.Message()), 3);
            return;
        }
        var original = selected.Text;
        record.SelectionLength = TextMetrics.CharacterCount(original);
        // Saved while the edit runs, so the instruction survives a crash.
        _history.Update(record);

        SegmentedPolishResult polished;
        try
        {
            polished = await Edit(original, record.RawText, OnPolishPartial(original), token);
        }
        catch (Exception error)
        {
            // Esc: Cancel has kept the record as cancelled.
            if (ApiException.From(error).IsCancelled || !IsCurrent(generation, token)) return;
            record.Status = DictationStatus.PolishFailed;
            record.Error = L("未能编辑：", "Couldn't edit: ") + error.Message;
            Account(record, pipeline, null, TakeDiscardedPolishes());
            _history.Update(record);
            Finish(new HudPhase.Error(L("未能编辑选中的文字：", "Couldn't edit the selection: ") + error.Message), 5);
            return;
        }
        NoteTiming(polished, 0, record, "edit of a selection");
        if (IsCurrent(generation, token))
        {
            Hud.Model.Progress.Reach(new ProcessingProgress.Milestone.Delivered());
            if (record.Timing is { } timing) timing.Delivered = (timing.Transcription ?? 0) + begun.Elapsed.TotalSeconds;
            if (polished.Fallbacks.FirstOrDefault()?.Fallback is { } fallback)
            {
                Note($"edit unusable ({fallback.ToString().ToLowerInvariant()}) → nothing pasted");
                var reason = fallback.CommandReason();
                record.Status = DictationStatus.PolishFailed;
                record.Error = L($"未能编辑：{reason}", $"Couldn't edit: {reason}");
                Finish(new HudPhase.Error(L($"未能编辑：{reason}，选中的文字没有改动", $"Couldn't edit: {reason}. Your text is unchanged")), 4);
            }
            else
            {
                record.Status = DictationStatus.Done;
                // Neither is kept in History: both come from the selection (see SelectionCommand.RecordMode).
                switch (SelectionCommand.ReplyOf(polished.Text, original))
                {
                    case SelectionCommand.Reply.Answer answer:
                        TextInserter.CopyToClipboard(answer.Text);
                        if (record.Timing != null) record.Timing.Delivery = "answered";
                        Finish(new HudPhase.Answer(answer.Text), 4);
                        break;
                    case SelectionCommand.Reply.Rewrite rewrite:
                        if (SelectionCommand.IsUnchanged(original, rewrite.Text))
                        {
                            if (record.Timing != null) record.Timing.Delivery = "unchanged";
                            Finish(new HudPhase.Error(L("没有需要修改的地方", "Nothing to change")), 2.5);
                        }
                        else
                        {
                            var delivery = await DeliverEdit(SelectionCommand.Replacement(original, rewrite.Text), place, original,
                                                             selected.Source, mark, generation, token);
                            if (record.Timing != null) record.Timing.Delivery = delivery;
                        }
                        break;
                }
            }
        }
        // Esc came before the text was in place (or while it went in): Cancel has kept the record as cancelled, and so it
        // stays, now with its edit.
        if (token.IsCancellationRequested) record.Status = DictationStatus.Cancelled;
        if (record.Status == DictationStatus.PolishFailed) record.PolishedText = null;
        Account(record, pipeline, polished, TakeDiscardedPolishes());
        _history.Update(record);
    }

    /// <summary>
    /// Pastes an edit over the selection it was made from, which the app's own undo then takes back. Only while it can go
    /// over that very selection (see <see cref="SelectionCommand.DeliveryOf"/>: the same window, tab and element in front,
    /// the same text still selected where UI Automation can tell, or else no click or key since it was read
    /// (<paramref name="mark"/>), something that takes text, the session unlocked); the window, the clicks and keys and the
    /// lock are checked once more right before Ctrl+V, as on macOS. Otherwise it goes to the clipboard, and the capsule
    /// says why. Returns how it went, for History.
    /// </summary>
    private async Task<string> DeliverEdit(string text, SelectedText.Place place, string original, SelectionSource source,
                                           long mark, int generation, CancellationToken token)
    {
        if (_deliverByPaste) await TextInserter.LeaveMenuMode();
        var canPaste = _deliverByPaste && !_isLocked;
        var sameWindow = canPaste && await place.IsInFront();
        var now = sameWindow && source == SelectionSource.Accessibility ? await SelectedText.SelectionNow() : null;
        var editable = !sameWindow || await FocusProbe.FocusedTarget() != FocusProbe.Target.NotEditable;
        var delivery = SelectionCommand.DeliveryOf(canPaste, sameWindow, original, source, now,
                                                   HotkeyMonitor.Shared.InputCount != mark, editable);
        if (!IsCurrent(generation, token))
        {
            Note("edit → nothing, it was cancelled meanwhile");
            return "cancelled";
        }
        if (delivery is SelectionCommand.Delivery.Clipboard clipboard)
        {
            Note($"edit → clipboard ({clipboard.Reason})");
            TextInserter.CopyToClipboard(text);
            Finish(new HudPhase.Error(clipboard.Reason.Message()), 3);
            return "copied";
        }
        var outcome = await TextInserter.Insert(text, _settings.RestoreClipboard,
                                                () => IsCurrent(generation, token) && _deliverByPaste && !_isLocked
                                                      && Win32.GetForegroundWindow() == place.Window
                                                      && Win32.WindowTitle(place.Window) == place.Title
                                                      && HotkeyMonitor.Shared.InputCount == mark);
        if (!IsCurrent(generation, token))
        {
            Note($"edit → {outcome}, but it was cancelled meanwhile");
            return "cancelled";
        }
        Note($"edit → {outcome}");
        switch (outcome)
        {
            case TextInserter.Outcome.Pasted:
                Reset();
                Hud.FinishWorking();
                return "pasted";
            case TextInserter.Outcome.Skipped:
                // The window, a click or key, or the lock changed just as Ctrl+V was due.
                TextInserter.CopyToClipboard(text);
                var reason = !_deliverByPaste || _isLocked ? SelectionCommand.ClipboardReason.Locked
                    : HotkeyMonitor.Shared.InputCount != mark ? SelectionCommand.ClipboardReason.SelectionChanged
                    : SelectionCommand.ClipboardReason.MovedAway;
                Finish(new HudPhase.Error(reason.Message()), 3);
                return "copied";
            default:
                Finish(new HudPhase.Copied(), 1.6);
                return "failed";
        }
    }

    /// <summary>The edit of <paramref name="selection"/> by <paramref name="instruction"/>, with the clean-up's models and routes.</summary>
    private async Task<SegmentedPolishResult> Edit(string selection, string instruction, Action<string>? onPartial, CancellationToken token)
    {
        if (_settings.PolishEndpoint is not { } endpoint || !_settings.IsConfigured(_settings.PolishProvider))
        {
            throw ApiException.MissingApiKey(_settings.PolishProvider.DisplayName());
        }
        var primary = Route(endpoint, _settings.PolishModel, null, selection);
        var backup = _settings.PolishBackupEndpoint is { } backupEndpoint
            ? Route(backupEndpoint, _settings.PolishBackupModel, null, selection)
            : null;
        return await SelectionCommand.Run(instruction, primary, backup, onPartial, token);
    }

    /// <summary>
    /// Saves how long the clean-up (or translation) <paramref name="outcome"/> took after the key was released (one started
    /// ahead of time had a head start of <paramref name="ahead"/> seconds), and which model answered.
    /// </summary>
    private static void NoteTiming(SegmentedPolishResult outcome, double ahead, DictationRecord record, string what)
    {
        var firstToken = Math.Max(0, outcome.FirstTokenSeconds - ahead);
        var total = Math.Max(0, outcome.TotalSeconds - ahead);
        if (record.Timing is { } timing)
        {
            timing.PolishFirstToken = firstToken;
            timing.Polish = total;
            timing.PolishModel = outcome.Model;
            timing.UsedBackup = outcome.UsedBackup;
        }
        var fallbacks = outcome.Fallbacks;
        Note($"{what}: {outcome.Model}{(outcome.UsedBackup ? " (backup)" : "")}, {outcome.Parts.Count} part(s), first token {firstToken:0.00}s, total {total:0.00}s"
             + (fallbacks.Count == 0 ? "" : "; unusable: " + string.Join(", ", fallbacks.Select(p => p.Fallback.ToString().ToLowerInvariant()))));
    }

    /// <summary>Whether the dictation <paramref name="generation"/> is still the one under way, not cancelled or followed by another.</summary>
    private bool IsCurrent(int generation, CancellationToken token) => generation == _generation && !token.IsCancellationRequested;

    /// <summary>Whether the window in front is no longer the one the recording was made for.</summary>
    private bool FocusMoved => _pasteTarget != 0 && Win32.GetForegroundWindow() != _pasteTarget;

    // MARK: - Clean-up ahead of time

    /// <summary>A clean-up of <c>Raw</c> (or its translation into <c>Translation</c>), started while recording.</summary>
    private sealed record SpeculativePolish(string Raw, TranslationTarget? Translation, Stopwatch Started, Task<SegmentedPolishResult> Task,
                                            CancellationTokenSource Cancel, PartialRelay Partials);

    /// <summary>
    /// Where a clean-up started ahead of time sends its partial text: nowhere until the dictation takes it over, so one
    /// that is dropped never moves the bar.
    /// </summary>
    private sealed class PartialRelay
    {
        public volatile Action<string>? Target;
        public void Send(string text) => Target?.Invoke(text);
    }

    /// <summary>
    /// The pipeline has the whole transcript the recording would give if it ended now (the speaker paused), or no longer
    /// has one (they went on). Starts cleaning that transcript up (or translating it, in translation mode), so the text can
    /// be ready when the key is released.
    /// </summary>
    private void SpeculationChanged(string? raw)
    {
        if (State != SessionState.Recording || Mode == RecordingMode.Command || !(_settings.PolishEnabled || Mode == RecordingMode.Translate)) return;
        if (raw != null && raw == _speculativePolish?.Raw) return;
        DropSpeculativePolish();
        if (raw is null || raw.Trim().Length == 0) return;
        // Only a transcript short enough for one request: a long one would go out as several at every pause, and its
        // parts are cleaned up in parallel once the key is released anyway.
        if (SegmentedPolish.NeedsParts(raw)) return;
        var cancel = new CancellationTokenSource();
        var partials = new PartialRelay();
        var translation = _record?.Translation;
        var task = Polish(raw, translation, partials.Send, cancel.Token);
        // A dropped clean-up's failure (usually its cancellation) is expected; observe it so it isn't reported.
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _speculativePolish = new SpeculativePolish(raw, translation, Stopwatch.StartNew(), task, cancel, partials);
        Note("clean-up started ahead of time (speaker paused)");
    }

    private void DropSpeculativePolish()
    {
        if (_speculativePolish is not { } speculative) return;
        _speculativePolish = null;
        if (speculative.Task.IsCompletedSuccessfully) _discardedPolishes.Add(speculative.Task.Result);
        else speculative.Cancel.Cancel();
    }

    /// <summary>
    /// The clean-ups done ahead of time that this dictation won't use, for its costs: the one still held is dropped first,
    /// so it isn't left for <see cref="Reset"/> to drop after the costs were written (and then lost).
    /// </summary>
    private List<SegmentedPolishResult> TakeDiscardedPolishes()
    {
        DropSpeculativePolish();
        var discarded = _discardedPolishes.ToList();
        _discardedPolishes.Clear();
        return discarded;
    }

    /// <summary>Moves the bar as the clean-up streams in: its first token, then its length against the transcript's.</summary>
    private Action<string> OnPolishPartial(string transcript)
    {
        var progress = Hud.Model.Progress;
        return text =>
        {
            var received = text.Length;
            _dispatcher.TryEnqueue(() => progress.Reach(new ProcessingProgress.Milestone.Polishing(received, transcript.Length)));
        };
    }

    /// <summary>
    /// Pastes at the cursor unless focus is clearly not a text input, or has moved to another window since the
    /// recording stopped. Whether the paste actually landed is detected from the target app reading the clipboard: if
    /// it did, the user's previous clipboard is restored; if not, the text stays on the clipboard and the HUD says so.
    /// Returns how it went, for History (see <see cref="DictationTiming.Delivery"/>).
    ///
    /// Esc (or the screen locking) can come at any point while the text goes in: the paste and the Enter are only sent
    /// while the dictation is still current, and a cancelled one leaves the session and the HUD to what replaced it.
    /// </summary>
    /// <param name="watchEdits">False for a translation, whose edits say nothing about what speech recognition misheard.</param>
    private async Task<string> Deliver(string text, string? notice, int generation, CancellationToken token, bool watchEdits = true)
    {
        if (_deliverByPaste) await TextInserter.LeaveMenuMode();
        var target = _deliverByPaste ? await FocusProbe.FocusedTarget() : FocusProbe.Target.NotEditable;
        if (!IsCurrent(generation, token))
        {
            Note("deliver → nothing, the dictation was cancelled meanwhile");
            return "cancelled";
        }
        if (target == FocusProbe.Target.NotEditable || _deliverByPaste && FocusMoved)
        {
            Note($"deliver → clipboard ({(target == FocusProbe.Target.NotEditable ? "focus not editable" : "another window in front")})");
            TextInserter.CopyToClipboard(text);
            Finish(notice is { } n ? new HudPhase.Error(n) : new HudPhase.Copied(), 1.6);
            return "copied";
        }
        var outcome = await TextInserter.Insert(text, _settings.RestoreClipboard,
                                                () => IsCurrent(generation, token) && _deliverByPaste && !FocusMoved);
        if (!IsCurrent(generation, token))
        {
            Note($"deliver → {outcome}, but the dictation was cancelled meanwhile");
            return "cancelled";
        }
        Note($"deliver → {outcome} (focus {target})");
        switch (outcome)
        {
            case TextInserter.Outcome.Pasted:
                // Sends it, in a chat box or an AI prompt. Then there's no text left to watch for corrections.
                var sent = false;
                if (_settings.PressEnterAfterInsert)
                {
                    // Not into another window that came to the front after the paste, nor once the screen locked.
                    sent = await TextInserter.PressEnter(() => IsCurrent(generation, token) && _deliverByPaste && !FocusMoved);
                    if (!IsCurrent(generation, token)) return "pasted";
                }
                if (notice is { } message)
                {
                    Finish(new HudPhase.Error(message), 3);
                }
                else
                {
                    Reset();
                    Hud.FinishWorking();
                }
                if (_settings.LearnFromEdits && watchEdits && !sent) _editWatcher.Watch(text);
                return "pasted";
            case TextInserter.Outcome.Skipped:
                // The screen locked or another window came to the front just as Ctrl+V was due.
                TextInserter.CopyToClipboard(text);
                Finish(notice is { } skippedNotice ? new HudPhase.Error(skippedNotice) : new HudPhase.Copied(), 1.6);
                return "copied";
            default:
                Finish(new HudPhase.Copied(), 1.6);
                return "failed";
        }
    }

    /// <summary>The clean-up of <paramref name="raw"/>, or its translation into <paramref name="translation"/>, with the clean-up's models and routes.</summary>
    private async Task<SegmentedPolishResult> Polish(string raw, TranslationTarget? translation, Action<string>? onPartial, CancellationToken token)
    {
        if (_settings.PolishEndpoint is not { } endpoint || !_settings.IsConfigured(_settings.PolishProvider))
        {
            throw ApiException.MissingApiKey(_settings.PolishProvider.DisplayName());
        }
        var primary = Route(endpoint, _settings.PolishModel, translation);
        var backup = _settings.PolishBackupEndpoint is { } backupEndpoint ? Route(backupEndpoint, _settings.PolishBackupModel, translation) : null;
        return await SegmentedPolish.Run(raw, primary, backup, onPartial, token);
    }

    private PolishRoute Route(ProviderEndpoint endpoint, string model, TranslationTarget? translation, string? selection = null)
    {
        model = model.Trim();
        return new PolishRoute(new ApiClient(endpoint), new PolishOptions(
            model,
            _settings.VocabularyList,
            _settings.ExtraInstructions,
            endpoint.Id == ProviderId.OpenRouter ? _modelInfo.GetValueOrDefault(model) : null,
            Misheard: _settings.MisheardHints,
            Host: endpoint.Id == ProviderId.OpenRouter ? _settings.OpenRouterHost(model) : null,
            Translation: translation,
            Selection: selection));
    }

    /// <summary>
    /// Prices what this run's requests used (the speech-to-text of every chunk sent, and every clean-up answer, used or
    /// not, with the hedge's cancelled requests) and adds it, with the dictation's words once it's finished, to the Home
    /// page totals.
    /// </summary>
    /// <param name="discarded">Clean-ups done ahead of time for a transcript the speaker then went on from.</param>
    private void Account(DictationRecord record, TranscriptionPipeline pipeline, SegmentedPolishResult? polished,
                         IReadOnlyList<SegmentedPolishResult> discarded)
    {
        var prices = PriceStore.Shared;
        var (transcription, unpriced) = CostEstimator.Transcriptions(
            pipeline.Usages(), prices.Price(_settings.SttProvider, _settings.SttModel), _settings.SttProvider == ProviderId.OpenRouter);
        // Chunks the backup speech-to-text route answered are priced with its model.
        var (backupCost, backupUnpriced) = CostEstimator.Transcriptions(
            pipeline.BackupUsages(), prices.Price(_settings.SttBackupProvider, _settings.SttBackupModel),
            _settings.SttBackupProvider == ProviderId.OpenRouter);
        transcription += backupCost;
        unpriced += backupUnpriced;
        var cleanup = 0.0;
        void Add(RequestUsage? usage, ProviderId provider, string model)
        {
            if (usage is null) return;
            var cost = CostEstimator.Chat(usage, prices.Price(provider, model), provider == ProviderId.OpenRouter);
            if (cost is { } c) cleanup += c; else unpriced++;
        }
        var outcomes = polished is null ? discarded : discarded.Append(polished);
        foreach (var answer in outcomes.SelectMany(o => o.Answers))
        {
            Add(answer.Result.Usage, answer.Provider, answer.Model);
            foreach (var request in answer.Cancelled ?? []) Add(request.Usage, request.Provider, request.Model);
        }
        if (transcription + cleanup > 0) record.Cost = (record.Cost ?? 0) + transcription + cleanup;
        Note($"cost: transcription ${transcription:0.######}, clean-up ${cleanup:0.######}, unpriced requests {unpriced}");
        UsageStore.Shared.Record(record, transcription, cleanup, unpriced);
    }

    // MARK: - Action shortcuts

    /// <summary>
    /// An action's shortcut was pressed (see <see cref="ShortcutAction"/>). Only between dictations: while one is under way
    /// its keys do nothing (they're kept from the focused app all the same).
    /// </summary>
    /// <param name="shortcut">The keys that were pressed.</param>
    public void Perform(ShortcutAction action, Hotkey? shortcut)
    {
        if (State != SessionState.Idle)
        {
            Note($"{action.RawValue()} ignored — a dictation is under way");
            return;
        }
        switch (action)
        {
            case ShortcutAction.PasteLastDictation:
                PasteLastDictation(shortcut, Win32.GetForegroundWindow());
                break;
        }
    }

    /// <summary>
    /// Puts the newest finished dictation at the cursor again, the way a dictation's text goes in: pasted, with the
    /// clipboard put back afterwards, or left on the clipboard when it can't be. When the newest one failed with its
    /// recording saved, re-sends that instead and pastes the result (see <see cref="LastDictation"/>).
    /// </summary>
    /// <param name="shortcut">
    /// The keys that asked for it, let go of before anything is pasted: Ctrl+V sent while they're held would be read
    /// together with them, as another shortcut altogether. Null from the tray panel, which first has to give focus back
    /// to the app the text goes to.
    /// </param>
    /// <param name="target">
    /// The window the text goes to, only while it's still in front (otherwise the clipboard): the one in front when the
    /// shortcut was pressed, or before the tray panel opened. 0 when that isn't known: then whichever is in front.
    /// </param>
    public void PasteLastDictation(Hotkey? shortcut, nint target)
    {
        if (State != SessionState.Idle || _pendingPasteLast != null) return;
        var id = ++_pasteLastCount;
        _pendingPasteLast = id;
        _ = Run();

        async Task Run()
        {
            try
            {
                var released = true;
                if (shortcut != null) released = await HotkeyMonitor.Shared.WaitForRelease(shortcut, TimeSpan.FromSeconds(3));
                else await Task.Delay(300);
                await PasteLast(id, target, released);
            }
            finally
            {
                if (_pendingPasteLast == id) _pendingPasteLast = null;
            }
        }
    }

    /// <param name="released">False when the shortcut's keys were still held after a while; the text then goes to the clipboard.</param>
    private async Task PasteLast(int id, nint target, bool released)
    {
        // Dropped meanwhile: by Esc, the session locking, or a dictation started while the keys were still held.
        if (!PasteLastIsCurrent(id)) return;
        var records = _history.Records.ToList();
        var entries = records.Select(r => new LastDictation.Entry(
            r.Status.PruningKind(),
            r.FinalText.Trim().Length > 0,
            // Only a failed one is ever re-sent, so only its recording is looked for on disk.
            r.Status == DictationStatus.Failed && r.HasAudio,
            r.IsCommand)).ToList();
        switch (LastDictation.Choose(entries))
        {
            case LastDictation.Choice.Retry retry:
                Note("paste last dictation → the newest one failed: retried");
                // Its text goes in like a dictation's: only into the target, while it's still in front (see FocusMoved).
                Retry(records[retry.Index], paste: released && !_isLocked, target: target);
                return;
            case LastDictation.Choice.Paste paste:
                var text = records[paste.Index].FinalText;
                if (released) await TextInserter.LeaveMenuMode();
                if (!PasteLastIsCurrent(id)) return;
                // The keys still held, another window in front, or focus on something that can't take text: the clipboard.
                var editable = released && IsInFront(target) && await FocusProbe.FocusedTarget() != FocusProbe.Target.NotEditable;
                if (!PasteLastIsCurrent(id)) return;
                if (!editable)
                {
                    Note("paste last dictation → clipboard");
                    TextInserter.CopyToClipboard(text);
                    Hud.Show(new HudPhase.Copied(), 1.6);
                    return;
                }
                var outcome = await TextInserter.Insert(text, _settings.RestoreClipboard, () => PasteLastIsCurrent(id) && IsInFront(target));
                Note($"paste last dictation → {outcome}");
                // Esc, the session locking or a new dictation while it went in: that leaves the HUD and the clipboard alone.
                if (!PasteLastIsCurrent(id)) return;
                switch (outcome)
                {
                    case TextInserter.Outcome.NotPasted:
                        Hud.Show(new HudPhase.Copied(), 1.6);
                        break;
                    case TextInserter.Outcome.Skipped:
                        // Another window came to the front just as Ctrl+V was due.
                        TextInserter.CopyToClipboard(text);
                        Hud.Show(new HudPhase.Copied(), 1.6);
                        break;
                }
                return;
            default:
                Note("paste last dictation → nothing to paste");
                Hud.Show(new HudPhase.Error(L("还没有可以粘贴的听写", "No dictation to paste yet")), 2);
                return;
        }
    }

    /// <summary>Whether the "Paste last dictation" <paramref name="id"/> is still wanted: not dropped, and no dictation under way.</summary>
    private bool PasteLastIsCurrent(int id) => _pendingPasteLast == id && State == SessionState.Idle && !_isLocked;

    private void DropPendingPasteLast(string reason)
    {
        if (_pendingPasteLast == null) return;
        _pendingPasteLast = null;
        Note($"paste last dictation dropped — {reason}");
    }

    /// <summary>Whether <paramref name="window"/> is in front (0: it couldn't be told, so there's nothing to check).</summary>
    private static bool IsInFront(nint window) => window == 0 || Win32.GetForegroundWindow() == window;

    // MARK: - Retry / cancel

    /// <summary>Re-runs a saved dictation. Chunks that already succeeded are reused; only failed ones are re-sent.</summary>
    /// <param name="target">The window its text is pasted into, only while that window is in front (0: whichever is in front then).</param>
    public void Retry(DictationRecord saved, bool paste, nint target = 0)
    {
        if (State != SessionState.Idle) return;
        // An edit of a selection can't run again without its selection, which isn't kept; run as a dictation, it would
        // paste the instruction.
        if (saved.IsCommand)
        {
            Hud.Show(new HudPhase.Error(L("编辑选中文字的记录不能重试", "An edit of a selection can't be retried")), 3);
            return;
        }
        if (_settings.SttEndpoint is not { } endpoint) return;
        short[]? samples = null;
        try
        {
            if (saved.HasAudio) samples = Wav.DecodeSamples(File.ReadAllBytes(saved.AudioPath));
        }
        catch (IOException) { }
        if (samples == null)
        {
            Hud.Show(new HudPhase.Error(L("找不到这条记录的录音文件", "The recording for this entry is gone")), 3);
            return;
        }
        _deliverByPaste = paste;
        // A cancelled recording still being transcribed in the background is this run's now.
        _keptInBackground.Remove(saved.Id);
        var record = saved.Copy();
        record.Status = DictationStatus.Processing;
        record.Error = null;
        // Saved transcripts are reused by chunk index, so only when the recording is cut into the same chunks again.
        var preset = saved.ChunkLayout == Chunker.Layout ? saved.ChunkTexts : new Dictionary<int, string>();
        // Saved with the new layout, so only transcripts of the new chunks may stay (a quit mid-retry keeps them).
        record.ChunkTexts = new Dictionary<int, string>(preset);
        record.ChunkLayout = Chunker.Layout;
        _history.Update(record);
        _record = record;
        _pasteTarget = target;
        State = SessionState.Processing;
        // A translation dictation is translated again, into the language it was translated into then.
        Mode = record.Translation is null ? RecordingMode.Dictation : RecordingMode.Translate;
        Hud.Model.Badge = record.Translation?.Badge;
        Hud.ShowWorking(_settings.PolishEnabled || record.Translation != null);

        var pipeline = MakePipeline(endpoint, preset);
        _pipeline = pipeline;
        var processing = new CancellationTokenSource();
        _processing = processing;
        var generation = _generation;
        _ = RunRetry();

        async Task RunRetry()
        {
            // Let the menu close and focus return to the previous app before pasting.
            try { await Task.Delay(300, processing.Token); } catch (OperationCanceledException) { return; }
            pipeline.Append(samples);
            await Process(record, pipeline, generation, processing.Token);
        }
    }

    public void Cancel(bool silently)
    {
        var wasRecording = State == SessionState.Recording;
        _processing?.Cancel();
        _pipeline?.Cancel();
        // Also right after the key was released, while the last of the audio is still coming in: no waiting for it.
        if (IsCapturing) StopCapture();
        if (wasRecording)
        {
            if (_record != null) _history.Delete(_record);
        }
        else if (_record is { } record)
        {
            // Cancelled while processing: keep what was transcribed; it can be re-transcribed for a day.
            if (_pipeline is { } pipeline)
            {
                record.ChunkTexts = pipeline.CompletedTranscripts();
                if (record.RawText.Length == 0)
                {
                    record.RawText = TranscriptJoiner.Join(record.ChunkTexts.OrderBy(p => p.Key).Select(p => p.Value));
                }
            }
            record.Status = DictationStatus.Cancelled;
            _history.Update(record);
        }
        Reset();
        if (silently) Hud.Show(new HudPhase.Hidden());
        else Hud.Show(new HudPhase.Error(L("已取消", "Cancelled")), 1);
    }

    /// <summary>
    /// Quitting mid-recording keeps the dictation instead of deleting it: capture stops, the recording file is closed (so
    /// it's complete) and the record stays in History, where the next launch shows it as interrupted with its audio saved,
    /// ready to retry. Runs as the app exits, so it only does what's quick. A dictation already being processed is
    /// cancelled as before (what was transcribed is kept for a day).
    /// </summary>
    public void StopForQuit()
    {
        if (!IsCapturing || _record is not { } record)
        {
            // Being processed: its record is left as it is, so the next launch shows it interrupted and retryable (as on
            // macOS), rather than cancelled and kept only a day.
            _processing?.Cancel();
            _pipeline?.Cancel();
            return;
        }
        _processing?.Cancel();
        _pipeline?.Cancel();
        StopCapture();
        record.Duration = _counter.Seconds;
        // A retry reuses the segments already transcribed.
        if (_pipeline is { } pipeline) record.ChunkTexts = pipeline.CompletedTranscripts();
        // An accidental tap isn't worth an entry.
        if (record.Duration - _preRollSeconds < 0.4) _history.Delete(record);
        else _history.Update(record);
        Hud.Show(new HudPhase.Hidden());
    }

    private void Abort(string message)
    {
        if (State != SessionState.Recording) return;
        // Keep whatever was captured: process it rather than throwing it away.
        Hud.Show(new HudPhase.Error(message), 2);
        FinishRecording();
    }

    private void Finish(HudPhase phase, double hideAfter)
    {
        Reset();
        Hud.Show(phase, hideAfter);
    }

    private void Reset()
    {
        DropSpeculativePolish();
        _generation++;
        State = SessionState.Idle;
        _preRollSeconds = 0;
        _pipeline = null;
        _record = null;
        _pressedAt = null;
        _pasteTarget = 0;
        _handsFree = false;
        Mode = RecordingMode.Dictation;
        DropSelection();
        _processing = null;
    }

    // MARK: - Misc

    /// <summary>OpenRouter model metadata, used to turn reasoning off for the clean-up models.</summary>
    public void RefreshModelInfo()
    {
        var usesOpenRouter = _settings.PolishProvider == ProviderId.OpenRouter
            || (_settings.PolishBackupEnabled && _settings.PolishBackupProvider == ProviderId.OpenRouter);
        if (!usesOpenRouter || _settings.Endpoint(ProviderId.OpenRouter) is not { } endpoint) return;
        _ = Load();

        async Task Load()
        {
            try
            {
                var models = await new ApiClient(endpoint).ListModels();
                var map = new Dictionary<string, ModelInfo>();
                foreach (var model in models) map.TryAdd(model.Id, model);
                _modelInfo = map;
            }
            catch
            {
                // Without metadata, reasoning is still excluded from the output; it just isn't switched off.
            }
        }
    }

    private void PlaySound(Sounds.Kind kind)
    {
        if (_settings.PlaySounds) Sounds.Play(kind);
    }
}
