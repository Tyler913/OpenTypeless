using System.Diagnostics;
using Microsoft.UI.Dispatching;
using OpenTypeless.Audio;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.Session;

/// <summary>
/// Transcribes audio and video files into History, one at a time, alongside dictating (see
/// <see cref="FileTranscription"/> for the rules). Each file becomes a record of its own, marked as a file: it's decoded
/// into the record's recording, then that recording goes through the pipeline a block at a time, and the transcript is
/// cleaned up when asked. Nothing is pasted; the text waits in History. A file is retried from its saved recording like a
/// dictation, reusing what was transcribed. UI-thread only, like the history store.
/// </summary>
public sealed class FileTranscriber
{
    /// <summary>A file's progress changed, or a file was queued or finished.</summary>
    public event Action? Changed;

    private readonly SessionController _controller;
    private readonly HistoryStore _history = HistoryStore.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    /// <summary>Where each file being transcribed, or waiting to be, has got to, by record ID.</summary>
    private readonly Dictionary<string, FileTranscription.Progress> _progress = new();
    private readonly List<Job> _queue = new();
    private Job? _running;
    private CancellationTokenSource? _cancel;
    /// <summary>The running file's pipeline, so quitting can save the segments it already has.</summary>
    private TranscriptionPipeline? _pipeline;
    /// <summary>
    /// Set while a dictation is under way: the file then hands out no new audio (see
    /// <see cref="FileTranscription.OutstandingLimit"/>). Read from the thread pool.
    /// </summary>
    private volatile bool _dictating;

    /// <param name="File">The file to read; null to transcribe the record's saved recording again (a retry).</param>
    /// <param name="Original">The record as it was before a retry, put back if the retry is cancelled before it starts.</param>
    private sealed record Job(string RecordId, string? File, bool CleanUp, DictationRecord? Original);

    public FileTranscriber(SessionController controller)
    {
        _controller = controller;
        _dictating = controller.State != SessionController.SessionState.Idle;
        controller.StateChanged += () => _dictating = controller.State != SessionController.SessionState.Idle;
    }

    /// <summary>How far this record's file has got; null when it isn't being transcribed or waiting to be.</summary>
    public FileTranscription.Progress? Progress(string id) => _progress.GetValueOrDefault(id);

    public bool IsActive(string id) => _progress.ContainsKey(id);

    /// <summary>Transcribes <paramref name="paths"/> one after another, each into a new History entry.</summary>
    public void Transcribe(IEnumerable<string> paths, bool cleanUp)
    {
        foreach (var path in paths)
        {
            var record = _history.Create();
            record.Status = DictationStatus.Processing;
            record.Source = RecordSource.File;
            record.SourceName = Path.GetFileName(path);
            _history.Update(record);
            _queue.Add(new Job(record.Id, path, cleanUp, null));
            _progress[record.Id] = new FileTranscription.Progress.Queued();
            AppLog.Debug("files", "queued " + record.SourceName);
        }
        Changed?.Invoke();
        StartNext();
    }

    /// <summary>Transcribes a file's saved recording again; the segments already transcribed are reused, as for a dictation.</summary>
    public void Retry(DictationRecord saved)
    {
        if (!saved.IsFile || !saved.HasAudio || IsActive(saved.Id)) return;
        var record = saved.Copy();
        record.Status = DictationStatus.Processing;
        record.Error = null;
        _history.Update(record);
        _queue.Add(new Job(record.Id, null, _settings.FileCleanUp, saved.Copy()));
        _progress[record.Id] = new FileTranscription.Progress.Queued();
        Changed?.Invoke();
        StartNext();
    }

    /// <summary>
    /// Stops a file: one waiting is taken off the queue (a new one deleted, a retry put back as it was); the one running
    /// stops where it is (see <see cref="Run"/>).
    /// </summary>
    public void Cancel(string id)
    {
        if (_running?.RecordId == id)
        {
            _cancel?.Cancel();
            return;
        }
        var index = _queue.FindIndex(j => j.RecordId == id);
        if (index < 0) return;
        var job = _queue[index];
        _queue.RemoveAt(index);
        _progress.Remove(id);
        Changed?.Invoke();
        if (_history.Records.FirstOrDefault(r => r.Id == id) is not { } record) return;
        if (job.Original is { } original) _history.Update(original);
        else _history.Delete(record);
    }

    /// <summary>
    /// Quitting: the segments the running file has are saved, so a retry at the next launch doesn't send them again. The
    /// records stay "processing" and show as interrupted then, like a dictation.
    /// </summary>
    public void StopForQuit()
    {
        if (_running is not { } running || _pipeline is not { } pipeline) return;
        if (_history.Records.FirstOrDefault(r => r.Id == running.RecordId)?.Copy() is not { } record) return;
        record.ChunkTexts = pipeline.CompletedTranscripts();
        _history.Update(record);
    }

    // MARK: - Running

    private void StartNext()
    {
        if (_running != null || _queue.Count == 0) return;
        var job = _queue[0];
        _queue.RemoveAt(0);
        _running = job;
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _ = RunJob();

        async Task RunJob()
        {
            try
            {
                await Run(job, cancel.Token);
            }
            catch (Exception error)
            {
                // Run handles what it expects; anything else mustn't leave the queue stuck.
                AppLog.Debug("files", "file job failed: " + error);
            }
            _progress.Remove(job.RecordId);
            _running = null;
            _cancel = null;
            _pipeline = null;
            Changed?.Invoke();
            StartNext();
        }
    }

    /// <summary>Moves a file's progress on, unless it has finished meanwhile.</summary>
    private void Report(string id, FileTranscription.Progress value)
    {
        if (!_progress.TryGetValue(id, out var current) || current == value) return;
        _progress[id] = value;
        Changed?.Invoke();
    }

    /// <summary>Writes the record, unless it was deleted meanwhile (Update would bring it back).</summary>
    private void Store(DictationRecord record)
    {
        if (_history.Records.Any(r => r.Id == record.Id)) _history.Update(record);
    }

    private async Task Run(Job job, CancellationToken token)
    {
        if (_history.Records.FirstOrDefault(r => r.Id == job.RecordId)?.Copy() is not { } record) return;
        if (_settings.SttEndpoint is not { } endpoint || !_settings.IsConfigured(_settings.SttProvider))
        {
            var name = _settings.SttProvider.DisplayName();
            record.Status = DictationStatus.Failed;
            record.Error = L($"请先在设置里配置语音转文字服务商（{name}）", $"Set up the speech-to-text provider ({name}) in Settings first");
            Store(record);
            return;
        }
        var id = record.Id;

        // The file goes into the record's recording first.
        int total;
        if (job.File is { } file)
        {
            Report(id, new FileTranscription.Progress.Reading(null));
            try
            {
                // Off the UI thread: opening a file can take a while (a network drive, a large video).
                var seconds = await Task.Run(() => AudioLoader.Duration(file), token);
                // Refused at once when the recording can't fit, rather than after filling the disk.
                if (FreeBytes(record.Folder) is { } free && free < FileTranscription.RequiredBytes(seconds))
                {
                    throw new FileTranscriptionException(FileTranscriptionException.Kind.DiskFull);
                }
                // The transcoder says how far it has got whether or not the length is known.
                var progress = new Throttled(fraction => _dispatcher.TryEnqueue(() => Report(id, new FileTranscription.Progress.Reading(fraction))));
                var (samples, chunks) = await Read(file, record.AudioPath, record.PartialAudioPath, progress, token);
                record.Duration = AudioFormat.Seconds(samples);
                total = chunks;
            }
            catch (Exception error)
            {
                if (token.IsCancellationRequested)
                {
                    // Nothing came of it yet.
                    AppLog.Debug("files", "cancelled while reading " + record.SourceName);
                    if (_history.Records.Any(r => r.Id == id)) _history.Delete(record);
                    return;
                }
                record.Status = DictationStatus.Failed;
                record.Error = FileTranscriptionException.Reading(error).Message;
                AppLog.Debug("files", $"can't read {record.SourceName}: {error}");
                Store(record);
                return;
            }
        }
        else
        {
            try
            {
                var audio = record.AudioPath;
                total = await Task.Run(() => Count(audio, token).Chunks);
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested)
                {
                    if (job.Original is { } original) Store(original);
                    return;
                }
                record.Status = DictationStatus.Failed;
                record.Error = L("找不到这条记录的录音文件", "The recording for this entry is gone");
                Store(record);
                return;
            }
        }

        // Saved transcripts are reused by chunk index, so only when the recording is cut into the same chunks again.
        var preset = record.ChunkLayout == Chunker.Layout ? record.ChunkTexts : new Dictionary<int, string>();
        record.ChunkTexts = new Dictionary<int, string>(preset);
        record.ChunkLayout = Chunker.Layout;
        Store(record);
        var pipeline = _controller.MakePipeline(endpoint, preset, forFile: true);
        _pipeline = pipeline;
        var started = Stopwatch.StartNew();
        Report(id, new FileTranscription.Progress.Transcribing(Math.Min(pipeline.ChunkCounts().Answered, total), total));
        // Moves the progress on as chunks come back, and saves the transcripts so far now and then.
        var watch = new CancellationTokenSource();
        _ = Watch();

        string raw;
        try
        {
            var audio = record.AudioPath;
            raw = await Task.Run(async () =>
            {
                using var reader = new WavFileReader(audio);
                return await FileTranscription.Feed(reader, pipeline, () => _dictating, token).ConfigureAwait(false);
            });
        }
        catch (Exception error)
        {
            watch.Cancel();
            record.ChunkTexts = pipeline.CompletedTranscripts();
            record.Status = DictationStatus.Failed;
            if (error is PipelineFailure failure)
            {
                record.RawText = failure.PartialText;
                record.Error = failure.Message;
            }
            else
            {
                record.RawText = TranscriptJoiner.Join(record.ChunkTexts.OrderBy(p => p.Key).Select(p => p.Value));
                record.Error = token.IsCancellationRequested || ApiException.From(error).IsCancelled
                    ? L("已取消（音频已保存，可重新转写）", "Cancelled (audio saved — you can re-transcribe it)")
                    : error.Message;
            }
            AppLog.Debug("files", "transcription stopped: " + record.Error);
            _controller.Account(record, pipeline, null, []);
            Store(record);
            return;
        }
        watch.Cancel();
        var timing = new DictationTiming { Transcription = started.Elapsed.TotalSeconds };
        record.Timing = timing;
        if (pipeline.BackupChunkCount() is var backupChunks and > 0) timing.TranscriptionBackupChunks = backupChunks;
        record.ChunkTexts = pipeline.CompletedTranscripts();
        record.RawText = raw;
        // A new transcript supersedes the clean-up of an earlier run.
        record.PolishedText = null;
        record.Error = null;

        SegmentedPolishResult? polished = null;
        if (raw.Trim().Length == 0)
        {
            record.Status = DictationStatus.Done;
            record.Error = L("没有识别到语音", "No speech detected");
        }
        else if (job.CleanUp && _settings.PolishEnabled)
        {
            Report(id, new FileTranscription.Progress.CleaningUp());
            // Saved while the clean-up runs, so the transcript survives a crash.
            Store(record);
            try
            {
                var outcome = await _controller.Polish(raw, null, token, forFile: true, holdWhile: () => _dictating);
                polished = outcome;
                timing.PolishFirstToken = outcome.FirstTokenSeconds;
                timing.Polish = outcome.TotalSeconds;
                timing.PolishModel = outcome.Model;
                timing.UsedBackup = outcome.UsedBackup;
                var fallbacks = outcome.Fallbacks;
                if (fallbacks.Count == outcome.Parts.Count)
                {
                    record.Error = fallbacks.Select(p => p.Error).FirstOrDefault(e => e != null)?.Message;
                    record.Status = DictationStatus.PolishFailed;
                }
                else
                {
                    record.PolishedText = outcome.Text;
                    record.Status = DictationStatus.Done;
                }
            }
            catch (Exception error)
            {
                // Cancelled while cleaning up: the transcript is all there, so it stays, as it was transcribed.
                record.Error = ApiException.From(error).IsCancelled ? L("已取消", "Cancelled") : error.Message;
                record.Status = DictationStatus.PolishFailed;
            }
        }
        else
        {
            record.Status = DictationStatus.Done;
        }
        AppLog.Debug("files", $"transcribed {record.SourceName}: {record.Duration:0}s, {total} segments, {record.Status}");
        _controller.Account(record, pipeline, polished, []);
        Store(record);

        async Task Watch()
        {
            var saved = Stopwatch.StartNew();
            while (!watch.IsCancellationRequested)
            {
                try { await Task.Delay(250, watch.Token); } catch (OperationCanceledException) { return; }
                Report(id, new FileTranscription.Progress.Transcribing(Math.Min(pipeline.ChunkCounts().Answered, total), total));
                if (saved.Elapsed.TotalSeconds < FileTranscription.SaveInterval) continue;
                saved.Restart();
                if (_history.Records.FirstOrDefault(r => r.Id == id)?.Copy() is { Status: DictationStatus.Processing } current)
                {
                    current.ChunkTexts = pipeline.CompletedTranscripts();
                    _history.Update(current);
                }
            }
        }
    }

    // MARK: - Off the UI thread

    /// <summary>
    /// Reads <paramref name="file"/> into a recording at <paramref name="destination"/>, written under
    /// <paramref name="partial"/> and renamed once all of it is there, so a recording only ever exists complete. Returns how
    /// many samples it holds and how many chunks the pipeline will cut it into (the chunker cuts in the same places however
    /// the audio is split into blocks).
    /// </summary>
    private static async Task<(int Samples, int Chunks)> Read(string file, string destination, string partial, IProgress<double> progress,
                                                              CancellationToken token)
    {
        try { File.Delete(partial); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try
        {
            await Task.Run(() => AudioLoader.Transcode(file, partial, progress, token), CancellationToken.None);
            (int Samples, int Chunks) counted;
            try
            {
                counted = await Task.Run(() => Count(partial, token));
            }
            catch (InvalidDataException)
            {
                throw new FileTranscriptionException(FileTranscriptionException.Kind.Unsupported);
            }
            if (counted.Samples == 0) throw new FileTranscriptionException(FileTranscriptionException.Kind.NoSound);
            try
            {
                File.Move(partial, destination, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw FileTranscriptionException.Writing(error);
            }
            return counted;
        }
        catch
        {
            await DeleteWhenFree(partial);
            throw;
        }
    }

    /// <summary>
    /// Deletes a recording being written, waiting (up to two seconds) while it's still open: a cancelled transcode lets go
    /// of its output a moment after it stops, and deleting the record's folder before then would leave the folder behind.
    /// </summary>
    private static async Task DeleteWhenFree(string path)
    {
        for (var attempt = 0; attempt < 10 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Space left on the drive holding <paramref name="folder"/>; null when Windows doesn't say (a network share).</summary>
    private static long? FreeBytes(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>How many samples a recording holds and how many chunks it's cut into, read from disk.</summary>
    private static (int Samples, int Chunks) Count(string audio, CancellationToken token)
    {
        using var reader = new WavFileReader(audio);
        var chunker = new Chunker();
        var chunks = 0;
        while (reader.Read(AudioFormat.SampleCount(FileTranscription.BlockSeconds)) is { } block)
        {
            token.ThrowIfCancellationRequested();
            chunks += chunker.Append(block).Count;
        }
        return (reader.SampleCount, chunks + (chunker.Finish() == null ? 0 : 1));
    }

    /// <summary>Passes progress on once it has moved by a percent, so the page isn't redrawn for every block.</summary>
    private sealed class Throttled(Action<double> report) : IProgress<double>
    {
        private double _reported = -1;

        public void Report(double value)
        {
            if (value - Volatile.Read(ref _reported) < 0.01) return;
            Volatile.Write(ref _reported, value);
            report(value);
        }
    }
}
