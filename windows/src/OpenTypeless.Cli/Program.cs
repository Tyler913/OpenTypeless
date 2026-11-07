using System.Diagnostics;
using System.Text;
using OpenTypeless.Audio;
using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var transcribe = Array.IndexOf(args, "--transcribe-file");
        if (transcribe >= 0 && transcribe + 1 < args.Length)
        {
            // Runs the full STT + polish pipeline on a file and prints the result, without the UI.
            // Useful for testing long recordings end to end.
            return await Cli.Transcribe(args[transcribe + 1], polish: !args.Contains("--no-polish"),
                                        realtime: args.Contains("--realtime"), chunksOnly: args.Contains("--chunks-only"));
        }
        if (args.Contains("--download-model")) return await Cli.DownloadModel();
        var polishText = Array.IndexOf(args, "--polish-text");
        if (polishText >= 0 && polishText + 1 < args.Length) return await Cli.PolishText(args[polishText + 1]);
        if (args.Contains("--eval-polish")) return await PolishEval.Run(args);

        Console.Error.WriteLine("""
            OpenTypeless command-line tools

              OpenTypeless.Cli --transcribe-file <audio> [--realtime] [--no-polish]
                  Full pipeline on an audio file (WAV, MP3, M4A, WMA, FLAC…); --realtime feeds audio at speaking speed.
              OpenTypeless.Cli --transcribe-file <audio> --chunks-only
                  Shows where the chunker cuts.
              OpenTypeless.Cli --download-model
                  Downloads the on-device speech model (as Settings → Models does) and checks it.
              OpenTypeless.Cli --polish-text <file>
                  Only the clean-up, as the app does it, on a transcript saved as text.
              OpenTypeless.Cli --eval-polish <cases.json> --models <ids> --out <path> [...]
                  Evaluates clean-up prompts and models (see eval/README.md).

            Uses the providers, models and API keys configured in the app (OPENROUTER_API_KEY overrides the OpenRouter key),
            or the on-device model when the app is set to recognise speech on this PC.
            """);
        return 64;
    }
}

public static class Cli
{
    public static async Task<int> Transcribe(string path, bool polish, bool realtime, bool chunksOnly)
    {
        short[] samples;
        try
        {
            samples = await AudioLoader.Load(path);
        }
        catch (Exception error)
        {
            Log($"❌ Can't read audio: {error.Message}");
            return 1;
        }
        var duration = AudioFormat.Seconds(samples.Length);
        if (chunksOnly)
        {
            PrintChunks(samples);
            return 0;
        }
        var settings = AppSettings.Shared;
        var onDevice = settings.SpeechEngine == SpeechEngine.OnDevice;
        ISpeechTranscriber client;
        TranscriptionOptions options;
        if (onDevice)
        {
            if (!SpeechModel.Shared.IsReady)
            {
                Log("❌ The on-device speech model isn't downloaded (OpenTypeless.Cli --download-model)");
                return 1;
            }
            client = SpeechModel.Shared.Transcriber;
            options = OnDeviceTranscriber.Options(settings.SttLanguage);
        }
        else
        {
            if (settings.SttEndpoint is not { } sttEndpoint)
            {
                Log("❌ The speech-to-text provider's base URL is invalid");
                return 1;
            }
            client = new ApiClient(sttEndpoint);
            options = new TranscriptionOptions(settings.SttModel, settings.SttLanguage.Length == 0 ? null : settings.SttLanguage, settings.DictationVocabulary);
        }
        // Without a key for it, on-device dictation goes in as spoken (see SpeechSetup.CleansUp), and so does this.
        polish = polish && SpeechSetup.CleansUp(true, settings.SpeechEngine, settings.IsConfigured(settings.PolishProvider));
        Log($"🎧 {Path.GetFileName(path)}: {duration:0.0} s");
        Log($"   STT: {(onDevice ? $"on-device / {SpeechModel.Shared.Store.Model.DisplayName}" : $"{settings.SttProvider.DisplayName()} / {settings.SttModel}")}"
            + (polish ? $"; clean-up: {settings.PolishProvider.DisplayName()} / {settings.PolishModel}" : "")
            + (polish && settings.PolishBackupEndpoint != null ? $" (backup {settings.PolishBackupProvider.DisplayName()} / {settings.PolishBackupModel})" : ""));

        var start = Stopwatch.StartNew();
        var pipeline = new TranscriptionPipeline(
            client,
            options,
            observer: (index, state) =>
            {
                var t = $"{start.Elapsed.TotalSeconds,5:0.0}s";
                switch (state)
                {
                    case ChunkState.Transcribing { Attempt: > 1 } s: Log($"[{t}] chunk {index}: attempt {s.Attempt}"); break;
                    case ChunkState.Done d: Log($"[{t}] chunk {index} ✅ {(d.Text.Length > 40 ? d.Text[..40] : d.Text)}…"); break;
                    case ChunkState.Failed f: Log($"[{t}] chunk {index} ❌ {f.Message}"); break;
                    case ChunkState.SkippedSilence: Log($"[{t}] chunk {index} silent, skipped"); break;
                }
            });

        if (realtime)
        {
            // Feed audio at real speed, like a live microphone, to exercise the streaming path.
            var block = AudioFormat.SampleCount(0.1);
            for (var i = 0; i < samples.Length; i += block)
            {
                pipeline.Append(samples.AsSpan(i, Math.Min(block, samples.Length - i)));
                await Task.Delay(100);
            }
        }
        else
        {
            pipeline.Append(samples);
        }
        var released = Stopwatch.StartNew();

        string raw;
        try
        {
            raw = await pipeline.Finish();
        }
        catch (Exception error)
        {
            Log($"❌ Transcription failed: {error.Message}");
            return 2;
        }
        Log($"⏱  Transcribed: {released.Elapsed.TotalSeconds:0.0} s after release ({start.Elapsed.TotalSeconds:0.0} s total)"
            + (pipeline.TailWasSpeculative ? "; the last segment was transcribed ahead of time, at the final pause" : ""));
        Console.WriteLine($"\n===== Raw transcript ({TextMetrics.CharacterCount(raw)} chars) =====\n{raw}\n");

        if (!polish) return 0;
        return await CleanUp(raw);
    }

    /// <summary>
    /// <c>--download-model</c>: downloads the on-device speech model into the app's folder (resuming one that stopped),
    /// checks its SHA-256 and loads it once, printing the progress. Exit code 0 once it's ready.
    /// </summary>
    public static async Task<int> DownloadModel()
    {
        var model = SpeechModel.Shared;
        var store = model.Store;
        Log($"📦 {store.Model.DisplayName}: {store.Model.TotalBytes / 1_048_576.0:0} MB into {store.Folder}");
        var clock = Stopwatch.StartNew();
        var last = -1;
        store.Changed += () =>
        {
            if (store.State is ModelState.Downloading d && d.Percent / 10 != last)
            {
                last = d.Percent / 10;
                Log($"   {d.Percent,3}%  {clock.Elapsed.TotalSeconds:0} s");
            }
        };
        if (!store.IsReady) await store.Download();
        if (store.State is ModelState.Failed failed)
        {
            Log($"❌ {failed.Message}");
            return 1;
        }
        if (!store.IsReady)
        {
            Log("❌ The download stopped");
            return 1;
        }
        Log($"✅ Downloaded and checked in {clock.Elapsed.TotalSeconds:0.0} s");
        // Loads it, so a model the runtime can't open fails here rather than at the first dictation.
        var load = Stopwatch.StartNew();
        try
        {
            var silence = new short[AudioFormat.SampleCount(1)];
            await model.Transcriber.TranscribeDetailedWithRetry(silence, OnDeviceTranscriber.Options(null));
        }
        catch (Exception error)
        {
            Log($"❌ The model doesn't load: {error.Message}");
            return 1;
        }
        Log($"✅ Loaded in {load.Elapsed.TotalSeconds:0.0} s");
        return 0;
    }

    /// <summary><c>--polish-text &lt;file&gt;</c>: the clean-up alone, on a transcript in a text file.</summary>
    public static async Task<int> PolishText(string path)
    {
        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(path);
        }
        catch (Exception error)
        {
            Log($"❌ Can't read {path}: {error.Message}");
            return 1;
        }
        var settings = AppSettings.Shared;
        Log($"📝 {Path.GetFileName(path)}: {TextMetrics.CharacterCount(raw)} chars, {WordCount.Count(raw)} words; clean-up: "
            + $"{settings.PolishProvider.DisplayName()} / {settings.PolishModel}");
        return await CleanUp(raw);
    }

    /// <summary>Cleans <paramref name="raw"/> up the way the app does (in parts when it's long, each part checked) and prints the result.</summary>
    public static async Task<int> CleanUp(string raw)
    {
        var settings = AppSettings.Shared;
        if (settings.PolishEndpoint is not { } polishEndpoint)
        {
            Log("❌ The clean-up provider's base URL is invalid");
            return 3;
        }
        var polishStart = Stopwatch.StartNew();
        try
        {
            var backupEndpoint = settings.PolishBackupEndpoint;
            List<ModelInfo>? models = null;
            if ((polishEndpoint.Id == ProviderId.OpenRouter || backupEndpoint?.Id == ProviderId.OpenRouter)
                && settings.Endpoint(ProviderId.OpenRouter) is { } openRouter)
            {
                try { models = await new ApiClient(openRouter).ListModels(); } catch { }
            }
            PolishRoute Route(ProviderEndpoint endpoint, string model) => new(new ApiClient(endpoint), new PolishOptions(
                model, settings.DictationVocabulary, settings.ExtraInstructions,
                endpoint.Id == ProviderId.OpenRouter ? models?.FirstOrDefault(m => m.Id == model) : null,
                Misheard: settings.MisheardHints,
                Host: endpoint.Id == ProviderId.OpenRouter ? settings.OpenRouterHost(model) : null));
            var outcome = await SegmentedPolish.Run(raw, Route(polishEndpoint, settings.PolishModel.Trim()),
                                                    backupEndpoint is null ? null : Route(backupEndpoint, settings.PolishBackupModel.Trim()));
            Log($"⏱  Clean-up took {polishStart.Elapsed.TotalSeconds:0.0} s (first token {outcome.FirstTokenSeconds:0.00} s, "
                + $"{(outcome.UsedBackup ? "backup " : "")}{outcome.Model}), {outcome.Parts.Count} part(s)");
            for (var i = 0; i < outcome.Parts.Count; i++)
            {
                var part = outcome.Parts[i];
                var kept = part.Answer is { } answer ? WordCount.Count(answer.Result.Text) : 0;
                var similarity = part.Answer is { } a ? PolishMetrics.Similarity(part.Transcript, a.Result.Text) : 0;
                Log($"   part {i + 1}: {WordCount.Count(part.Transcript)} → {kept} words, similarity {similarity:0.00}"
                    + (part.Fallback is { } fallback ? $" — inserted as spoken ({fallback.ToString().ToLowerInvariant()})" : ""));
                if (part.Error is { } error) Log($"     {error.Message}");
            }
            Console.WriteLine($"===== Cleaned up ({TextMetrics.CharacterCount(outcome.Text)} chars, {WordCount.Count(outcome.Text)} words) =====\n{outcome.Text}\n");
            return 0;
        }
        catch (Exception error)
        {
            Log($"❌ Clean-up failed: {error.Message}");
            return 3;
        }
    }

    /// <summary>Shows where the chunker cuts and how quiet each cut point is (dBFS over ±100 ms).</summary>
    public static void PrintChunks(short[] samples)
    {
        var chunker = new Chunker();
        var chunks = chunker.Append(samples);
        if (chunker.Finish() is { } tail) chunks.Add(tail);
        var radius = AudioFormat.SampleCount(0.1);
        var speechRms = AudioLevel.Rms(samples);
        Console.WriteLine($"Duration {AudioFormat.Seconds(samples.Length):0.0} s, overall RMS {20 * Math.Log10(Math.Max(speechRms, 1e-6)):0.0} dBFS, {chunks.Count} chunks");
        foreach (var chunk in chunks)
        {
            var end = chunk.StartSample + chunk.Samples.Length;
            int lo = Math.Max(0, end - radius), hi = Math.Min(samples.Length, end + radius);
            var cutRms = AudioLevel.Rms(samples.AsSpan(lo, hi - lo));
            Console.WriteLine($"  chunk {chunk.Index}: {chunk.StartTime,6:0.00}s → {chunk.StartTime + chunk.Duration,6:0.00}s ({chunk.Duration:0.0}s), " +
                              $"cut-point energy {20 * Math.Log10(Math.Max(cutRms, 1e-6)):0.0} dBFS{(AudioLevel.IsSilent(chunk.Samples) ? " (silent)" : "")}");
        }
    }

    public static void Log(string message) => Console.Error.WriteLine(message);
}
