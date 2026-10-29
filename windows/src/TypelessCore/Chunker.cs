using System.Runtime.InteropServices;

namespace TypelessCore;

public sealed record AudioChunk(int Index, int StartSample, short[] Samples)
{
    public double Duration => AudioFormat.Seconds(Samples.Length);
    public double StartTime => AudioFormat.Seconds(StartSample);

    /// <summary>
    /// This chunk without the silence before and after the speech (see <see cref="VoiceActivity.SpeechBounds"/>): less to
    /// upload and transcribe, and nothing for the model to hallucinate on. Itself when there is nothing to trim.
    /// </summary>
    public AudioChunk Trimmed()
    {
        var (start, end) = VoiceActivity.SpeechBounds(Samples);
        return start == 0 && end == Samples.Length ? this : this with { StartSample = StartSample + start, Samples = Samples[start..end] };
    }
}

/// <summary>
/// Splits a live PCM stream into chunks that are each short enough for a single STT request.
///
/// Once <see cref="Config.PauseCutSeconds"/> of audio are pending, it cuts as soon as the speaker pauses
/// (<see cref="Config.PauseSeconds"/> quiet after speech, as <see cref="VoiceActivity"/> tells them apart), in the middle of
/// the pause's first <see cref="Config.PauseSeconds"/>: that chunk is sent while they go on, so less is left to send when
/// they let go of the key. Without such a pause, once the pending audio reaches <see cref="Config.MaxSeconds"/> it cuts at
/// the centre of the quietest <see cref="Config.WindowSeconds"/> window found between <see cref="Config.MinSeconds"/> and
/// <see cref="Config.MaxSeconds"/>, so words are not sliced in half: the quietest spot always wins.
///
/// Where it cuts depends only on the audio, not on how it arrives: a retry hands the saved recording over in one go and
/// reuses the transcripts of the chunks that succeeded by their index, so it must cut exactly where the live recording,
/// delivered 40 ms at a time, was cut.
/// </summary>
public sealed class Chunker
{
    /// <param name="PauseCutSeconds">
    /// Pending audio from which a pause is enough to cut at. Short enough that a dictation which pauses now and then
    /// leaves at most this much (plus what follows the last pause) to send at release, since the wait for a chunk grows
    /// with its length (about 0.05 s per second of audio in 789 timed dictations); long enough that each request still
    /// has a few sentences of context and the chunk count stays low.
    /// </param>
    /// <param name="PauseSeconds">
    /// How long the speaker has to be quiet for that: the pause the speculative tail waits for, so a pause long past
    /// <paramref name="PauseCutSeconds"/> is cut at rather than transcribed ahead of time and then sent again.
    /// </param>
    public sealed record Config(double MinSeconds = 18, double MaxSeconds = 28, double WindowSeconds = 0.4,
                                double PauseCutSeconds = 8, double PauseSeconds = TranscriptionPipeline.PauseSeconds);

    /// <summary>
    /// Which way recordings are cut into chunks. A saved dictation's per-chunk transcripts are reused by a retry only when
    /// it was cut the same way (1, or none saved, was 18–28 s at the quietest spot only; 2 also cuts at pauses).
    /// </summary>
    public const int Layout = 2;

    private readonly Config _config;
    private readonly List<short> _pending = new();
    /// <summary>The RMS of each whole 30 ms frame of the chunk being built, counted from its first sample.</summary>
    private readonly List<float> _levels = new();
    /// <summary>How much of the front of <see cref="_pending"/> this append has already handed out as chunks.</summary>
    private int _emitted;
    private int _pendingStart;
    private int _nextIndex;

    public Chunker(Config? config = null)
    {
        _config = config ?? new Config();
    }

    /// <summary>Appends samples and returns any chunks that became ready.</summary>
    public List<AudioChunk> Append(ReadOnlySpan<short> samples)
    {
        _pending.AddRange(samples);
        var ready = new List<AudioChunk>();
        var maxSamples = AudioFormat.SampleCount(_config.MaxSeconds);
        while (true)
        {
            // A pause is looked for up to `MaxSeconds` before falling back to the quietest spot, so a chunk is cut at the
            // same place whether its audio came in one go or 40 ms at a time.
            if (PauseCutPoint(Math.Min(_pending.Count - _emitted, maxSamples)) is { } cut) ready.Add(Emit(cut));
            else if (_pending.Count - _emitted >= maxSamples) ready.Add(Emit(QuietestCutPoint()));
            else break;
        }
        DropEmitted();
        return ready;
    }

    /// <summary>Flushes whatever audio is left as the final chunk (null when nothing is left).</summary>
    public AudioChunk? Finish()
    {
        if (_pending.Count == 0) return null;
        var chunk = Emit(_pending.Count);
        DropEmitted();
        return chunk;
    }

    /// <summary>The chunk <see cref="Finish"/> would flush now, left in place (null when nothing is pending).</summary>
    public AudioChunk? Peek() => _pending.Count == 0 ? null : new AudioChunk(_nextIndex, _pendingStart, _pending.ToArray());

    /// <summary>Where the audio not yet cut into a chunk starts, in samples from the start of the recording.</summary>
    public int PendingStart => _pendingStart;

    private AudioChunk Emit(int cut)
    {
        // Copied straight from the list's storage: GetRange would copy it once more, and a chunk is ~1 MB allocated on the
        // audio thread.
        var chunk = new AudioChunk(_nextIndex, _pendingStart, CollectionsMarshal.AsSpan(_pending).Slice(_emitted, cut).ToArray());
        _emitted += cut;
        _pendingStart += cut;
        _nextIndex += 1;
        _levels.Clear();
        return chunk;
    }

    /// <summary>
    /// Removes what <see cref="Emit"/> handed out, once per call rather than once per chunk: removing each chunk from
    /// the front moved all the audio after it, so re-transcribing a long recording (appended in one go) was quadratic.
    /// </summary>
    private void DropEmitted()
    {
        _pending.RemoveRange(0, _emitted);
        _emitted = 0;
    }

    /// <summary>
    /// Measures the frames of the chunk being built that end within its first <paramref name="available"/> samples, and
    /// returns where to cut at the first pause found once <see cref="Config.PauseCutSeconds"/> are pending, if any. Frame
    /// by frame, each decided on the frames up to it, so the answer doesn't depend on how the audio was split into appends.
    /// </summary>
    private int? PauseCutPoint(int available)
    {
        const int frame = VoiceActivity.FrameSamples;
        var earliest = AudioFormat.SampleCount(_config.PauseCutSeconds);
        // Chunks too short to reach it (as in the performance test) are never cut at pauses: nothing to measure.
        if (earliest >= AudioFormat.SampleCount(_config.MaxSeconds)) return null;
        var pauseFrames = Math.Max(1, AudioFormat.SampleCount(_config.PauseSeconds) / frame);
        while ((_levels.Count + 1) * frame <= available)
        {
            var start = _emitted + _levels.Count * frame;
            _levels.Add(AudioLevel.Rms(CollectionsMarshal.AsSpan(_pending).Slice(start, frame)));
            if (_levels.Count * frame >= earliest && _levels.Count > pauseFrames && PauseJustBegan(pauseFrames))
            {
                var pauseStart = (_levels.Count - pauseFrames) * frame;
                return pauseStart + pauseFrames * frame / 2;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the last <paramref name="count"/> frames are quiet and the one before them speech: the speaker has just
    /// been quiet for <see cref="Config.PauseSeconds"/>. A pause that began earlier (before
    /// <see cref="Config.PauseCutSeconds"/>) isn't cut at when it gets there: the speculative tail already covers it, and
    /// cutting would send that audio a second time.
    /// </summary>
    private bool PauseJustBegan(int count)
    {
        var speech = _levels[_levels.Count - count - 1];
        var loudest = 0f;
        for (var i = _levels.Count - count; i < _levels.Count; i++) loudest = Math.Max(loudest, _levels[i]);
        // Most frames are clearly speech or clearly quiet whatever the room's noise (the threshold is always within these
        // bounds), which spares working the threshold out over every frame.
        if (speech <= VoiceActivity.MinimumThreshold || loudest > VoiceActivity.MaximumThreshold || speech <= loudest) return false;
        if (speech > VoiceActivity.MaximumThreshold && loudest <= VoiceActivity.MinimumThreshold) return true;
        var threshold = VoiceActivity.Threshold(_levels);
        return speech > threshold && loudest <= threshold;
    }

    private int QuietestCutPoint()
    {
        var pending = CollectionsMarshal.AsSpan(_pending)[_emitted..];
        var lower = AudioFormat.SampleCount(_config.MinSeconds);
        var upper = Math.Min(pending.Length, AudioFormat.SampleCount(_config.MaxSeconds));
        var window = Math.Max(1, AudioFormat.SampleCount(_config.WindowSeconds));
        var hop = Math.Max(1, AudioFormat.SampleCount(0.02));
        if (upper - lower <= window) return upper;

        // Sliding sum of squares over the window, sampled every `hop` samples.
        var bestStart = lower;
        var bestEnergy = double.MaxValue;
        var start = lower;
        var energy = 0.0;
        for (var i = start; i < start + window; i++) energy += (double)pending[i] * pending[i];
        while (start + window <= upper)
        {
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestStart = start;
            }
            var next = start + hop;
            if (next + window > upper) break;
            for (var i = start; i < next; i++) energy -= (double)pending[i] * pending[i];
            for (var i = start + window; i < next + window; i++) energy += (double)pending[i] * pending[i];
            start = next;
        }
        return bestStart + window / 2;
    }
}

public static class AudioLevel
{
    /// <summary>RMS of the samples normalised to 0...1.</summary>
    public static float Rms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return 0;
        float sum = 0;
        foreach (var s in samples)
        {
            var v = s / 32768f;
            sum += v * v;
        }
        return MathF.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// True when no 30 ms frame rises above <paramref name="threshold"/> RMS. Such chunks are skipped because
    /// STT models tend to hallucinate text ("Thanks for watching!") on pure silence.
    /// </summary>
    public static bool IsSilent(ReadOnlySpan<short> samples, float threshold = 0.004f)
    {
        var frame = AudioFormat.SampleCount(0.03);
        var i = 0;
        while (i < samples.Length)
        {
            var end = Math.Min(i + frame, samples.Length);
            if (Rms(samples[i..end]) > threshold) return false;
            i = end;
        }
        return true;
    }
}
