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
/// Once the pending audio reaches <see cref="Config.MaxSeconds"/>, it cuts at the centre of the quietest
/// <see cref="Config.WindowSeconds"/> window found between <see cref="Config.MinSeconds"/> and <see cref="Config.MaxSeconds"/>,
/// i.e. at a pause, so words are not sliced in half. No thresholds to tune: the quietest spot always wins.
/// </summary>
public sealed class Chunker
{
    public sealed record Config(double MinSeconds = 18, double MaxSeconds = 28, double WindowSeconds = 0.4);

    private readonly Config _config;
    private readonly List<short> _pending = new();
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
        while (_pending.Count - _emitted >= maxSamples)
        {
            var cut = QuietestCutPoint();
            ready.Add(Emit(cut));
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
        var chunk = new AudioChunk(_nextIndex, _pendingStart, _pending.GetRange(_emitted, cut).ToArray());
        _emitted += cut;
        _pendingStart += cut;
        _nextIndex += 1;
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
