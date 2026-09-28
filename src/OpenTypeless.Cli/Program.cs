using System.Diagnostics;
using System.Text;
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
        if (args.Contains("--eval-polish")) return await PolishEval.Run(args);

        Console.Error.WriteLine("""
            OpenTypeless command-line tools

              OpenTypeless.Cli --transcribe-file <audio> [--realtime] [--no-polish]
                  Full pipeline on an audio file (WAV, MP3, M4A, WMA, FLAC…); --realtime feeds audio at speaking speed.
              OpenTypeless.Cli --transcribe-file <audio> --chunks-only
                  Shows where the chunker cuts.
              OpenTypeless.Cli --eval-polish <cases.json> --models <ids> --out <path> [...]
                  Evaluates clean-up prompts and models (see eval/README.md).

            Uses the providers, models and API keys configured in the app (OPENROUTER_API_KEY overrides the OpenRouter key).
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
        if (settings.SttEndpoint is not { } sttEndpoint)
        {
            Log("❌ The speech-to-text provider's base URL is invalid");
            return 1;
        }
        var client = new ApiClient(sttEndpoint);
        Log($"🎧 {Path.GetFileName(path)}: {duration:0.0} s");
        Log($"   STT: {settings.SttProvider.DisplayName()} / {settings.SttModel}"
            + (polish ? $"; clean-up: {settings.PolishProvider.DisplayName()} / {settings.PolishModel}" : ""));

        var start = Stopwatch.StartNew();
        var pipeline = new TranscriptionPipeline(
            client,
            new TranscriptionOptions(settings.SttModel, settings.SttLanguage.Length == 0 ? null : settings.SttLanguage),
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
        Log($"⏱  Transcribed: {released.Elapsed.TotalSeconds:0.0} s after release ({start.Elapsed.TotalSeconds:0.0} s total)");
        Console.WriteLine($"\n===== Raw transcript ({TextMetrics.CharacterCount(raw)} chars) =====\n{raw}\n");

        if (!polish) return 0;
        if (settings.PolishEndpoint is not { } polishEndpoint)
        {
            Log("❌ The clean-up provider's base URL is invalid");
            return 3;
        }
        var polishClient = new ApiClient(polishEndpoint);
        var polishStart = Stopwatch.StartNew();
        try
        {
            ModelInfo? info = null;
            if (polishEndpoint.Id == ProviderId.OpenRouter)
            {
                try { info = (await polishClient.ListModels()).FirstOrDefault(m => m.Id == settings.PolishModel); } catch { }
            }
            var result = await polishClient.Polish(raw, new PolishOptions(settings.PolishModel, settings.VocabularyList, settings.ExtraInstructions, info));
            Log($"⏱  Clean-up took {polishStart.Elapsed.TotalSeconds:0.0} s{(result.Truncated ? " (truncated)" : "")}");
            if (Prompts.LooksLikeAnAnswer(raw, result.Text))
            {
                Log("⚠️ Output is much longer than the input; the app would fall back to the raw transcript");
            }
            Console.WriteLine($"===== Cleaned up ({TextMetrics.CharacterCount(result.Text)} chars) =====\n{result.Text}\n");
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
