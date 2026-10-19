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
/// still talking. When the user stops, only the last chunk is left to transcribe, and often not even that: when the
/// speaker pauses, the audio so far is transcribed ahead of time (a speculative tail), and if they then let go of the
/// key without saying anything more, that answer is the last chunk's.
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
    private readonly SemaphoreSlim _semaphore;
    private readonly bool _trimSilence;
    private readonly bool _speculate;
    private readonly Action<string?>? _onSpeculation;
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
    /// <summary>The last chunk, once <see cref="Finish"/> has cut it.</summary>
    private int? _tailIndex;
    /// <summary>Which route answered each chunk that was transcribed (true: the backup).</summary>
    private readonly Dictionary<int, bool> _answeredByBackup = new();

    /// <summary>How long the speaker has to be quiet before the audio so far is transcribed ahead of time.</summary>
    public const double PauseSeconds = 0.3;
    /// <summary>
    /// The end of a recording this close to the release isn't taken for speech: letting go of the key clicks, and people
    /// breathe out. Otherwise a click after the pause voided the speculative tail (it applied in 4 of 571 timed
    /// dictations), and after a chunk cut at a pause a tail of silence and a click was sent on its own.
    /// </summary>
    public const double ReleaseSeconds = 0.15;
    /// <summary>
    /// A chunk sent once the recording has ended is what the user waits for: when it holds at most this much audio, the
    /// backup route is asked at once alongside the main one, rather than once the main one is late (the backup won in 13%
    /// of 789 timed dictations, and then the text still took 1.79 s at the median, mostly the wait before asking it). A
    /// longer chunk keeps the delay, so two uploads of it don't share a slow uplink; so does a chunk sent while the user is
    /// still talking, which nothing waits on yet.
    /// </summary>
    public const double RaceSeconds = 10;
    private readonly PauseTracker _pauses = new();
    /// <summary>The audio not yet cut into a chunk, being transcribed ahead of time since the speaker paused.</summary>
    private Speculation? _speculation;
    /// <summary>The last transcript passed to <c>onSpeculation</c>, and how many changes were worked out so far.</summary>
    private string? _announced;
    private int _announcements;
    /// <summary>Calls <c>onSpeculation</c> one at a time, and how many changes it was told of (the newest one's number).</summary>
    private readonly object _announceLock = new();
    private int _announcedUpTo;

    private sealed class Speculation(AudioChunk chunk, CancellationTokenSource cancel)
    {
        /// <summary>The pending audio when the pause was noticed; it ends where the speculation's audio ends.</summary>
        public AudioChunk Chunk { get; } = chunk;
        public int End => Chunk.StartSample + Chunk.Samples.Length;
        public CancellationTokenSource Cancel { get; } = cancel;
        public Task<Outcome> Task { get; set; } = null!;
    }

    /// <summary>A chunk's request: its state, whether a failure is permanent, and whether the backup route answered.</summary>
    private readonly record struct Outcome(ChunkState State, bool Permanent, bool Backup = false);

    /// <summary>Whether the last chunk's transcript came from a speculative tail, so nothing was left to send when the recording ended.</summary>
    public bool TailWasSpeculative { get; private set; }

    /// <summary>
    /// The audio so far ends in a pause a speculative tail is already transcribing: if the recording ended now, that
    /// answer would be the last chunk's (as Finish decides it, the key's click aside). A moment more of (quiet) audio
    /// after the key is released changes nothing then.
    /// </summary>
    public bool EndsInCoveredPause
    {
        get
        {
            lock (_lock) return _speculation is { } speculation && !_pauses.SpeechAfter(speculation.End, before: ReleaseStart);
        }
    }

    /// <summary>
    /// Whether the backup route answered the last chunk that was transcribed (null when none was): the request the text
    /// waited on at the end.
    /// </summary>
    public bool? LastChunkUsedBackup
    {
        get
        {
            lock (_lock) return _answeredByBackup.Count == 0 ? null : _answeredByBackup[_answeredByBackup.Keys.Max()];
        }
    }

    /// <param name="preset">Lets a retry reuse transcripts that already succeeded (keyed by chunk index).</param>
    /// <param name="maxConcurrentRequests">
    /// How many chunks are transcribed at once (a file being transcribed asks for fewer than a dictation, see
    /// <see cref="FileTranscription"/>).
    /// </param>
    /// <param name="trimSilence">Sends each chunk without the silence before and after its speech (see <see cref="AudioChunk.Trimmed"/>).</param>
    /// <param name="speculate">Transcribes the pending audio ahead of time whenever the speaker pauses (see the class summary).</param>
    /// <param name="onSpeculation">
    /// Called, on a background thread, with the whole transcript the recording would give if it ended now, once a
    /// speculative tail and every chunk before it are transcribed; with null when that stops being true because the
    /// speaker went on. Lets the caller start cleaning the text up before the key is released. It is called one call at a
    /// time and never with a stale transcript after a newer one, so it mustn't call back into the pipeline.
    /// </param>
    public TranscriptionPipeline(ApiClient client, TranscriptionOptions options, Chunker.Config? chunkConfig = null,
                                 RetryPolicy? policy = null, IReadOnlyDictionary<int, string>? preset = null,
                                 Action<int, ChunkState>? observer = null, TranscriptionRoute? backup = null,
                                 TranscriptionLatency? latency = null, bool trimSilence = true, bool speculate = true,
                                 Action<string?>? onSpeculation = null, int maxConcurrentRequests = 3)
    {
        _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrentRequests));
        _trimSilence = trimSilence;
        _speculate = speculate;
        _onSpeculation = onSpeculation;
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
        var speculationChanged = false;
        lock (_lock)
        {
            if (_finished || _cancelled) return;
            ready = _chunker.Append(samples);
            if (_speculate)
            {
                var before = _speculation;
                _pauses.Append(samples);
                if (ready.Count > 0)
                {
                    // A chunk was cut off the pending audio the speculation covers: it no longer matches the tail.
                    DropSpeculation();
                    _pauses.Forget(_chunker.PendingStart);
                }
                UpdateSpeculation();
                speculationChanged = _speculation != before;
            }
        }
        ready.ForEach(Schedule);
        if (speculationChanged) Announce();
    }

    /// <summary>
    /// Flushes the remaining audio and waits for every chunk. Failed chunks get one more full retry
    /// round before giving up, so a brief network blip during a long dictation doesn't lose it.
    /// </summary>
    public async Task<string> Finish()
    {
        AudioChunk? tail;
        Speculation? adopted = null;
        lock (_lock)
        {
            _finished = true;
            tail = _chunker.Finish();
            _tailIndex = tail?.Index;
            if (_speculation is { } speculation)
            {
                // Only silence since the pause (the key's click aside): the speculative answer is the tail's.
                if (tail != null && tail.Index == speculation.Chunk.Index && tail.StartSample == speculation.Chunk.StartSample
                    && !_pauses.SpeechAfter(speculation.End, before: ReleaseStart))
                {
                    adopted = speculation;
                    _speculation = null;
                    Adopt(tail, speculation);
                }
                else
                {
                    DropSpeculation();
                }
            }
        }
        _recordingEnded.TrySetResult();
        if (adopted != null) _observer?.Invoke(tail!.Index, new ChunkState.Queued());
        else if (tail != null) Schedule(tail);
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

    /// <summary>
    /// How many of the chunks so far have their answer (or needed none, or were reused from the preset), and how many are
    /// handed out and still waiting for one. Unlike <see cref="TranscriptionProgress"/> it's there before
    /// <see cref="Finish"/>: a long recording read from a file is handed over a block at a time, held back while enough
    /// chunks are out (see <see cref="FileTranscription"/>).
    /// </summary>
    public (int Finished, int Outstanding) ChunkCounts()
    {
        lock (_lock)
        {
            var finished = _results.Values.Count(r => r.IsFinished);
            return (finished, _results.Count - finished);
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

    /// <summary>The recording has ended (<see cref="Finish"/> was called), so every chunk still out is holding up the text.</summary>
    private bool IsFinished
    {
        get { lock (_lock) return _finished; }
    }

    private void Schedule(AudioChunk chunk)
    {
        ChunkState? preset = null;
        lock (_lock)
        {
            if (_results.TryGetValue(chunk.Index, out var existing) && existing is ChunkState.Done) preset = existing;
            else _chunks[chunk.Index] = chunk;
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
        if (AudioLevel.IsSilent(chunk.Samples) || chunk.Duration < 0.3 || IsOnlyThePauseBeforeRelease(chunk))
        {
            _results[index] = new ChunkState.SkippedSilence();
            _chunks.Remove(index);
            Monitor.Exit(_lock);
            _observer?.Invoke(index, new ChunkState.SkippedSilence());
            return;
        }
        _results[index] = new ChunkState.Queued();
        var token = _cancellation.Token;
        var task = Task.Run(async () =>
        {
            Outcome outcome;
            try
            {
                await _semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Record(index, new Outcome(new ChunkState.Failed(ApiException.Cancelled().Message), Permanent: true));
                return;
            }
            try
            {
                outcome = await Run(chunk, token).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
            Record(index, outcome);
        });
        _tasks[index] = task;
        Monitor.Exit(_lock);
        _observer?.Invoke(index, new ChunkState.Queued());
    }

    private void Record(int index, Outcome outcome)
    {
        lock (_lock)
        {
            _results[index] = outcome.State;
            // Only a failed chunk is sent again (in Finish's last round): the audio of the others can go, which keeps an
            // hour-long file from being held in memory as it's transcribed.
            if (outcome.State is not ChunkState.Failed) _chunks.Remove(index);
            if (outcome.Permanent) _permanentFailures.Add(index); else _permanentFailures.Remove(index);
            if (outcome.State is ChunkState.Done) _answeredByBackup[index] = outcome.Backup; else _answeredByBackup.Remove(index);
        }
        _observer?.Invoke(index, outcome.State);
        Announce();
    }

    /// <summary>Where the audio stops counting as speech: <see cref="ReleaseSeconds"/> before the end of what has been appended. Under the lock.</summary>
    private int ReleaseStart => _pauses.SampleCount - AudioFormat.SampleCount(ReleaseSeconds);

    /// <summary>
    /// The last chunk of a recording that was cut at a pause, holding nothing but the rest of that pause and the key's
    /// click: nothing to send (models make text up from noise). Called under the lock.
    /// </summary>
    private bool IsOnlyThePauseBeforeRelease(AudioChunk chunk) =>
        chunk.Index > 0 && chunk.Index == _tailIndex
        && !VoiceActivity.ContainsSpeech(chunk.Samples, AudioFormat.SampleCount(ReleaseSeconds));

    // MARK: - Speculative tail

    /// <summary>
    /// After new audio: drops the speculation if the speaker has spoken since it started, and starts one when they have
    /// been quiet for <see cref="PauseSeconds"/> after speaking. Called under the lock. Sound in the last
    /// <see cref="ReleaseSeconds"/> doesn't drop it yet: it may be the key's click, which <see cref="Finish"/> leaves out too.
    /// </summary>
    private void UpdateSpeculation()
    {
        if (_speculation is { } current)
        {
            if (!_pauses.SpeechAfter(current.End, before: ReleaseStart)) return;
            DropSpeculation();
        }
        if (_pauses.LastSpeechEnd() is not { } lastSpeech
            || _pauses.SampleCount - lastSpeech < AudioFormat.SampleCount(PauseSeconds)) return;
        if (_chunker.Peek() is not { } chunk || chunk.Duration < 0.3 || AudioLevel.IsSilent(chunk.Samples)) return;
        if (_results.TryGetValue(chunk.Index, out var existing) && existing is ChunkState.Done) return;

        var speculation = new Speculation(chunk, CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token));
        var token = speculation.Cancel.Token;
        speculation.Task = Task.Run(async () =>
        {
            try
            {
                await _semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new Outcome(new ChunkState.Failed(ApiException.Cancelled().Message), Permanent: true);
            }
            try
            {
                return await Run(chunk, token, report: false).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }, CancellationToken.None);
        _speculation = speculation;
        // Queued rather than run inline, so it never runs under the lock.
        speculation.Task.ContinueWith(_ => Announce(), TaskScheduler.Default);
    }

    /// <summary>Cancels the speculation (the speaker went on). Called under the lock.</summary>
    private void DropSpeculation()
    {
        _speculation?.Cancel.Cancel();
        _speculation = null;
    }

    /// <summary>Makes the speculation's request the tail's, as if the tail had been sent when the pause began. Called under the lock.</summary>
    private void Adopt(AudioChunk tail, Speculation speculation)
    {
        TailWasSpeculative = true;
        // Retries (the final round) send the tail itself.
        _chunks[tail.Index] = tail;
        _results[tail.Index] = new ChunkState.Queued();
        _tasks[tail.Index] = Task.Run(async () =>
        {
            var outcome = await speculation.Task.ConfigureAwait(false);
            Record(tail.Index, outcome);
        });
    }

    /// <summary>Tells <c>onSpeculation</c> when the transcript the recording would give if it ended now changes.</summary>
    private void Announce()
    {
        if (_onSpeculation == null) return;
        string? text;
        int number;
        lock (_lock)
        {
            if (_finished || _cancelled) return;
            text = SpeculativeTranscript();
            if (text == _announced) return;
            _announced = text;
            number = ++_announcements;
        }
        // Changes are numbered in order under the lock, but two threads can get from there to here the other way round: a
        // change a newer one overtook is stale, and passing it on would leave the caller cleaning up the wrong text.
        lock (_announceLock)
        {
            if (number <= _announcedUpTo) return;
            _announcedUpTo = number;
            _onSpeculation(text);
        }
    }

    /// <summary>The whole transcript with the speculation as the tail, once it and every chunk before it are done. Under the lock.</summary>
    private string? SpeculativeTranscript()
    {
        if (_speculation is not { Task.IsCompletedSuccessfully: true } speculation
            || speculation.Task.Result.State is not ChunkState.Done tail) return null;
        var parts = new List<string>();
        for (var index = 0; index < speculation.Chunk.Index; index++)
        {
            switch (_results.GetValueOrDefault(index))
            {
                case ChunkState.Done done:
                    parts.Add(done.Text);
                    break;
                case ChunkState.SkippedSilence:
                    break;
                default:
                    return null;
            }
        }
        parts.Add(tail.Text);
        return TranscriptJoiner.Join(parts);
    }

    // MARK: - Requests

    /// <param name="report">False for a speculative tail: nothing is waiting on it yet, so the observer isn't told.</param>
    private async Task<Outcome> Run(AudioChunk chunk, CancellationToken token, bool report = true)
    {
        if (token.IsCancellationRequested) return new Outcome(new ChunkState.Failed(ApiException.Cancelled().Message), Permanent: true);
        if (report) _observer?.Invoke(chunk.Index, new ChunkState.Transcribing(1));
        if (_trimSilence) chunk = chunk.Trimmed();
        try
        {
            var (result, usedBackup) = await Transcribe(chunk, token, report).ConfigureAwait(false);
            if (result.Usage is { } usage)
            {
                lock (_lock) (usedBackup ? _backupUsages : _usages).Add(usage);
            }
            return new Outcome(new ChunkState.Done(result.Text), Permanent: false, Backup: usedBackup);
        }
        catch (Exception error)
        {
            var apiError = ApiException.From(error);
            return new Outcome(new ChunkState.Failed(apiError.Message), Permanent: !apiError.IsRetryable);
        }
    }

    private Task<TranscriptionResult> Attempt(ApiClient client, TranscriptionOptions options, AudioChunk chunk, CancellationToken token,
                                              bool report) =>
        client.TranscribeDetailedWithRetry(
            chunk.Samples, options, _policy,
            onRetry: (attempt, error) =>
            {
                if (Environment.GetEnvironmentVariable("OPENTYPELESS_DEBUG") != null)
                {
                    Console.Error.WriteLine($"  chunk {chunk.Index} attempt {attempt} failed: {error}");
                }
                if (report) _observer?.Invoke(chunk.Index, new ChunkState.Transcribing(attempt + 1));
            },
            cancellationToken: token);

    /// <summary>
    /// One chunk, on the primary route; with a backup route, the backup is asked too once the primary has failed, or is
    /// late (see <see cref="TranscriptionLatency"/>, and <see cref="RaceSeconds"/> for a chunk sent after the recording
    /// ended) and the recording has ended, and whichever answers first is used; the other is cancelled.
    /// </summary>
    private async Task<(TranscriptionResult Result, bool UsedBackup)> Transcribe(AudioChunk chunk, CancellationToken token, bool report)
    {
        var clock = Stopwatch.StartNew();
        if (_backup is not { } backup)
        {
            var only = await Attempt(_client, _options, chunk, token, report).ConfigureAwait(false);
            _latency.Record(clock.Elapsed.TotalSeconds, chunk.Duration);
            return (only, false);
        }
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Each on the thread pool, so neither can hold up the other (or the timer) before its first await.
        var primary = Task.Run(() => Attempt(_client, _options, chunk, race.Token, report), CancellationToken.None);
        var late = Late(HedgeDelay(chunk), race.Token);
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
                    second = Task.Run(() => Attempt(backup.Client, backup.Options, chunk, race.Token, report), CancellationToken.None);
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
        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        await _recordingEnded.Task.WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// How long the primary route has for a chunk before the backup is asked too: nothing for a short chunk sent once the
    /// recording has ended (see <see cref="RaceSeconds"/>), else until it's late.
    /// </summary>
    private double HedgeDelay(AudioChunk chunk) =>
        IsFinished && chunk.Duration <= RaceSeconds ? 0 : _latency.HedgeDelay(chunk.Duration);

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
