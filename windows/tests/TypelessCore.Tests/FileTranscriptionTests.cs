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

    /// <summary>Answers each speech-to-text request after a moment, counting how many are out at once.</summary>
    private sealed class CountingServer : HttpMessageHandler
    {
        private readonly object _lock = new();
        private int _current;
        public int Peak { get; private set; }
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _current++;
                Requests++;
                Peak = Math.Max(Peak, _current);
            }
            try
            {
                await Task.Delay(50, cancellationToken);
            }
            finally
            {
                lock (_lock) _current--;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"text\":\"words\"}")),
                RequestMessage = request,
            };
        }
    }

    /// <summary>Speech with a pause every 6 seconds, so the chunker cuts it into many chunks.</summary>
    private static short[] Speech(double seconds) =>
        Enumerable.Range(0, AudioFormat.SampleCount(seconds))
            .Select(i => AudioFormat.Seconds(i) % 6 < 0.5 ? (short)0 : (short)(6000 * Math.Sin(i * 0.2)))
            .ToArray();

    [Fact]
    public async Task FileRequestsAreCappedAndCountedBeforeTheEnd()
    {
        var server = new CountingServer();
        using var http = new HttpClient(server);
        var client = new ApiClient(ProviderEndpoint.OpenRouter("test"), http);
        var pipeline = new TranscriptionPipeline(client, new TranscriptionOptions("m"), speculate: false,
                                                 maxConcurrentRequests: FileTranscription.MaxConcurrentRequests);
        var audio = Speech(120);
        var reference = new Chunker();
        var total = reference.Append(audio).Count + (reference.Finish() == null ? 0 : 1);

        pipeline.Append(audio);
        // Handed out and counted before Finish, unlike TranscriptionProgress.
        Assert.Null(pipeline.TranscriptionProgress());
        var (finished, outstanding) = pipeline.ChunkCounts();
        Assert.Equal(total - 1, finished + outstanding);
        Assert.True(outstanding > FileTranscription.MaxConcurrentRequests);

        await pipeline.Finish();
        Assert.Equal((total, 0), pipeline.ChunkCounts());
        Assert.Equal(FileTranscription.MaxConcurrentRequests, server.Peak);
        Assert.Equal(total, server.Requests);
    }

    [Fact]
    public async Task CleanUpPartsAreCapped()
    {
        var transcript = string.Concat(Enumerable.Range(1, 60).Select(i => $"我们今天讨论了第{i}个问题，主要是关于缓存和数据库的性能。"));
        var gate = new object();
        int current = 0, peak = 0;
        var outcome = await SegmentedPolish.Run(transcript, async (part, _, token) =>
        {
            lock (gate) peak = Math.Max(peak, ++current);
            await Task.Delay(30, token);
            lock (gate) current--;
            return new HedgedPolishResult(new PolishResult(part, false), "m", UsedBackup: false, FirstTokenSeconds: 0.01, TotalSeconds: 0.03);
        }, concurrency: 2);
        Assert.True(outcome.Parts.Count >= 5);
        Assert.Empty(outcome.Fallbacks);
        Assert.Equal(2, peak);
    }
}
