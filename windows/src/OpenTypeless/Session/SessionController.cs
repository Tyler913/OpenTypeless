using System.Diagnostics;
using Microsoft.UI.Dispatching;
using OpenTypeless.Audio;
using OpenTypeless.Input;
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
    private DateTimeOffset? _pressedAt;
    private bool _handsFree;
    private CancellationTokenSource? _processing;
    private DispatcherQueueTimer? _maxDurationTimer;
    /// <summary>Keeps the connections warm while recording (see <see cref="WarmConnections"/>).</summary>
    private DispatcherQueueTimer? _warmTimer;
    private readonly Preconnector _preconnector = new();
    /// <summary>A clean-up started before the key was released, on the transcript a speculative tail gave.</summary>
    private SpeculativePolish? _speculativePolish;
    /// <summary>Clean-ups started ahead of time that finished but were then dropped because the speaker went on: they were billed too.</summary>
    private readonly List<HedgedPolishResult> _discardedPolishes = new();
    private Dictionary<string, ModelInfo> _modelInfo = new();
    /// <summary>False when retrying from the history page: the result goes to the clipboard instead.</summary>
    private bool _deliverByPaste = true;

    /// <summary>A press shorter than this toggles hands-free mode instead of push-to-talk.</summary>
    private static readonly TimeSpan TapThreshold = TimeSpan.FromSeconds(0.35);

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
            Abort(L("录音中断：", "Recording interrupted: ") + error.Message));
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
    /// With "Keep microphone ready" on, keeps the recorder running between dictations (on the chosen input), so a
    /// dictation starts at once and includes the moment before the key; otherwise lets it stop. Called at launch,
    /// when settings change and after each dictation.
    /// </summary>
    public void UpdateWarmMicrophone()
    {
        if (State != SessionState.Idle) return;
        if (_settings.KeepMicrophoneWarm && Permissions.MicrophoneGranted)
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
        }
    }

    // MARK: - Hotkey events

    private static void Note(string text) => AppLog.Debug("session", text);

    public void HotkeyPressed()
    {
        switch (State)
        {
            case SessionState.Idle:
                Note("start recording");
                StartRecording();
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

    public void HotkeyReleased()
    {
        if (State != SessionState.Recording || _handsFree || _pressedAt is not { } pressedAt) return;
        var held = DateTimeOffset.Now - pressedAt;
        Note(held < TapThreshold ? $"tap ({held.TotalSeconds:0.00}s) → hands-free" : $"released after {held.TotalSeconds:0.00}s → stop");
        if (held < TapThreshold) _handsFree = true;
        else FinishRecording();
    }

    /// <summary>Right Ctrl + C and similar combos shouldn't start a dictation.</summary>
    public void OtherKeyPressed()
    {
        if (State != SessionState.Recording || _handsFree || _pressedAt is not { } pressedAt
            || DateTimeOffset.Now - pressedAt >= TimeSpan.FromSeconds(1)) return;
        Note("cancelled — another key was pressed with the shortcut (treated as a key combo)");
        Cancel(silently: true);
    }

    public void EscapePressed()
    {
        if (State == SessionState.Idle) return;
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
        Reset();
        Hud.Show(new HudPhase.Error(L("已取消，录音在历史记录中保留 24 小时", "Cancelled — kept in History for 24 hours")), 2.5);
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
            if (_history.Records.All(r => r.Id != record.Id)) return; // deleted meanwhile
            record.RawText = raw;
            record.ChunkTexts = pipeline.CompletedTranscripts();
            Account(record, pipeline, null);
            _history.Update(record);
        }
    }

    // MARK: - Recording

    private void StartRecording()
    {
        // First of all, so the connections are ready by the time the first request goes out.
        WarmConnections();
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
        _pressedAt = DateTimeOffset.Now;
        _handsFree = false;
        State = SessionState.Recording;
        LastError = null;
        Hud.Model.ResetLevels();
        Hud.Model.StartedAt = DateTimeOffset.Now;
        Hud.Show(new HudPhase.Recording());
        PlaySound(Sounds.Kind.Start);

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
        if (_settings.PolishEnabled)
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
            ? new TranscriptionRoute(new ApiClient(backupEndpoint), new TranscriptionOptions(_settings.SttBackupModel.Trim(), language))
            : null;
        var key = ModelPrice.Key(_settings.SttProvider, _settings.SttModel);
        if (!_latencies.TryGetValue(key, out var latency)) _latencies[key] = latency = new TranscriptionLatency();
        return new TranscriptionPipeline(new ApiClient(endpoint), new TranscriptionOptions(_settings.SttModel, language), preset: preset,
                                         backup: backup, latency: latency, onSpeculation: onSpeculation);
    }

    private void StopCapture()
    {
        _maxDurationTimer?.Stop();
        _maxDurationTimer = null;
        _warmTimer?.Stop();
        _warmTimer = null;
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
        _writer = null;
    }

    private void FinishRecording()
    {
        if (State != SessionState.Recording || _record is not { } record || _pipeline is not { } pipeline) return;
        StopCapture();
        PlaySound(Sounds.Kind.Stop);

        record.Duration = _counter.Seconds;
        if (record.Duration - _preRollSeconds < 0.4)
        {
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
        Hud.ShowWorking(_settings.PolishEnabled);

        var processing = new CancellationTokenSource();
        _processing = processing;
        _ = Process(record, pipeline, processing.Token);
    }

    // MARK: - Processing

    private async Task Process(DictationRecord record, TranscriptionPipeline pipeline, CancellationToken token)
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
            progress.Reach(new ProcessingProgress.Milestone.Transcribed());
            record.Timing = new DictationTiming { Transcription = started.Elapsed.TotalSeconds };
            if (pipeline.BackupChunkCount() is var backupChunks and > 0) record.Timing.TranscriptionBackupChunks = backupChunks;
            if (pipeline.TailWasSpeculative) Note($"last segment transcribed ahead of time: text {started.Elapsed.TotalSeconds:0.00}s after release");
            record.ChunkTexts = pipeline.CompletedTranscripts();
            record.RawText = raw;
            record.Error = null;
            if (raw.Trim().Length == 0)
            {
                record.Status = DictationStatus.Done;
                Account(record, pipeline, null);
                _history.Update(record);
                Finish(new HudPhase.Error(L("没有识别到语音", "No speech detected")), 2);
                return;
            }
            await PolishAndDeliver(record, pipeline, token);
        }
        catch (PipelineFailure failure)
        {
            record.ChunkTexts = pipeline.CompletedTranscripts();
            record.RawText = failure.PartialText;
            record.Status = DictationStatus.Failed;
            record.Error = failure.Message;
            Account(record, pipeline, null);
            _history.Update(record);
            LastError = failure.Message;
            Finish(new HudPhase.Error(L("转写失败，可在托盘菜单重试", "Failed — retry from the tray menu")), 4);
        }
        catch (Exception error)
        {
            if (ApiException.From(error).IsCancelled) return;
            record.Status = DictationStatus.Failed;
            record.Error = error.Message;
            Account(record, pipeline, null);
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

    private async Task PolishAndDeliver(DictationRecord record, TranscriptionPipeline pipeline, CancellationToken token)
    {
        var text = record.RawText;
        string? notice = null;
        HedgedPolishResult? polished = null;

        if (_settings.PolishEnabled)
        {
            // When the speaker paused before letting go and said nothing more, the clean-up of this very transcript
            // started while they were still holding the key: take it over (often it's already done).
            Task<HedgedPolishResult> polishing;
            var ahead = 0.0;
            CancellationTokenRegistration adopted = default;
            // One that already failed (while recording) gets a fresh attempt instead.
            if (_speculativePolish is { } speculative && speculative.Raw == record.RawText
                && !speculative.Task.IsFaulted && !speculative.Task.IsCanceled)
            {
                _speculativePolish = null;
                polishing = speculative.Task;
                // What it streams from now on moves this dictation's bar.
                speculative.Partials.Target = OnPolishPartial(record.RawText);
                ahead = speculative.Started.Elapsed.TotalSeconds;
                adopted = token.Register(speculative.Cancel.Cancel);
                Note($"clean-up started {ahead:0.00}s ago, while recording");
            }
            else
            {
                DropSpeculativePolish();
                polishing = Polish(record.RawText, OnPolishPartial(record.RawText), token);
            }
            // Saved while the clean-up runs, so the transcript survives a crash.
            _history.Update(record);
            try
            {
                var outcome = await polishing;
                polished = outcome;
                var result = outcome.Result;
                // The wait after the key was released: a clean-up started ahead of time had a head start.
                var firstToken = Math.Max(0, outcome.FirstTokenSeconds - ahead);
                var total = Math.Max(0, outcome.TotalSeconds - ahead);
                if (record.Timing is { } timing)
                {
                    timing.PolishFirstToken = firstToken;
                    timing.Polish = total;
                    timing.PolishModel = outcome.Model;
                    timing.UsedBackup = outcome.UsedBackup;
                }
                Note($"polish: {outcome.Model}{(outcome.UsedBackup ? " (backup)" : "")}, first token {firstToken:0.00}s, total {total:0.00}s");
                if (result.Truncated || Prompts.LooksLikeAnAnswer(record.RawText, result.Text))
                {
                    notice = L("未整理，已插入原文", "Inserted without clean-up");
                    record.Status = DictationStatus.PolishFailed;
                }
                else
                {
                    text = result.Text;
                    record.PolishedText = result.Text;
                    record.Status = DictationStatus.Done;
                }
            }
            catch (Exception error)
            {
                if (ApiException.From(error).IsCancelled) return;
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

        if (!token.IsCancellationRequested)
        {
            Hud.Model.Progress.Reach(new ProcessingProgress.Milestone.Delivered());
            await Deliver(text, notice);
        }
        // Costs, the usage ledger and History are written once the text is in place: nobody is waiting on them.
        Account(record, pipeline, polished, _discardedPolishes.ToList());
        _discardedPolishes.Clear();
        _history.Update(record);
    }

    // MARK: - Clean-up ahead of time

    /// <summary>A clean-up of <c>Raw</c>, started while recording.</summary>
    private sealed record SpeculativePolish(string Raw, Stopwatch Started, Task<HedgedPolishResult> Task, CancellationTokenSource Cancel,
                                            PartialRelay Partials);

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
    /// has one (they went on). Starts cleaning that transcript up, so the text can be ready when the key is released.
    /// </summary>
    private void SpeculationChanged(string? raw)
    {
        if (State != SessionState.Recording || !_settings.PolishEnabled) return;
        if (raw != null && raw == _speculativePolish?.Raw) return;
        DropSpeculativePolish();
        if (raw is null || raw.Trim().Length == 0) return;
        var cancel = new CancellationTokenSource();
        var partials = new PartialRelay();
        var task = Polish(raw, partials.Send, cancel.Token);
        // A dropped clean-up's failure (usually its cancellation) is expected; observe it so it isn't reported.
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _speculativePolish = new SpeculativePolish(raw, Stopwatch.StartNew(), task, cancel, partials);
        Note("clean-up started ahead of time (speaker paused)");
    }

    private void DropSpeculativePolish()
    {
        if (_speculativePolish is not { } speculative) return;
        _speculativePolish = null;
        if (speculative.Task.IsCompletedSuccessfully) _discardedPolishes.Add(speculative.Task.Result);
        else speculative.Cancel.Cancel();
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
    /// Pastes at the cursor unless focus is clearly not a text input. Whether the paste actually landed is
    /// detected from the target app reading the clipboard: if it did, the user's previous clipboard is restored;
    /// if not, the text stays on the clipboard and the HUD says so.
    /// </summary>
    private async Task Deliver(string text, string? notice)
    {
        var target = _deliverByPaste ? await FocusProbe.FocusedTarget() : FocusProbe.Target.NotEditable;
        if (target == FocusProbe.Target.NotEditable)
        {
            Note("deliver → clipboard (focus not editable)");
            TextInserter.CopyToClipboard(text);
            Finish(notice is { } n ? new HudPhase.Error(n) : new HudPhase.Copied(), 1.6);
            return;
        }
        var outcome = await TextInserter.Insert(text, _settings.RestoreClipboard);
        Note($"deliver → {outcome} (focus {target})");
        switch (outcome)
        {
            case TextInserter.Outcome.Pasted:
                if (notice is { } message)
                {
                    Finish(new HudPhase.Error(message), 3);
                }
                else
                {
                    Reset();
                    Hud.FinishWorking();
                }
                if (_settings.LearnFromEdits) _editWatcher.Watch(text);
                break;
            default:
                Finish(new HudPhase.Copied(), 1.6);
                break;
        }
    }

    private async Task<HedgedPolishResult> Polish(string raw, Action<string>? onPartial, CancellationToken token)
    {
        if (_settings.PolishEndpoint is not { } endpoint || !_settings.IsConfigured(_settings.PolishProvider))
        {
            throw ApiException.MissingApiKey(_settings.PolishProvider.DisplayName());
        }
        var primary = Route(endpoint, _settings.PolishModel);
        var backup = _settings.PolishBackupEndpoint is { } backupEndpoint ? Route(backupEndpoint, _settings.PolishBackupModel) : null;
        return await new RetryPolicy(MaxAttempts: 2).Run((_, ct) => HedgedPolish.Run(raw, primary, backup, cancellationToken: ct, onPartial: onPartial),
                                                         cancellationToken: token);
    }

    private PolishRoute Route(ProviderEndpoint endpoint, string model)
    {
        model = model.Trim();
        return new PolishRoute(new ApiClient(endpoint), new PolishOptions(
            model,
            _settings.VocabularyList,
            _settings.ExtraInstructions,
            endpoint.Id == ProviderId.OpenRouter ? _modelInfo.GetValueOrDefault(model) : null,
            Misheard: _settings.MisheardHints));
    }

    /// <summary>
    /// Prices what this run's requests used (the speech-to-text of every chunk sent, and the clean-up answer that was
    /// kept) and adds it, with the dictation's words once it's finished, to the Home page totals.
    /// </summary>
    /// <param name="discarded">Clean-ups done ahead of time for a transcript the speaker then went on from.</param>
    private void Account(DictationRecord record, TranscriptionPipeline pipeline, HedgedPolishResult? polished,
                         IReadOnlyList<HedgedPolishResult>? discarded = null)
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
        foreach (var answer in (discarded ?? []).Append(polished))
        {
            if (answer?.Result.Usage is not { } usage) continue;
            var cost = CostEstimator.Chat(usage, prices.Price(answer.Provider, answer.Model), answer.Provider == ProviderId.OpenRouter);
            if (cost is { } c) cleanup += c; else unpriced++;
        }
        if (transcription + cleanup > 0) record.Cost = (record.Cost ?? 0) + transcription + cleanup;
        Note($"cost: transcription ${transcription:0.######}, clean-up ${cleanup:0.######}, unpriced requests {unpriced}");
        UsageStore.Shared.Record(record, transcription, cleanup, unpriced);
    }

    // MARK: - Retry / cancel

    /// <summary>Re-runs a saved dictation. Chunks that already succeeded are reused; only failed ones are re-sent.</summary>
    public void Retry(DictationRecord saved, bool paste)
    {
        if (State != SessionState.Idle) return;
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
        var record = saved.Copy();
        record.Status = DictationStatus.Processing;
        record.Error = null;
        _history.Update(record);
        _record = record;
        State = SessionState.Processing;
        Hud.ShowWorking(_settings.PolishEnabled);

        var pipeline = MakePipeline(endpoint, saved.ChunkTexts);
        _pipeline = pipeline;
        var processing = new CancellationTokenSource();
        _processing = processing;
        _ = RunRetry();

        async Task RunRetry()
        {
            // Let the menu close and focus return to the previous app before pasting.
            try { await Task.Delay(300, processing.Token); } catch (OperationCanceledException) { return; }
            pipeline.Append(samples);
            await Process(record, pipeline, processing.Token);
        }
    }

    public void Cancel(bool silently)
    {
        var wasRecording = State == SessionState.Recording;
        _processing?.Cancel();
        _pipeline?.Cancel();
        if (wasRecording)
        {
            StopCapture();
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
        State = SessionState.Idle;
        _preRollSeconds = 0;
        _pipeline = null;
        _record = null;
        _pressedAt = null;
        _handsFree = false;
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
