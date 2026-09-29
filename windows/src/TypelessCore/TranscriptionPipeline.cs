using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace TypelessCore;

/// <summary>A provider/model to transcribe with.</summary>
public sealed record TranscriptionRoute(ApiClient Client, TranscriptionOptions Options);

public abstract record ChunkState
{
    public sealed record Queued : ChunkState;
    public sealed record Transcribing(int Attempt) : ChunkState;
    public sealed record Done(string Text) : ChunkState;
    public sealed record SkippedSilence : ChunkState;
    public sealed record Failed(string Message) : ChunkState;

    public bool IsFinished => this is Done or SkippedSilence or Failed;
}

public sealed class PipelineFailure : Exception
{
    public string PartialText { get; }
    public IReadOnlyList<int> FailedChunks { get; }
    public string Underlying { get; }

    public PipelineFailure(string partialText, IReadOnlyList<int> failedChunks, string underlying)
    {
        PartialText = partialText;
        FailedChunks = failedChunks;
        Underlying = underlying;
    }

    public override string Message =>
        L($"有 {FailedChunks.Count} 段转写失败：{Underlying}", $"{FailedChunks.Count} segment(s) failed: {Underlying}");
}

/// <summary>
/// Streams live audio into chunks and transcribes each chunk in the background while the user is
/// still talking. When the user stops, only the last chunk is left to transcribe.
///
/// Thread-safety: <see cref="Append"/> is called from the audio thread; all mutable state is behind a lock.
/// </summary>
public sealed class TranscriptionPipeline
{
    private readonly ApiClient _client;
    private readonly TranscriptionOptions _options;
    /// <summary>Another provider/model, asked too when the primary is late for a chunk or fails (see <see cref="Transcribe"/>).</summary>
    private readonly TranscriptionRoute? _backup;
    private readonly TranscriptionLatency _latency;
    private readonly RetryPolicy _policy;
    private readonly Action<int, ChunkState>? _observer;
    private readonly SemaphoreSlim _semaphore = new(3);
    private readonly CancellationTokenSource _cancellation = new();

    private readonly object _lock = new();
    private readonly Chunker _chunker;
    private readonly Dictionary<int, AudioChunk> _chunks = new();
    private readonly Dictionary<int, ChunkState> _results = new();
    /// <summary>Chunks whose last failure was permanent (bad key, no credit…): not worth another round.</summary>
    private readonly HashSet<int> _permanentFailures = new();
    private readonly Dictionary<int, Task> _tasks = new();
    /// <summary>Usage of every successful request, retries of the same chunk included (each one was billed).</summary>
    private readonly List<RequestUsage> _usages = new();
    private readonly List<RequestUsage> _backupUsages = new();
    private bool _finished;
    /// <summary>Every chunk has been handed out: <see cref="Finish"/> has scheduled the tail.</summary>
    private bool _flushed;
    private bool _cancelled;
    /// <summary>Completed by <see cref="Finish"/>: the recording has ended, so every chunk still out is holding up the text.</summary>
    private readonly TaskCompletionSource _recordingEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="preset">Lets a retry reuse transcripts that already succeeded (keyed by chunk index).</param>
    public TranscriptionPipeline(ApiClient client, TranscriptionOptions options, Chunker.Config? chunkConfig = null,
                                 RetryPolicy? policy = null, IReadOnlyDictionary<int, string>? preset = null,
                                 Action<int, ChunkState>? observer = null, TranscriptionRoute? backup = null,
                                 TranscriptionLatency? latency = null)
    {
        _client = client;
        _options = options;
        _backup = backup;
        _latency = latency ?? new TranscriptionLatency();
        _policy = policy ?? new RetryPolicy();
        _observer = observer;
        _chunker = new Chunker(chunkConfig);
        if (preset != null)
        {
            foreach (var (index, text) in preset) _results[index] = new ChunkState.Done(text);
        }
    }

    public void Append(ReadOnlySpan<short> samples)
    {
        List<AudioChunk> ready;
        lock (_lock)
        {
            if (_finished || _cancelled) return;
            ready = _chunker.Append(samples);
        }
        ready.ForEach(Schedule);
    }

    /// <summary>
    /// Flushes the remaining audio and waits for every chunk. Failed chunks get one more full retry
    /// round before giving up, so a brief network blip during a long dictation doesn't lose it.
    /// </summary>
    public async Task<string> Finish()
    {
        AudioChunk? tail;
        lock (_lock)
        {
            _finished = true;
            tail = _chunker.Finish();
        }
        _recordingEnded.TrySetResult();
        if (tail != null) Schedule(tail);
        lock (_lock) _flushed = true;

        await WaitForAll().ConfigureAwait(false);

        var failed = FailedIndices().Where(index => { lock (_lock) return !_permanentFailures.Contains(index); }).ToList();
        if (failed.Count > 0 && !IsCancelled)
        {
            foreach (var index in failed) Restart(index);
            await WaitForAll().ConfigureAwait(false);
        }
        if (IsCancelled) throw ApiException.Cancelled();

        var (text, stillFailed, lastError) = Snapshot();
        if (stillFailed.Count > 0) throw new PipelineFailure(text, stillFailed, lastError ?? "unknown");
        return text;
    }

    public void Cancel()
    {
        lock (_lock) _cancelled = true;
        _cancellation.Cancel();
    }

    /// <summary>Per-chunk transcripts so far (for saving progress / history).</summary>
    public Dictionary<int, string> CompletedTranscripts()
    {
        lock (_lock)
        {
            return _results.Where(r => r.Value is ChunkState.Done).ToDictionary(r => r.Key, r => ((ChunkState.Done)r.Value).Text);
        }
    }

    /// <summary>
    /// How many chunks have their answer (or needed none), of how many in all; null until <see cref="Finish"/> has
    /// handed out the last one, since the total isn't known before.
    /// </summary>
    public (int Done, int Total)? TranscriptionProgress()
    {
        lock (_lock)
        {
            if (!_flushed) return null;
            return (_results.Values.Count(r => r.IsFinished), _results.Count);
        }
    }

    /// <summary>What the requests so far used, one entry per successful request.</summary>
    public List<RequestUsage> Usages()
    {
        lock (_lock) return new List<RequestUsage>(_usages);
    }

    /// <summary>Usage of the requests the backup route answered (priced with the backup's model).</summary>
    public List<RequestUsage> BackupUsages()
    {
        lock (_lock) return new List<RequestUsage>(_backupUsages);
    }

    /// <summary>How many chunks the backup route transcribed.</summary>
    public int BackupChunkCount()
    {
        lock (_lock) return _backupUsages.Count;
    }

    // MARK: - Private

    private bool IsCancelled
    {
        get { lock (_lock) return _cancelled; }
    }

    private void Schedule(AudioChunk chunk)
    {
        ChunkState? preset = null;
        lock (_lock)
        {
            _chunks[chunk.Index] = chunk;
            if (_results.TryGetValue(chunk.Index, out var existing) && existing is ChunkState.Done) preset = existing;
        }
        if (preset != null)
        {
            _observer?.Invoke(chunk.Index, preset);
            return;
        }
        Restart(chunk.Index);
    }

    private void Restart(int index)
    {
        Monitor.Enter(_lock);
        if (!_chunks.TryGetValue(index, out var chunk))
        {
            Monitor.Exit(_lock);
            return;
        }
        if (AudioLevel.IsSilent(chunk.Samples) || chunk.Duration < 0.3)
        {
            _results[index] = new ChunkState.SkippedSilence();
            Monitor.Exit(_lock);
            _observer?.Invoke(index, new ChunkState.SkippedSilence());
            return;
        }
        _results[index] = new ChunkState.Queued();
        var token = _cancellation.Token;
        var task = Task.Run(async () =>
        {
            ChunkState state;
            bool permanent;
            try
            {
                await _semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Record(index, new ChunkState.Failed(ApiException.Cancelled().Message), permanent: true);
                return;
            }
            try
            {
                (state, permanent) = await Run(chunk, token).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
            Record(index, state, permanent);
        });
        _tasks[index] = task;
        Monitor.Exit(_lock);
        _observer?.Invoke(index, new ChunkState.Queued());
    }

    private void Record(int index, ChunkState state, bool permanent)
    {
        lock (_lock)
        {
            _results[index] = state;
            if (permanent) _permanentFailures.Add(index); else _permanentFailures.Remove(index);
        }
        _observer?.Invoke(index, state);
    }

    private async Task<(ChunkState, bool Permanent)> Run(AudioChunk chunk, CancellationToken token)
    {
        if (token.IsCancellationRequested) return (new ChunkState.Failed(ApiException.Cancelled().Message), true);
        _observer?.Invoke(chunk.Index, new ChunkState.Transcribing(1));
        try
        {
            var (result, usedBackup) = await Transcribe(chunk, token).ConfigureAwait(false);
            if (result.Usage is { } usage)
            {
                lock (_lock) (usedBackup ? _backupUsages : _usages).Add(usage);
            }
            return (new ChunkState.Done(result.Text), false);
        }
        catch (Exception error)
        {
            var apiError = ApiException.From(error);
            return (new ChunkState.Failed(apiError.Message), !apiError.IsRetryable);
        }
    }

    private Task<TranscriptionResult> Attempt(ApiClient client, TranscriptionOptions options, AudioChunk chunk, CancellationToken token) =>
        client.TranscribeDetailedWithRetry(
            chunk.Samples, options, _policy,
            onRetry: (attempt, error) =>
            {
                if (Environment.GetEnvironmentVariable("OPENTYPELESS_DEBUG") != null)
                {
                    Console.Error.WriteLine($"  chunk {chunk.Index} attempt {attempt} failed: {error}");
                }
                _observer?.Invoke(chunk.Index, new ChunkState.Transcribing(attempt + 1));
            },
            cancellationToken: token);

    /// <summary>
    /// One chunk, on the primary route; with a backup route, the backup is asked too once the primary has failed, or is
    /// late (see <see cref="TranscriptionLatency"/>) and the recording has ended, and whichever answers first is used;
    /// the other is cancelled.
    /// </summary>
    private async Task<(TranscriptionResult Result, bool UsedBackup)> Transcribe(AudioChunk chunk, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        if (_backup is not { } backup)
        {
            var only = await Attempt(_client, _options, chunk, token).ConfigureAwait(false);
            _latency.Record(clock.Elapsed.TotalSeconds, chunk.Duration);
            return (only, false);
        }
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Each on the thread pool, so neither can hold up the other (or the timer) before its first await.
        var primary = Task.Run(() => Attempt(_client, _options, chunk, race.Token), CancellationToken.None);
        var late = Late(_latency.HedgeDelay(chunk.Duration), race.Token);
        Task<TranscriptionResult>? second = null;
        Exception? primaryError = null;
        var backupFailed = false;
        var pending = new List<Task> { primary, late };
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var askBackup = false;
                if (done == late)
                {
                    askBackup = primaryError == null && !late.IsCanceled;
                }
                else if (done == primary)
                {
                    if (primary.IsCompletedSuccessfully)
                    {
                        _latency.Record(clock.Elapsed.TotalSeconds, chunk.Duration);
                        return (primary.Result, false);
                    }
                    var error = primary.Exception?.InnerException ?? ApiException.Cancelled();
                    if (ApiException.From(error).IsCancelled || backupFailed) ExceptionDispatchInfo.Throw(error);
                    primaryError = error;
                    askBackup = true;
                }
                else if (done == second)
                {
                    if (second.IsCompletedSuccessfully) return (second.Result, true);
                    backupFailed = true;
                    if (primaryError != null) ExceptionDispatchInfo.Throw(primaryError);
                }
                if (askBackup && second == null)
                {
                    second = Task.Run(() => Attempt(backup.Client, backup.Options, chunk, race.Token), CancellationToken.None);
                    pending.Add(second);
                }
            }
            throw primaryError ?? ApiException.Cancelled();
        }
        finally
        {
            // Stops whichever request lost, and the timer.
            race.Cancel();
        }
    }

    /// <summary>
    /// Completes once a chunk has been out for <paramref name="seconds"/> and the recording has ended. While the user
    /// is still talking nothing waits on the chunk yet, so the backup isn't worth paying for; once the recording ends,
    /// a chunk that is late by then is asked of the backup at once.
    /// </summary>
    private async Task Late(double seconds, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        await _recordingEnded.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task WaitForAll()
    {
        while (true)
        {
            List<Task> pending;
            lock (_lock)
            {
                pending = _tasks.Where(t => !(_results.TryGetValue(t.Key, out var r) && r.IsFinished)).Select(t => t.Value).ToList();
            }
            if (pending.Count == 0) return;
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch
            {
                // Chunk tasks record their own failures; nothing escapes them in normal operation.
            }
            lock (_lock)
            {
                // A task that ended without recording a result (e.g. an observer threw) counts as a transient failure.
                foreach (var (index, task) in _tasks)
                {
                    if (task.IsCompleted && !(_results.TryGetValue(index, out var r) && r.IsFinished))
                    {
                        _results[index] = new ChunkState.Failed(task.Exception?.InnerException?.Message ?? "unknown");
                    }
                }
            }
        }
    }

    private List<int> FailedIndices()
    {
        lock (_lock)
        {
            return _results.Where(r => r.Value is ChunkState.Failed).Select(r => r.Key).Order().ToList();
        }
    }

    private (string Text, List<int> Failed, string? LastError) Snapshot()
    {
        lock (_lock)
        {
            var parts = new List<string>();
            var failed = new List<int>();
            string? lastError = null;
            foreach (var index in _results.Keys.Order())
            {
                switch (_results[index])
                {
                    case ChunkState.Done done:
                        parts.Add(done.Text);
                        break;
                    case ChunkState.Failed f:
                        failed.Add(index);
                        lastError = f.Message;
                        break;
                }
            }
            return (TranscriptJoiner.Join(parts), failed, lastError);
        }
    }
}
