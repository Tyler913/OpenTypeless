namespace TypelessCore;

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
    private bool _finished;
    private bool _cancelled;

    /// <param name="preset">Lets a retry reuse transcripts that already succeeded (keyed by chunk index).</param>
    public TranscriptionPipeline(ApiClient client, TranscriptionOptions options, Chunker.Config? chunkConfig = null,
                                 RetryPolicy? policy = null, IReadOnlyDictionary<int, string>? preset = null,
                                 Action<int, ChunkState>? observer = null)
    {
        _client = client;
        _options = options;
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
        if (tail != null) Schedule(tail);

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
            var text = await _client.TranscribeWithRetry(
                chunk.Samples, _options, _policy,
                onRetry: (attempt, error) =>
                {
                    if (Environment.GetEnvironmentVariable("OPENTYPELESS_DEBUG") != null)
                    {
                        Console.Error.WriteLine($"  chunk {chunk.Index} attempt {attempt} failed: {error}");
                    }
                    _observer?.Invoke(chunk.Index, new ChunkState.Transcribing(attempt + 1));
                },
                cancellationToken: token).ConfigureAwait(false);
            return (new ChunkState.Done(text), false);
        }
        catch (Exception error)
        {
            var apiError = ApiException.From(error);
            return (new ChunkState.Failed(apiError.Message), !apiError.IsRetryable);
        }
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
