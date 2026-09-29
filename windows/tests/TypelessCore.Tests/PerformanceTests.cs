using System.Diagnostics;
using System.Globalization;

namespace TypelessCore.Tests;

/// <summary>
/// Time budgets for the work that runs while the user dictates (on the capture thread, every 40 ms) and for work the
/// app does on the UI thread, where anything slow freezes the windows and the recording capsule.
///
/// Off by default: timings only mean something in an optimised build, so <c>scripts\perf.ps1</c> runs them in Release
/// (CI does too, and puts the results on the run's summary page). The inputs are sized so that work growing faster than
/// the input takes seconds instead of milliseconds, and budgets leave a wide margin for slow, shared CI machines.
/// The macOS suite (macos/Tests/TypelessCoreTests/PerformanceTests.swift) checks the same cases with the same numbers.
/// </summary>
public class PerformanceTests
{
    /// <summary>
    /// The capture thread's work for each 40 ms block of a 20-minute dictation: hand the samples on through the sink,
    /// append them to the WAV file and the chunker, and measure the level for the capsule.
    /// </summary>
    [PerformanceFact]
    public void LiveAudioKeepsUpWithTheMicrophone()
    {
        var audio = Performance.Speech(minutes: 20);
        // Twice, keeping the better result: a block that is slow by nature is slow both times, while a hiccup of a
        // shared CI machine rarely hits twice.
        double slowest = double.MaxValue, total = double.MaxValue;
        for (var run = 0; run < 2; run++)
        {
            var (runSlowest, runTotal, chunks) = Dictate(audio);
            Assert.True(chunks >= 40);
            slowest = Math.Min(slowest, runSlowest);
            total = Math.Min(total, runTotal);
        }
        // The microphone fills the next block 40 ms later; half of that leaves room for the rest of the thread's work.
        Performance.Check("Live audio: slowest 40 ms block", slowest, budget: 20);
        Performance.Check("Live audio: 20 minutes, all blocks", total, budget: 1500);

        static (double Slowest, double Total, int Chunks) Dictate(short[] audio)
        {
            var block = AudioFormat.SampleCount(0.04);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
            try
            {
                var writer = new WavFileWriter(path);
                var chunker = new Chunker();
                var chunks = 0;
                var sink = new AudioSink();
                sink.Begin(samples =>
                {
                    writer.Append(samples);
                    chunks += chunker.Append(samples).Count;
                });
                var slowest = 0.0;
                Performance.CollectGarbage();
                var total = Performance.Time(() =>
                {
                    for (var offset = 0; offset + block <= audio.Length; offset += block)
                    {
                        var samples = audio.AsSpan(offset, block).ToArray();
                        slowest = Math.Max(slowest, Performance.Time(() =>
                        {
                            sink.Deliver(samples);
                            AudioLevel.Rms(samples);
                        }));
                    }
                });
                sink.End();
                writer.Close();
                return (slowest, total, chunks);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Re-transcribing a saved recording hands all of it to the chunker at once, on the UI thread. Cutting every
    /// 0.25–0.5 s instead of 18–28 s makes over 3,000 chunks out of 20 minutes, so any per-chunk work that grows with the
    /// recording (as removing each chunk from the front of the audio did) costs seconds.
    /// </summary>
    [PerformanceFact]
    public void ReTranscribingALongRecordingIsLinear()
    {
        var audio = Performance.Speech(minutes: 20);
        var chunks = 0;
        Performance.Measure("Re-transcribe: chunk 20 minutes at once", budget: 300,
            () => chunks = new Chunker(new Chunker.Config(MinSeconds: 0.25, MaxSeconds: 0.5, WindowSeconds: 0.05)).Append(audio).Count);
        Assert.True(chunks >= 2400);
    }

    /// <summary>
    /// When the user fixes a word in dictated text, the fix is found by comparing the whole dictation before and after,
    /// on the UI thread. A 20-minute dictation is about 3,000 English words or 5,000 Chinese characters.
    /// </summary>
    [PerformanceFact]
    public void LearningFromAFixInALongDictation()
    {
        string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "a", "lazy", "dog", "while", "we", "talk",
                          "about", "shipping", "the", "next", "release", "on", "time"];
        var english = Enumerable.Range(0, 3000).Select(i => words[(i * 7 + i / 3) % words.Length]).ToArray();
        english[1500] = "TypeList";
        var inserted = string.Join(" ", english);
        english[1500] = "Typeless";
        var edited = string.Join(" ", english);
        List<string> learned = [];
        Performance.Measure("Learn from a fix: 20-minute English dictation", budget: 50,
            () => learned = CorrectionLearner.ReviewEdit(inserted, edited).Learnable.Select(c => c.Corrected).ToList());
        Assert.Equal(["Typeless"], learned);

        const string characters = "我们今天讨论一下这个项目的进展情况以及下一步的计划安排和具体细节";
        var chinese = string.Concat(Enumerable.Range(0, 5000).Select(i => characters[(i * 7 + i / 3) % characters.Length]));
        var heard = chinese[..2500] + "罗级鼠标" + chinese[2500..];
        var fixedText = chinese[..2500] + "罗技鼠标" + chinese[2500..];
        string? skipped = "not run";
        Performance.Measure("Learn from a fix: 20-minute Chinese dictation", budget: 50,
            () => skipped = CorrectionLearner.ReviewEdit(heard, fixedText).Skipped);
        Assert.Null(skipped);
    }

    /// <summary>
    /// Everything the Home page computes each time it's drawn (on macOS also while the pointer moves over the activity
    /// grid), after five years of dictating every day.
    /// </summary>
    [PerformanceFact]
    public void HomePageStatsAfterYearsOfUse()
    {
        var today = new DateOnly(2026, 9, 29);
        var ledger = new UsageLedger();
        for (var day = 0; day < 5 * 365; day++)
        {
            var date = today.AddDays(-day);
            for (var dictation = 0; dictation < 1 + day % 6; dictation++)
            {
                ledger.AddDictation(date, 20 + (day * 13 + dictation * 7) % 400, 30);
            }
            ledger.AddCost(date, 0.001, 0.002, 0);
        }
        var cells = 0;
        Performance.Measure("Home page: stats for five years of use", budget: 16, () =>
        {
            ledger.Totals();
            ledger.Today(today);
            ledger.Month(today);
            ledger.Streaks(today);
            cells = ledger.Heatmap(today, 53, 0).Sum(week => week.Length);
        });
        Assert.Equal(53 * 7, cells);
    }
}

/// <summary>A test that only runs with OPENTYPELESS_PERF=1 (see <see cref="PerformanceTests"/>).</summary>
public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (!Performance.IsEnabled) Skip = "Performance budgets run with OPENTYPELESS_PERF=1 (scripts\\perf.ps1)";
    }
}

internal static class Performance
{
    public static bool IsEnabled => Environment.GetEnvironmentVariable("OPENTYPELESS_PERF") == "1";

    /// <summary>
    /// Runs <paramref name="work"/> a few times and checks the fastest run against <paramref name="budget"/> (ms):
    /// that's what the code costs, the slower runs are the machine (other jobs on a shared runner, JIT warm-up).
    /// </summary>
    public static void Measure(string name, double budget, Action work, int runs = 3) =>
        Check(name, Enumerable.Range(0, runs).Min(_ =>
        {
            CollectGarbage();
            return Time(work);
        }), budget);

    /// <summary>
    /// Starts a measurement from a collected heap, so the garbage an earlier test or run left behind (hundreds of MB of
    /// audio) isn't collected on this one's clock.
    /// </summary>
    public static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Records a timing (ms) for the summary (OPENTYPELESS_PERF_REPORT) and fails the test when it's over budget.</summary>
    public static void Check(string name, double milliseconds, double budget)
    {
        if (Environment.GetEnvironmentVariable("OPENTYPELESS_PERF_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture, $"{name}\t{milliseconds:F3}\t{budget:F0}\n"));
        }
        Assert.True(milliseconds <= budget, string.Create(CultureInfo.InvariantCulture, $"{name}: {milliseconds:F1} ms, budget {budget:F0} ms"));
    }

    /// <summary>Milliseconds <paramref name="work"/> takes.</summary>
    public static double Time(Action work)
    {
        var start = Stopwatch.GetTimestamp();
        work();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Speech-like audio: a tone that pauses for 0.4 s every 2 s, so the chunker finds a quiet spot to cut at.</summary>
    public static short[] Speech(int minutes)
    {
        var samples = new short[AudioFormat.SampleCount(minutes * 60)];
        int period = AudioFormat.SampleCount(2), pause = AudioFormat.SampleCount(0.4);
        for (var i = 0; i < samples.Length; i++) samples[i] = i % period < pause ? (short)0 : (short)(8000 * Math.Sin(i * 0.3));
        return samples;
    }
}
