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
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly AudioRecorder _recorder = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private TranscriptionPipeline? _pipeline;
    private WavFileWriter? _writer;
    private DictationRecord? _record;
    private SampleCounter _counter = new();
    private DateTimeOffset? _pressedAt;
    private bool _handsFree;
    private CancellationTokenSource? _processing;
    private DispatcherQueueTimer? _maxDurationTimer;
    private Dictionary<string, ModelInfo> _modelInfo = new();
    /// <summary>False when retrying from the history page: the result goes to the clipboard instead.</summary>
    private bool _deliverByPaste = true;

    /// <summary>A press shorter than this toggles hands-free mode instead of push-to-talk.</summary>
    private static readonly TimeSpan TapThreshold = TimeSpan.FromSeconds(0.35);

    public SessionController()
    {
        _recorder.OnFailure = error => _dispatcher.TryEnqueue(() =>
            Abort(L("录音中断：", "Recording interrupted: ") + error.Message));
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
        Cancel(silently: false);
    }

    // MARK: - Recording

    private void StartRecording()
    {
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

        var pipeline = MakePipeline(sttEndpoint, new Dictionary<int, string>());
        var counter = new SampleCounter();
        var hudModel = Hud.Model;
        _recorder.OnSamples = samples =>
        {
            writer.Append(samples);
            pipeline.Append(samples);
            counter.Add(samples.Length);
        };
        _recorder.OnLevel = level => _dispatcher.TryEnqueue(() => hudModel.Push(level));

        try
        {
            _recorder.Start();
        }
        catch (Exception error)
        {
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
    }

    private TranscriptionPipeline MakePipeline(ProviderEndpoint endpoint, IReadOnlyDictionary<int, string> preset)
    {
        var language = _settings.SttLanguage.Length == 0 ? null : _settings.SttLanguage;
        return new TranscriptionPipeline(new ApiClient(endpoint), new TranscriptionOptions(_settings.SttModel, language), preset: preset);
    }

    private void StopCapture()
    {
        _maxDurationTimer?.Stop();
        _maxDurationTimer = null;
        _recorder.Stop();
        _recorder.OnSamples = null;
        _recorder.OnLevel = null;
        _writer?.Close();
        _writer = null;
    }

    private void FinishRecording()
    {
        if (State != SessionState.Recording || _record is not { } record || _pipeline is not { } pipeline) return;
        StopCapture();
        PlaySound(Sounds.Kind.Stop);

        record.Duration = _counter.Seconds;
        if (record.Duration < 0.4)
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
        Hud.Show(new HudPhase.Working());

        var processing = new CancellationTokenSource();
        _processing = processing;
        _ = Process(record, pipeline, processing.Token);
    }

    // MARK: - Processing

    private async Task Process(DictationRecord record, TranscriptionPipeline pipeline, CancellationToken token)
    {
        try
        {
            var raw = await pipeline.Finish();
            record.ChunkTexts = pipeline.CompletedTranscripts();
            record.RawText = raw;
            record.Error = null;
            if (raw.Trim().Length == 0)
            {
                record.Status = DictationStatus.Done;
                _history.Update(record);
                Finish(new HudPhase.Error(L("没有识别到语音", "No speech detected")), 2);
                return;
            }
            _history.Update(record);
            await PolishAndDeliver(record, token);
        }
        catch (PipelineFailure failure)
        {
            record.ChunkTexts = pipeline.CompletedTranscripts();
            record.RawText = failure.PartialText;
            record.Status = DictationStatus.Failed;
            record.Error = failure.Message;
            _history.Update(record);
            LastError = failure.Message;
            Finish(new HudPhase.Error(L("转写失败，可在托盘菜单重试", "Failed — retry from the tray menu")), 4);
        }
        catch (Exception error)
        {
            if (ApiException.From(error).IsCancelled) return;
            record.Status = DictationStatus.Failed;
            record.Error = error.Message;
            _history.Update(record);
            LastError = error.Message;
            Finish(new HudPhase.Error(error.Message), 5);
        }
    }

    private async Task PolishAndDeliver(DictationRecord record, CancellationToken token)
    {
        var text = record.RawText;
        string? notice = null;

        if (_settings.PolishEnabled)
        {
            try
            {
                var result = await Polish(record.RawText, token);
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
        }
        else
        {
            record.Status = DictationStatus.Done;
        }
        if (record.Status == DictationStatus.PolishFailed) record.PolishedText = null;
        _history.Update(record);

        if (token.IsCancellationRequested) return;
        await Deliver(text, notice);
    }

    /// <summary>
    /// Pastes at the cursor when a text input has focus; otherwise leaves the text on the clipboard.
    /// When focus can't be determined (apps without UI Automation info) it pastes *and* keeps the
    /// text on the clipboard, so nothing is lost either way.
    /// </summary>
    private async Task Deliver(string text, string? notice)
    {
        var target = _deliverByPaste ? await FocusProbe.FocusedTarget() : FocusProbe.Target.NotEditable;
        Note($"deliver → {target}");
        if (target == FocusProbe.Target.NotEditable)
        {
            TextInserter.CopyToClipboard(text);
            Finish(notice is { } n ? new HudPhase.Error(n) : new HudPhase.Copied(), 1.6);
            return;
        }
        var inserted = await TextInserter.Insert(text, restoreClipboard: target == FocusProbe.Target.Editable && _settings.RestoreClipboard);
        if (!inserted)
        {
            TextInserter.CopyToClipboard(text);
            Finish(new HudPhase.Copied(), 1.6);
        }
        else if (notice != null)
        {
            Finish(new HudPhase.Error(notice), 3);
        }
        else
        {
            Finish(new HudPhase.Hidden(), 0);
        }
    }

    private async Task<PolishResult> Polish(string raw, CancellationToken token)
    {
        if (_settings.PolishEndpoint is not { } endpoint || !_settings.IsConfigured(_settings.PolishProvider))
        {
            throw ApiException.MissingApiKey(_settings.PolishProvider.DisplayName());
        }
        var options = new PolishOptions(
            _settings.PolishModel,
            _settings.VocabularyList,
            _settings.ExtraInstructions,
            endpoint.Id == ProviderId.OpenRouter ? _modelInfo.GetValueOrDefault(_settings.PolishModel) : null);
        var client = new ApiClient(endpoint);
        return await new RetryPolicy(MaxAttempts: 2).Run((_, ct) => client.Polish(raw, options, cancellationToken: ct), cancellationToken: token);
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
        Hud.Show(new HudPhase.Working());

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
            record.Status = DictationStatus.Failed;
            record.Error = L("已取消", "Cancelled");
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
        State = SessionState.Idle;
        _pipeline = null;
        _record = null;
        _pressedAt = null;
        _handsFree = false;
        _processing = null;
    }

    // MARK: - Misc

    /// <summary>OpenRouter model metadata, used to turn reasoning off for the clean-up model.</summary>
    public void RefreshModelInfo()
    {
        if (_settings.PolishProvider != ProviderId.OpenRouter || _settings.PolishEndpoint is not { } endpoint) return;
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
