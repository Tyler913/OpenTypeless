using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypelessCore.Tests;

public class FileTranscriptionTests
{
    private sealed class Probe
    {
        [JsonPropertyName("source")]
        [JsonConverter(typeof(RecordSourceConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public RecordSource Source { get; set; }
    }

    [Fact]
    public void RecordSourceRoundTripsAndOlderRecordsAreDictations()
    {
        var encoded = JsonSerializer.Serialize(new Probe { Source = RecordSource.File });
        Assert.Equal("{\"source\":\"file\"}", encoded);
        Assert.Equal(RecordSource.File, JsonSerializer.Deserialize<Probe>(encoded)!.Source);
        // Saved before files could be transcribed: no key at all, and nothing is written for a dictation.
        Assert.Equal(RecordSource.Dictation, JsonSerializer.Deserialize<Probe>("{}")!.Source);
        Assert.Equal("{}", JsonSerializer.Serialize(new Probe()));
        // A source a later version adds doesn't lose the record.
        Assert.Equal(RecordSource.Dictation, JsonSerializer.Deserialize<Probe>("{\"source\":\"screen\"}")!.Source);
        Assert.Equal(RecordSource.Dictation, JsonSerializer.Deserialize<Probe>("{\"source\":{\"kind\":\"file\"}}")!.Source);
    }

    [Fact]
    public void CostEstimateIsPerMinuteOfAudio()
    {
        var price = new ModelPrice(PerMinute: 0.006);
        Assert.Equal(0.36, FileTranscription.EstimatedCost(3600, price)!.Value, 9);
        Assert.Equal(0.009, FileTranscription.EstimatedCost(90, price)!.Value, 9);
        // Nothing to go on: no price, a model billed by the token, or a length that isn't known.
        Assert.Null(FileTranscription.EstimatedCost(3600, null));
        Assert.Null(FileTranscription.EstimatedCost(3600, new ModelPrice(InputPerMillion: 2.5, OutputPerMillion: 10)));
        Assert.Null(FileTranscription.EstimatedCost(0, price));
    }

    [Fact]
    public void ProgressLabelsAndFractions()
    {
        // In whichever language the tests run in.
        Assert.Contains("3/12", new FileTranscription.Progress.Transcribing(3, 12).Label);
        Assert.Contains("40", new FileTranscription.Progress.Reading(0.404).Label);
        Assert.DoesNotContain("%", new FileTranscription.Progress.Reading(null).Label);
        Assert.Equal(0.25, new FileTranscription.Progress.Transcribing(3, 12).Fraction);
        Assert.Null(new FileTranscription.Progress.Transcribing(0, 0).Fraction);
        Assert.Equal(1, new FileTranscription.Progress.Reading(1.5).Fraction);
        Assert.Null(new FileTranscription.Progress.CleaningUp().Fraction);
        Assert.Null(new FileTranscription.Progress.Queued().Fraction);
        Assert.Equal(new FileTranscription.Progress.Transcribing(3, 12), new FileTranscription.Progress.Transcribing(3, 12));
    }

    [Fact]
    public void NoNewAudioIsHandedOutWhileDictating()
    {
        Assert.Equal(FileTranscription.MaxOutstandingChunks, FileTranscription.OutstandingLimit(dictating: false));
        Assert.Equal(0, FileTranscription.OutstandingLimit(dictating: true));
        Assert.True(FileTranscription.MaxConcurrentRequests < 3);
    }

    [Fact]
    public void DiskFullIsRecognisedThroughWrappers()
    {
        var full = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        Assert.True(FileTranscription.IsDiskFull(full));
        Assert.True(FileTranscription.IsDiskFull(new InvalidOperationException("write failed", full)));
        Assert.False(FileTranscription.IsDiskFull(new IOException("busy", unchecked((int)0x80070020))));
        Assert.Equal(FileTranscriptionException.Kind.DiskFull, FileTranscriptionException.Writing(full).Reason);
        Assert.Equal(FileTranscriptionException.Kind.WriteFailed, FileTranscriptionException.Writing(new IOException("busy")).Reason);
        Assert.Equal(FileTranscriptionException.Kind.PermissionDenied,
                     FileTranscriptionException.Reading(new UnauthorizedAccessException()).Reason);
        Assert.Equal(FileTranscriptionException.Kind.NotFound, FileTranscriptionException.Reading(new FileNotFoundException()).Reason);
        Assert.Equal(FileTranscriptionException.Kind.Unsupported, FileTranscriptionException.Reading(new InvalidDataException()).Reason);
        Assert.Equal(L("这个文件里没有音轨", "This file has no audio track"),
                     new FileTranscriptionException(FileTranscriptionException.Kind.NoAudioTrack).Message);
    }

    [Fact]
    public void GivingUpIsForRefusalsAndRunsOfFailures()
    {
        Assert.False(FileTranscription.ShouldGiveUp(new ChunkCounts(10, 2, 2, FailureStreak: 2)));
        Assert.True(FileTranscription.ShouldGiveUp(new ChunkCounts(10, 3, 0, FailureStreak: FileTranscription.MaxConsecutiveFailures)));
        Assert.True(FileTranscription.ShouldGiveUp(new ChunkCounts(0, 1, 3, Refused: 1, FailureStreak: 1)));
        Assert.True(ApiException.Http(401, "").RefusesEveryRequest);
        Assert.True(ApiException.MissingApiKey("x").RefusesEveryRequest);
        Assert.False(ApiException.Http(400, "").RefusesEveryRequest);
        Assert.False(ApiException.Http(500, "").RefusesEveryRequest);
        Assert.False(ApiException.TimedOut(30).RefusesEveryRequest);
    }

    [Fact]
    public void RoomForTheRecording()
    {
        // An hour is about 115 MB, and some to spare.
        Assert.Equal(3600L * 32_000 + 64 * 1_048_576, FileTranscription.RequiredBytes(3600));
        Assert.Equal(64L * 1_048_576, FileTranscription.RequiredBytes(0));
    }

    [Fact]
    public void InterruptedRecordsSayWhatToDo()
    {
        var reading = FileTranscription.InterruptedMessage(isFile: true, hasAudio: false);
        var retry = FileTranscription.InterruptedMessage(isFile: false, hasAudio: true);
        Assert.NotEqual(reading, retry);
        Assert.Equal(retry, FileTranscription.InterruptedMessage(isFile: true, hasAudio: true));
        Assert.Equal(retry, FileTranscription.InterruptedMessage(isFile: false, hasAudio: false));
    }

    [Fact]
    public void EveryErrorHasItsOwnMessage()
    {
        var kinds = Enum.GetValues<FileTranscriptionException.Kind>();
        var messages = kinds.Select(kind => new FileTranscriptionException(kind, "x").Message)
            .Append(new FileTranscriptionException(FileTranscriptionException.Kind.Unsupported).Message)
            .ToHashSet();
        Assert.Equal(kinds.Length + 1, messages.Count);
    }

    [Fact]
    public void ChunksDontDependOnHowTheAudioIsSplit()
    {
        var audio = Speech(95);
        int Count(int block)
        {
            var chunker = new Chunker();
            var chunks = 0;
            for (var start = 0; start < audio.Length; start += block)
            {
                chunks += chunker.Append(audio.AsSpan(start, Math.Min(block, audio.Length - start))).Count;
            }
            return chunks + (chunker.Finish() == null ? 0 : 1);
        }
        var whole = Count(audio.Length);
        Assert.True(whole > 5);
        Assert.Equal(whole, Count(AudioFormat.SampleCount(FileTranscription.BlockSeconds)));
        Assert.Equal(whole, Count(1_234));
    }

    /// <summary>A WAV put together by hand: <paramref name="chunks"/> in order, each padded to an even length as RIFF wants.</summary>
    private static byte[] Riff(params (string Id, byte[] Content)[] chunks)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes("WAVE"));
        foreach (var (id, content) in chunks)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            body.AddRange(BitConverter.GetBytes((uint)content.Length));
            body.AddRange(content);
            if (content.Length % 2 == 1) body.Add(0);
        }
        var data = new List<byte>(Encoding.ASCII.GetBytes("RIFF"));
        data.AddRange(BitConverter.GetBytes((uint)body.Count));
        data.AddRange(body);
        return data.ToArray();
    }

    private static byte[] Format(ushort tag = 1, int extra = 0)
    {
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(tag));
        data.AddRange(BitConverter.GetBytes((ushort)1));
        data.AddRange(BitConverter.GetBytes(16_000u));
        data.AddRange(BitConverter.GetBytes(32_000u));
        data.AddRange(BitConverter.GetBytes((ushort)2));
        data.AddRange(BitConverter.GetBytes((ushort)16));
        if (extra > 0)
        {
            data.AddRange(BitConverter.GetBytes((ushort)extra));
            data.AddRange(new byte[extra]);
        }
        return data.ToArray();
    }

    private static byte[] Samples(params short[] values) => values.SelectMany(value => BitConverter.GetBytes(value)).ToArray();

    private static short[] ReadAll(byte[] data)
    {
        var path = Scratch("edge.wav");
        try
        {
            File.WriteAllBytes(path, data);
            using var reader = new WavFileReader(path);
            var read = new List<short>();
            while (reader.Read(2) is { } block) read.AddRange(block);
            Assert.Equal(reader.SampleCount, read.Count);
            return read.ToArray();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void RecordingsFromOtherWritersAreReadToo()
    {
        var audio = Samples(1, -2, 3);
        short[] expected = [1, -2, 3];
        // Other chunks before the audio, one of an odd size (padded).
        Assert.Equal(expected, ReadAll(Riff(("LIST", new byte[] { 1, 2, 3 }), ("fmt ", Format()), ("JUNK", new byte[4]), ("data", audio))));
        // WAVE_FORMAT_EXTENSIBLE, as Media Foundation writes plain PCM.
        Assert.Equal(expected, ReadAll(Riff(("fmt ", Format(tag: 0xFFFE, extra: 22)), ("data", audio))));
        // Something after the audio isn't read as audio.
        Assert.Equal(expected, ReadAll(Riff(("fmt ", Format()), ("data", audio), ("LIST", new byte[] { 9, 9, 9, 9 }))));
        // An odd byte at the end of the audio is no sample.
        Assert.Equal(expected, ReadAll(Riff(("fmt ", Format()), ("data", audio.Append((byte)7).ToArray()))));
        // No audio at all.
        Assert.Empty(ReadAll(Riff(("fmt ", Format()), ("data", Array.Empty<byte>()))));
    }

    [Fact]
    public void ACutOffRecordingIsReadAsFarAsItGoes()
    {
        var data = Riff(("fmt ", Format()), ("data", Samples(5, 6, 7, 8)));
        // Says 1000 bytes of audio, has 8; then lose the last sample and a half.
        BitConverter.GetBytes(1000u).CopyTo(data, data.Length - 12);
        Assert.Equal(new short[] { 5, 6 }, ReadAll(data[..^3]));
    }

    private static string Scratch(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "opentypeless-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void RecordingIsReadBackInBlocks()
    {
        var path = Scratch("audio.wav");
        try
        {
            var samples = Enumerable.Range(0, 40_000).Select(i => unchecked((short)(i * 7))).ToArray();
            var writer = new WavFileWriter(path);
            writer.Append(samples.AsSpan(0, 25_000));
            writer.Append(samples.AsSpan(25_000));
            // Not finalized yet, as after a crash: the header still says 0 bytes, so it's read to the end of the file.
            using (var unfinished = new WavFileReader(path)) Assert.Equal(samples.Length, unfinished.SampleCount);
            writer.Close();

            using var reader = new WavFileReader(path);
            var read = new List<short>();
            var blocks = 0;
            while (reader.Read(16_000) is { } block)
            {
                Assert.True(block.Length <= 16_000);
                read.AddRange(block);
                blocks++;
            }
            Assert.Equal(samples, read);
            Assert.Equal(3, blocks);
            Assert.Null(reader.Read(16_000));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void RecordingInAnotherFormatIsRefused()
    {
        var path = Scratch("other.wav");
        try
        {
            File.WriteAllBytes(path, Wav.Encode([1, 2, 3, 4], sampleRate: 44_100));
            Assert.Throws<InvalidDataException>(() => new WavFileReader(path));
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not audio"));
            Assert.Throws<InvalidDataException>(() => new WavFileReader(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// A speech-to-text server: answers "words", or fails with <see cref="Status"/>, after the first
    /// <see cref="FailFirst"/> requests fail with 500. While <see cref="Holding"/>, requests wait until
    /// <see cref="Release"/>, so a test sees how many are out at once without timing.
    /// </summary>
    private sealed class MockServer : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _current, _peak, _requests, _failFirst;
        public int Status { get; init; } = 200;
        public bool Holding { get; init; }
        public int FailFirst { init => _failFirst = value; }

        public (int Current, int Peak, int Requests) Counts
        {
            get
            {
                lock (_lock) return (_current, _peak, _requests);
            }
        }

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int status;
            lock (_lock)
            {
                _current++;
                _requests++;
                _peak = Math.Max(_peak, _current);
                status = _failFirst > 0 ? 500 : Status;
                if (_failFirst > 0) _failFirst--;
            }
            try
            {
                if (Holding) await _released.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_lock) _current--;
            }
            var body = status == 200 ? "{\"text\":\"words\"}" : "{\"error\":{\"message\":\"no\"}}";
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
                RequestMessage = request,
            };
        }
    }

    /// <summary>Speech with a pause every 6 seconds, so the chunker cuts it into many chunks.</summary>
    private static short[] Speech(double seconds) =>
        Enumerable.Range(0, AudioFormat.SampleCount(seconds))
            .Select(i => AudioFormat.Seconds(i) % 6 < 0.5 ? (short)0 : (short)(6000 * Math.Sin(i * 0.2)))
            .ToArray();

    private static int ChunkCount(short[] audio)
    {
        var chunker = new Chunker();
        return chunker.Append(audio).Count + (chunker.Finish() == null ? 0 : 1);
    }

    private static TranscriptionPipeline Pipeline(MockServer server, int attempts = 4,
                                                  int maxConcurrentRequests = FileTranscription.MaxConcurrentRequests,
                                                  IReadOnlyDictionary<int, string>? preset = null) =>
        new(new ApiClient(ProviderEndpoint.OpenRouter("test"), new HttpClient(server)), new TranscriptionOptions("m"),
            policy: new RetryPolicy(MaxAttempts: attempts, BaseDelay: 0.01), preset: preset, speculate: false,
            maxConcurrentRequests: maxConcurrentRequests);

    /// <summary>Waits (up to 10 s) until <paramref name="condition"/> holds.</summary>
    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
    }

    /// <summary>A recording of <paramref name="audio"/> on disk, as a file's is.</summary>
    private static string Recording(short[] audio)
    {
        var path = Scratch("audio.wav");
        using var writer = new WavFileWriter(path);
        writer.Append(audio);
        writer.Close();
        return path;
    }

    [Fact]
    public async Task FileRequestsAreCappedAndCountedBeforeTheEnd()
    {
        var server = new MockServer { Holding = true };
        var pipeline = Pipeline(server);
        var audio = Speech(120);
        var total = ChunkCount(audio);

        pipeline.Append(audio);
        // Handed out and counted before Finish, unlike TranscriptionProgress.
        Assert.Null(pipeline.TranscriptionProgress());
        var counts = pipeline.ChunkCounts();
        Assert.Equal(total - 1, counts.Answered + counts.Outstanding);
        Assert.True(counts.Outstanding > FileTranscription.MaxConcurrentRequests);
        // As many as the cap are sent, and no more while they're out.
        await Eventually(() => server.Counts.Current == FileTranscription.MaxConcurrentRequests);
        await Task.Delay(100);
        Assert.Equal(FileTranscription.MaxConcurrentRequests, server.Counts.Current);
        server.Release();

        await pipeline.Finish();
        Assert.Equal(new ChunkCounts(total, 0, 0), pipeline.ChunkCounts());
        Assert.Equal(FileTranscription.MaxConcurrentRequests, server.Counts.Peak);
        Assert.Equal(total, server.Counts.Requests);
        Assert.Equal(0, pipeline.HeldChunkCount);
    }

    [Fact]
    public async Task OnlyFailedChunksKeepTheirAudioForTheLastRound()
    {
        var server = new MockServer { FailFirst = 2 };
        var pipeline = Pipeline(server, attempts: 1, preset: new Dictionary<int, string> { [0] = "kept" });
        var audio = Speech(60);
        var total = ChunkCount(audio);
        pipeline.Append(audio);
        await Eventually(() => pipeline.ChunkCounts().Outstanding == 0);
        // The preset chunk isn't sent or kept; the two that failed are kept; the rest are let go.
        var counts = pipeline.ChunkCounts();
        Assert.Equal((total - 3, 2, 0), (counts.Answered, counts.Failed, counts.Outstanding));
        Assert.Equal(2, pipeline.HeldChunkCount);

        var text = await pipeline.Finish();
        Assert.StartsWith("kept", text);
        Assert.Equal(total - 1 + 2, server.Counts.Requests);
        Assert.Equal(0, pipeline.HeldChunkCount);
    }

    [Fact]
    public async Task AFileStopsWhenTheProviderRefuses()
    {
        var server = new MockServer { Status = 401 };
        var audio = Speech(200);
        var path = Recording(audio);
        try
        {
            using var reader = new WavFileReader(path);
            await Assert.ThrowsAsync<PipelineFailure>(() => FileTranscription.Feed(reader, Pipeline(server), () => false));
            // Stopped after the first refusal, not after sending every chunk.
            Assert.True(server.Counts.Requests <= FileTranscription.MaxOutstandingChunks + 2);
            Assert.True(ChunkCount(audio) > 3 * FileTranscription.MaxOutstandingChunks);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task AFileStopsAfterARunOfFailures()
    {
        var server = new MockServer { Status = 500 };
        var audio = Speech(200);
        var path = Recording(audio);
        try
        {
            using var reader = new WavFileReader(path);
            var failure = await Assert.ThrowsAsync<PipelineFailure>(() => FileTranscription.Feed(reader, Pipeline(server, attempts: 1), () => false));
            Assert.True(failure.FailedChunks.Count >= FileTranscription.MaxConsecutiveFailures);
            Assert.True(server.Counts.Requests < ChunkCount(audio));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task ADictationNeverStopsEarly()
    {
        // The same failures, in the pipeline as a dictation uses it: every chunk is sent, and again in the last round.
        var server = new MockServer { Status = 500 };
        var audio = Speech(60);
        var total = ChunkCount(audio);
        var pipeline = Pipeline(server, attempts: 1, maxConcurrentRequests: 3);
        pipeline.Append(audio);
        await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        // At least: a slow answer on a busy machine can be raced as well.
        Assert.True(server.Counts.Requests >= 2 * total);
    }

    [Fact]
    public async Task AFileWaitsWhileDictating()
    {
        var server = new MockServer();
        var path = Recording(Speech(40));
        try
        {
            using var reader = new WavFileReader(path);
            var dictating = true;
            var feeding = Task.Run(() => FileTranscription.Feed(reader, Pipeline(server), () => Volatile.Read(ref dictating)));
            await Task.Delay(500);
            Assert.Equal(0, server.Counts.Requests);
            Volatile.Write(ref dictating, false);
            Assert.Contains("words", await feeding);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private static readonly string Transcript =
        string.Concat(Enumerable.Range(1, 60).Select(i => $"我们今天讨论了第{i}个问题，主要是关于缓存和数据库的性能。"));

    private static HedgedPolishResult Answer(string part) =>
        new(new PolishResult(part, false), "m", UsedBackup: false, FirstTokenSeconds: 0.01, TotalSeconds: 0.01);

    /// <summary>Counts the clean-up requests out at once.</summary>
    private sealed class Gauge
    {
        private readonly object _lock = new();
        private int _current, _peak, _entered;
        public int Peak { get { lock (_lock) return _peak; } }
        public int Entered { get { lock (_lock) return _entered; } }

        public void Enter()
        {
            lock (_lock)
            {
                _current++;
                _entered++;
                _peak = Math.Max(_peak, _current);
            }
        }

        public void Leave()
        {
            lock (_lock) _current--;
        }
    }

    [Fact]
    public async Task CleanUpPartsAreCapped()
    {
        async Task<(SegmentedPolishResult Outcome, int Peak)> Run(int? concurrency)
        {
            var gauge = new Gauge();
            var outcome = await SegmentedPolish.Run(Transcript, async (part, partial, token) =>
            {
                gauge.Enter();
                partial?.Invoke(part);
                await Task.Delay(5, token);
                gauge.Leave();
                return Answer(part);
            }, concurrency: concurrency);
            return (outcome, gauge.Peak);
        }
        var (all, _) = await Run(null);
        var (two, peakTwo) = await Run(2);
        var (one, peakOne) = await Run(1);
        Assert.True(all.Parts.Count >= 5);
        Assert.True(peakTwo <= 2);
        Assert.Equal(1, peakOne);
        // The same text in the same order, however many are out at once.
        Assert.Equal(all.Text, two.Text);
        Assert.Equal(all.Text, one.Text);
        Assert.Empty(one.Fallbacks);
    }

    [Fact]
    public async Task CleanUpPartsWaitingTheirTurnDontStartOnceCancelled()
    {
        var gauge = new Gauge();
        using var cancel = new CancellationTokenSource();
        var running = SegmentedPolish.Run(Transcript, async (part, _, token) =>
        {
            gauge.Enter();
            try
            {
                await Task.Delay(5000, token);
                return Answer(part);
            }
            finally
            {
                gauge.Leave();
            }
        }, cancellationToken: cancel.Token, concurrency: 1);
        await Eventually(() => gauge.Entered == 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => running);
        Assert.Equal(1, gauge.Entered);
    }

    [Fact]
    public async Task AFilesCleanUpWaitsWhileDictating()
    {
        var gauge = new Gauge();
        var dictating = true;
        var running = SegmentedPolish.Run(Transcript, (part, _, _) =>
        {
            gauge.Enter();
            gauge.Leave();
            return Task.FromResult(Answer(part));
        }, concurrency: 2, holdWhile: () => Volatile.Read(ref dictating));
        await Task.Delay(500);
        Assert.Equal(0, gauge.Entered);
        Volatile.Write(ref dictating, false);
        Assert.Empty((await running).Fallbacks);
    }
}
