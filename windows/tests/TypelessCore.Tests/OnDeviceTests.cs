using System.Security.Cryptography;
using System.Text;

namespace TypelessCore.Tests;

/// <summary>On-device speech-to-text: the engine setting, setup rules, the transcriber as a pipeline route, and $0 spend.</summary>
public class OnDeviceTests
{
    /// <summary>A model that answers with how much audio it got, in the language it was asked for.</summary>
    private sealed class FakeModel : ILocalSpeechModel
    {
        private int _running;
        public int MostAtOnce;
        public readonly List<string> Languages = [];
        public bool Disposed;
        public Func<float[], string>? Answer;

        public string Recognize(float[] samples, string language)
        {
            var now = Interlocked.Increment(ref _running);
            lock (Languages)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
                Languages.Add(language);
            }
            Thread.Sleep(5);
            Interlocked.Decrement(ref _running);
            return Answer?.Invoke(samples) ?? $" heard {samples.Length} ";
        }

        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(null, SpeechEngine.Provider)]
    [InlineData("", SpeechEngine.Provider)]
    [InlineData("provider", SpeechEngine.Provider)]
    [InlineData("whisper", SpeechEngine.Provider)]
    [InlineData("onDevice", SpeechEngine.OnDevice)]
    [InlineData(" onDevice ", SpeechEngine.OnDevice)]
    public void EngineIsTheProviderUnlessSetToOnDevice(string? raw, SpeechEngine expected)
    {
        Assert.Equal(expected, SpeechEngines.Parse(raw));
    }

    [Fact]
    public void EngineValuesMatchTheMacApp()
    {
        Assert.Equal("provider", SpeechEngine.Provider.RawValue());
        Assert.Equal("onDevice", SpeechEngine.OnDevice.RawValue());
        foreach (var engine in Enum.GetValues<SpeechEngine>()) Assert.Equal(engine, SpeechEngines.Parse(engine.RawValue()));
    }

    [Fact]
    public void OnDeviceNeedsTheModelNotAKey()
    {
        // No key at all: on-device is ready once the model is there, and only then.
        Assert.True(SpeechSetup.SpeechReady(SpeechEngine.OnDevice, providerConfigured: false, modelReady: true));
        Assert.False(SpeechSetup.SpeechReady(SpeechEngine.OnDevice, providerConfigured: true, modelReady: false));
        // The provider needs its key, whatever the model.
        Assert.True(SpeechSetup.SpeechReady(SpeechEngine.Provider, providerConfigured: true, modelReady: false));
        Assert.False(SpeechSetup.SpeechReady(SpeechEngine.Provider, providerConfigured: false, modelReady: true));
    }

    [Fact]
    public void OnDeviceWithNoCleanUpKeyGoesInAsSpoken()
    {
        Assert.False(SpeechSetup.CleansUp(polishEnabled: true, SpeechEngine.OnDevice, polishConfigured: false));
        Assert.True(SpeechSetup.CleansUp(polishEnabled: true, SpeechEngine.OnDevice, polishConfigured: true));
        Assert.False(SpeechSetup.CleansUp(polishEnabled: false, SpeechEngine.OnDevice, polishConfigured: true));
        // With a provider, a missing clean-up key still fails the clean-up visibly, as before.
        Assert.True(SpeechSetup.CleansUp(polishEnabled: true, SpeechEngine.Provider, polishConfigured: false));
        Assert.False(SpeechSetup.CleansUp(polishEnabled: false, SpeechEngine.Provider, polishConfigured: true));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("auto", "")]
    [InlineData("zh", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-HK", "yue")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("EN_gb", "en")]
    [InlineData("ja", "ja")]
    [InlineData("ko", "ko")]
    [InlineData("yue", "yue")]
    [InlineData("fr", null)]
    [InlineData("de", null)]
    [InlineData("ru", null)]
    public void LanguageHintsMapToTheModel(string? hint, string? expected)
    {
        Assert.Equal(expected, OnDeviceLanguages.ModelLanguage(hint));
    }

    [Fact]
    public void EverySpokenLanguageInSettingsIsKnownOrRefused()
    {
        var known = Localization.Supported.Select(l => l.RawValue()).Where(code => OnDeviceLanguages.ModelLanguage(code) != null).ToList();
        Assert.Equal(["zh", "en", "ja", "ko"], known);
    }

    [Fact]
    public void AnAppProfilesLanguageDecidesTheModelsLanguage()
    {
        var defaults = new DictationSettings(PolishEnabled: true, PressEnter: false, Language: "", ExtraInstructions: "");
        Assert.Equal("", OnDeviceLanguages.ModelLanguage(defaults.Language));
        var japanese = defaults.Applying(new AppProfile { App = "line.exe", Name = "LINE", Language = "ja" }, "line.exe");
        Assert.Equal("ja", OnDeviceLanguages.ModelLanguage(japanese.Language));
        var french = defaults.Applying(new AppProfile { App = "word.exe", Name = "Word", Language = "fr" }, "word.exe");
        Assert.Null(OnDeviceLanguages.ModelLanguage(french.Language));
        // A profile that follows the global language keeps it.
        var following = (defaults with { Language = "ko" }).Applying(new AppProfile { App = "x.exe", Name = "X" }, "x.exe");
        Assert.Equal("ko", OnDeviceLanguages.ModelLanguage(following.Language));
    }

    [Fact]
    public void SamplesBecomeFloatsFromMinusOneToOne()
    {
        Assert.Equal(new[] { -1f, 0f, 0.5f }, OnDeviceTranscriber.Floats([short.MinValue, 0, 16384]));
    }

    [Fact]
    public async Task ADictationIsTranscribedOnDeviceAndCostsNothing()
    {
        var model = new FakeModel();
        var loads = 0;
        var transcriber = new OnDeviceTranscriber(() =>
        {
            loads++;
            return model;
        });
        var states = new List<(int, ChunkState)>();
        var pipeline = new TranscriptionPipeline(transcriber, OnDeviceTranscriber.Options("en-US"),
                                                 observer: (index, state) => { lock (states) states.Add((index, state)); });
        var audio = ChunkerTests.Signal(40, [10.0, 21.0, 32.0]);
        var block = AudioFormat.SampleCount(0.1);
        for (var i = 0; i < audio.Length; i += block) pipeline.Append(audio.AsSpan(i, Math.Min(block, audio.Length - i)));
        var text = await pipeline.Finish();

        var chunks = pipeline.CompletedTranscripts();
        Assert.True(chunks.Count >= 2, $"{chunks.Count} chunks");
        Assert.Equal(TranscriptJoiner.Join(chunks.OrderBy(c => c.Key).Select(c => c.Value)), text);
        Assert.All(chunks.Values, chunk => Assert.StartsWith("heard ", chunk)); // trimmed
        Assert.All(model.Languages, language => Assert.Equal("en", language));
        // Loaded once, and one recognition at a time even with three chunks out at once.
        Assert.Equal(1, loads);
        Assert.Equal(1, model.MostAtOnce);
        Assert.True(transcriber.Recognitions >= chunks.Count);
        // Nothing billed: no usage, so the transcription costs $0 at any price, and no backup route was asked.
        Assert.Empty(pipeline.Usages());
        Assert.Empty(pipeline.BackupUsages());
        Assert.Equal((0.0, 0), CostEstimator.Transcriptions(pipeline.Usages(), new ModelPrice(PerMinute: 0.006), preferReported: true));
        Assert.False(pipeline.LastChunkUsedBackup ?? false);
    }

    [Fact]
    public async Task ALanguageTheModelDoesntKnowFailsAtOnceAndKeepsTheDictation()
    {
        var model = new FakeModel();
        var pipeline = new TranscriptionPipeline(new OnDeviceTranscriber(() => model), OnDeviceTranscriber.Options("fr"));
        pipeline.Append(ChunkerTests.Signal(12, [6.0]));
        var failure = await Assert.ThrowsAsync<PipelineFailure>(() => pipeline.Finish());
        Assert.Equal(ApiException.UnsupportedLanguage().Message, failure.Underlying);
        Assert.NotEmpty(failure.FailedChunks);
        Assert.Equal("", failure.PartialText);
        // Never sent to the model, and not retried: it would fail the same way.
        Assert.Empty(model.Languages);
        Assert.False(ApiException.UnsupportedLanguage().IsRetryable);
        Assert.True(ApiException.UnsupportedLanguage().RefusesEveryRequest);
    }

    [Fact]
    public async Task AMissingModelIsAClearPermanentFailure()
    {
        var transcriber = new OnDeviceTranscriber(() => throw ApiException.ModelMissing());
        var error = await Assert.ThrowsAsync<ApiException>(() => transcriber.TranscribeDetailedWithRetry(new short[16000], OnDeviceTranscriber.Options(null)));
        Assert.Equal(ApiErrorKind.ModelMissing, error.Kind);
        Assert.False(error.IsRetryable);

        // Anything else the runtime throws is reported as on-device recognition failing.
        var broken = new OnDeviceTranscriber(() => throw new DllNotFoundException("sherpa-onnx-c-api"));
        var failed = await Assert.ThrowsAsync<ApiException>(() => broken.TranscribeDetailedWithRetry(new short[16000], OnDeviceTranscriber.Options(null)));
        Assert.Equal(ApiErrorKind.OnDevice, failed.Kind);
        Assert.Contains("sherpa-onnx-c-api", failed.Message);
        Assert.False(failed.IsRetryable);
    }

    [Fact]
    public async Task UnloadingFreesTheModelUntilItsNeededAgain()
    {
        var models = new List<FakeModel>();
        var transcriber = new OnDeviceTranscriber(() =>
        {
            var model = new FakeModel();
            models.Add(model);
            return model;
        });
        Assert.False(transcriber.IsLoaded);
        var result = await transcriber.TranscribeDetailedWithRetry(new short[1600], OnDeviceTranscriber.Options(null));
        Assert.Equal("heard 1600", result.Text);
        Assert.Null(result.Usage);
        Assert.True(transcriber.IsLoaded);
        await transcriber.Unload();
        Assert.False(transcriber.IsLoaded);
        Assert.True(models[0].Disposed);
        await transcriber.TranscribeDetailedWithRetry(new short[1600], OnDeviceTranscriber.Options("zh"));
        Assert.Equal(2, models.Count);
        Assert.Equal(["zh"], models[1].Languages);
    }

    [Fact]
    public async Task ACancelledChunkIsNotRecognised()
    {
        var model = new FakeModel();
        var transcriber = new OnDeviceTranscriber(() => model);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<ApiException>(() => transcriber.TranscribeDetailedWithRetry(new short[1600], OnDeviceTranscriber.Options(null),
                                                                                                         cancellationToken: cancel.Token));
        Assert.True(error.IsCancelled);
        Assert.Empty(model.Languages);
    }

    [Fact]
    public async Task WarmingLoadsTheModelAhead()
    {
        var loaded = new TaskCompletionSource();
        var transcriber = new OnDeviceTranscriber(() =>
        {
            loaded.TrySetResult();
            return new FakeModel();
        });
        transcriber.Warm();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100 && !transcriber.IsLoaded; i++) await Task.Delay(10);
        Assert.True(transcriber.IsLoaded);
    }
}

/// <summary>The speech model's download: pinned checksums, resuming, sources in turn, free space, cancelling and removing.</summary>
public sealed class ModelStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opentypeless-model-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Weights = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 % 251)).ToArray();
    private static readonly byte[] Tokens = Encoding.UTF8.GetBytes("<unk> 0\n<s> 1\n</s> 2\n");

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static SpeechModelInfo Model(byte[]? weights = null) => new("test-model", "Test model",
    [
        File("model.onnx", weights ?? Weights),
        File("tokens.txt", Tokens),
    ]);

    private static ModelFile File(string name, byte[] data) => new(name, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)),
        [new Uri($"https://first.example/{name}"), new Uri($"https://second.example/{name}")]);

    /// <summary>Serves the test files by name; can fail a host, ignore Range, or hold the body back.</summary>
    private sealed class FakeFetcher(Dictionary<string, byte[]> files) : IModelFetcher
    {
        public readonly List<(string Host, string Name, long Offset)> Requests = [];
        public string? DownHost;
        public bool IgnoresRange;
        public Func<Stream, Stream>? Wrap;

        public Task<(Stream Body, long Start)> Open(Uri url, long offset, CancellationToken cancellationToken)
        {
            var name = url.Segments[^1];
            lock (Requests) Requests.Add((url.Host, name, offset));
            if (url.Host == DownHost) throw new HttpRequestException("no route to host");
            var data = files[name];
            var start = IgnoresRange ? 0 : (int)offset;
            Stream body = new MemoryStream(data, start, data.Length - start);
            return Task.FromResult((Wrap?.Invoke(body) ?? body, (long)start));
        }
    }

    /// <summary>Hands over a little, then waits until the read is cancelled; <see cref="Waiting"/> once the first part is written.</summary>
    private sealed class StallingStream(Stream inner, int first) : Stream
    {
        private int _reads;
        public readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ == 0) return await inner.ReadAsync(buffer[..Math.Min(first, buffer.Length)], cancellationToken);
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private FakeFetcher Fetcher(byte[]? weights = null) => new(new() { ["model.onnx"] = weights ?? Weights, ["tokens.txt"] = Tokens });

    [Fact]
    public async Task DownloadsChecksAndRemembersTheModel()
    {
        var fetcher = Fetcher();
        var store = new ModelStore(Model(), _folder, fetcher, _ => long.MaxValue);
        Assert.IsType<ModelState.Missing>(store.State);
        Assert.Equal(Weights.Length + Tokens.Length, store.RemainingBytes());
        var percents = new List<int>();
        store.Changed += () =>
        {
            if (store.State is ModelState.Downloading d) lock (percents) percents.Add(d.Percent);
        };
        await store.Download();

        Assert.IsType<ModelState.Ready>(store.State);
        Assert.Equal(Weights, System.IO.File.ReadAllBytes(store.PathOf("model.onnx")));
        Assert.Equal(Tokens, System.IO.File.ReadAllBytes(store.PathOf("tokens.txt")));
        Assert.False(System.IO.File.Exists(store.PathOf("model.onnx") + ".partial"));
        Assert.Equal(0, store.RemainingBytes());
        Assert.Contains(100, percents);
        Assert.All(fetcher.Requests, r => Assert.Equal("first.example", r.Host));
        // The next launch sees it's ready without downloading or hashing again.
        Assert.IsType<ModelState.Ready>(new ModelStore(Model(), _folder, Fetcher(), _ => long.MaxValue).State);
        // A model with other hashes in the same folder isn't taken for it.
        Assert.IsType<ModelState.Missing>(new ModelStore(Model(Weights.Reverse().ToArray()), _folder, Fetcher(), _ => long.MaxValue).State);
    }

    [Fact]
    public async Task ADamagedDownloadIsThrownAwayAndStartsOver()
    {
        var damaged = (byte[])Weights.Clone();
        damaged[1234] ^= 0xFF;
        var store = new ModelStore(Model(), _folder, Fetcher(damaged), _ => long.MaxValue);
        await store.Download();
        var failed = Assert.IsType<ModelState.Failed>(store.State);
        Assert.Equal(L("下载的文件已损坏（校验和不符），请重试", "The download was damaged (checksum mismatch). Try again."), failed.Message);
        Assert.False(System.IO.File.Exists(store.PathOf("model.onnx") + ".partial"));
        Assert.False(System.IO.File.Exists(store.PathOf("model.onnx")));

        // Tried again (the source is fine now): from the start, not from the damaged bytes.
        var fetcher = Fetcher();
        var again = new ModelStore(Model(), _folder, fetcher, _ => long.MaxValue);
        await again.Download();
        Assert.IsType<ModelState.Ready>(again.State);
        Assert.Contains(("first.example", "model.onnx", 0L), fetcher.Requests);
    }

    [Fact]
    public async Task AnInterruptedDownloadResumesWhereItStopped()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllBytes(Path.Combine(_folder, "model.onnx.partial"), Weights[..120_000]);
        var fetcher = Fetcher();
        var store = new ModelStore(Model(), _folder, fetcher, _ => long.MaxValue);
        Assert.Equal(Weights.Length - 120_000 + Tokens.Length, store.RemainingBytes());
        await store.Download();
        Assert.IsType<ModelState.Ready>(store.State);
        Assert.Contains(("first.example", "model.onnx", 120_000L), fetcher.Requests);
        Assert.Equal(Weights, System.IO.File.ReadAllBytes(store.PathOf("model.onnx")));
    }

    [Fact]
    public async Task ASourceThatSendsTheWholeFileOverwritesThePartOnDisk()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllBytes(Path.Combine(_folder, "model.onnx.partial"), Weights[..50_000]);
        var fetcher = Fetcher();
        fetcher.IgnoresRange = true;
        var store = new ModelStore(Model(), _folder, fetcher, _ => long.MaxValue);
        await store.Download();
        Assert.IsType<ModelState.Ready>(store.State);
        Assert.Equal(Weights, System.IO.File.ReadAllBytes(store.PathOf("model.onnx")));
    }

    [Fact]
    public async Task TheNextSourceIsTriedWhenOneIsDown()
    {
        var fetcher = Fetcher();
        fetcher.DownHost = "first.example";
        var store = new ModelStore(Model(), _folder, fetcher, _ => long.MaxValue);
        await store.Download();
        Assert.IsType<ModelState.Ready>(store.State);
        Assert.Contains(fetcher.Requests, r => r.Host == "second.example" && r.Name == "model.onnx");
    }

    [Fact]
    public async Task EverySourceDownIsAFailureToRetry()
    {
        var fetcher = Fetcher();
        fetcher.DownHost = "first.example";
        var model = Model() with { Files = Model().Files.Select(f => f with { Urls = [f.Urls[0]] }).ToList() };
        var store = new ModelStore(model, _folder, fetcher, _ => long.MaxValue);
        await store.Download();
        var failed = Assert.IsType<ModelState.Failed>(store.State);
        Assert.Contains("no route to host", failed.Message);
        Assert.False(store.IsReady);
    }

    [Fact]
    public async Task NotEnoughSpaceStopsBeforeDownloading()
    {
        var fetcher = Fetcher();
        var store = new ModelStore(Model(), _folder, fetcher, _ => 10_000_000);
        await store.Download();
        var failed = Assert.IsType<ModelState.Failed>(store.State);
        Assert.Contains("MB", failed.Message);
        Assert.Empty(fetcher.Requests);
        // The space it asks for is the download plus a margin, so 10 MB isn't enough for a 0.3 MB model.
        Assert.True(ModelStore.SpareBytes > 10_000_000);
    }

    [Fact]
    public async Task CancellingKeepsWhatCameInForTheNextTry()
    {
        var fetcher = Fetcher();
        StallingStream? stalling = null;
        fetcher.Wrap = body => stalling = new StallingStream(body, 40_000);
        var store = new ModelStore(Model(), _folder, fetcher, _ => long.MaxValue);
        var download = store.Download();
        Assert.True(store.IsDownloading);
        // Asking again while it runs doesn't start a second one.
        Assert.Same(download, store.Download());
        for (var i = 0; i < 500 && stalling == null; i++) await Task.Delay(10);
        await stalling!.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        store.Cancel();
        await download.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<ModelState.Missing>(store.State);
        Assert.Equal(40_000, new FileInfo(store.PathOf("model.onnx") + ".partial").Length);

        fetcher.Wrap = null;
        await store.Download();
        Assert.IsType<ModelState.Ready>(store.State);
        Assert.Contains(("first.example", "model.onnx", 40_000L), fetcher.Requests);
    }

    [Fact]
    public async Task RemovingFreesTheSpace()
    {
        var store = new ModelStore(Model(), _folder, Fetcher(), _ => long.MaxValue);
        await store.Download();
        Assert.True(store.IsReady);
        Assert.True(await store.Remove());
        Assert.IsType<ModelState.Missing>(store.State);
        Assert.False(Directory.Exists(_folder));
        Assert.Equal(Weights.Length + Tokens.Length, store.RemainingBytes());
    }

    [Fact]
    public void ThePinnedModelIsSenseVoiceSmallFromAFixedCommit()
    {
        var model = SpeechModelInfo.SenseVoiceSmall;
        Assert.Equal(["model.int8.onnx", "tokens.txt"], model.Files.Select(f => f.Name));
        Assert.Equal(239_549_735L, model.TotalBytes);
        foreach (var file in model.Files)
        {
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.NotEmpty(file.Urls);
            Assert.All(file.Urls, url =>
            {
                Assert.Equal("https", url.Scheme);
                Assert.Contains("/resolve/" + SpeechModelInfo.Revision + "/", url.AbsoluteUri);
            });
        }
        Assert.Equal("huggingface.co", model.Files[0].Urls[0].Host);
    }

    [Theory]
    [InlineData(0L, 100L, 0)]
    [InlineData(99L, 100L, 99)]
    [InlineData(999L, 1000L, 99)]
    [InlineData(1000L, 1000L, 100)]
    [InlineData(5L, 0L, 0)]
    public void PercentRoundsDownSoAHundredMeansDone(long received, long total, int percent)
    {
        Assert.Equal(percent, new ModelState.Downloading(received, total).Percent);
    }
}
