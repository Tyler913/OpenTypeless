namespace TypelessCore;

/// <summary>
/// Tells speech from pauses on 30 ms frames: to trim the silence off both ends of the audio sent for transcription,
/// and to notice while recording that the speaker has stopped (see <see cref="PauseTracker"/>).
///
/// A frame is speech when its RMS is above <see cref="Threshold"/>: twice the level of the quietest tenth of the
/// frames (the room's noise), but never below 0.004, the level silent chunks are skipped under, and never above 0.008
/// (about −42 dBFS, well under quiet speech). So a noisy room still has pauses, and a softly spoken word is never
/// taken for one.
/// </summary>
public static class VoiceActivity
{
    public const int FrameSamples = 480; // 30 ms
    public const float MinimumThreshold = 0.004f;
    public const float MaximumThreshold = 0.008f;
    /// <summary>Audio kept around the speech when trimming, so soft onsets and word endings survive.</summary>
    public const double PaddingSeconds = 0.3;

    /// <summary>The RMS of each 30 ms frame; a shorter last frame counts too.</summary>
    public static float[] FrameLevels(ReadOnlySpan<short> samples)
    {
        var levels = new float[(samples.Length + FrameSamples - 1) / FrameSamples];
        for (var frame = 0; frame < levels.Length; frame++)
        {
            var start = frame * FrameSamples;
            levels[frame] = AudioLevel.Rms(samples[start..Math.Min(start + FrameSamples, samples.Length)]);
        }
        return levels;
    }

    /// <summary>The level above which a frame is speech, given the levels of the frames around it.</summary>
    public static float Threshold(IReadOnlyList<float> levels)
    {
        if (levels.Count == 0) return MinimumThreshold;
        var copy = levels.ToArray();
        return Math.Clamp(2 * NthSmallest(copy, copy.Length / 10), MinimumThreshold, MaximumThreshold);
    }

    /// <summary>
    /// The value sorting would put at <paramref name="k"/>, found by selection instead (reorders <paramref name="values"/>):
    /// the pause tracker asks for the threshold on every 40 ms block of the capture thread, and sorting every level was
    /// most of that thread's time.
    /// </summary>
    private static float NthSmallest(float[] values, int k)
    {
        int lo = 0, hi = values.Length - 1;
        while (lo < hi)
        {
            var pivot = values[(lo + hi) >>> 1];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (values[i] < pivot) i++;
                while (values[j] > pivot) j--;
                if (i <= j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return values[k];
        }
        return values[k];
    }

    /// <summary>
    /// Where the speech in <paramref name="samples"/> starts and ends, widened by <paramref name="padding"/> seconds on each
    /// side: all of it when no frame is speech.
    /// </summary>
    public static (int Start, int End) SpeechBounds(ReadOnlySpan<short> samples, double padding = PaddingSeconds)
    {
        var levels = FrameLevels(samples);
        var threshold = Threshold(levels);
        var first = Array.FindIndex(levels, level => level > threshold);
        if (first < 0) return (0, samples.Length);
        var last = Array.FindLastIndex(levels, level => level > threshold);
        var pad = AudioFormat.SampleCount(padding);
        return (Math.Max(0, first * FrameSamples - pad), Math.Min(samples.Length, (last + 1) * FrameSamples + pad));
    }
}

/// <summary>
/// Follows the live audio 30 ms at a time, to tell when the speaker last spoke and whether they spoke again after a
/// given point. Positions are sample offsets from the start of the recording. Not thread-safe.
/// </summary>
public sealed class PauseTracker
{
    private readonly List<float> _levels = new();
    /// <summary>The frame <c>_levels[0]</c> belongs to: frames before it (the chunks already cut) are forgotten.</summary>
    private int _firstFrame;
    private readonly short[] _partial = new short[VoiceActivity.FrameSamples];
    private int _partialCount;

    /// <summary>Samples appended so far.</summary>
    public int SampleCount => (_firstFrame + _levels.Count) * VoiceActivity.FrameSamples + _partialCount;

    public void Append(ReadOnlySpan<short> samples)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, VoiceActivity.FrameSamples - _partialCount);
            samples[..take].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += take;
            samples = samples[take..];
            if (_partialCount < VoiceActivity.FrameSamples) break;
            _levels.Add(AudioLevel.Rms(_partial));
            _partialCount = 0;
        }
    }

    /// <summary>Forgets the frames that end at or before <paramref name="sample"/>, so only the audio not yet cut into a chunk counts.</summary>
    public void Forget(int sample)
    {
        var drop = Math.Min(_levels.Count, sample / VoiceActivity.FrameSamples - _firstFrame);
        if (drop <= 0) return;
        _levels.RemoveRange(0, drop);
        _firstFrame += drop;
    }

    /// <summary>Where the last frame of speech ends, or null while there has been none.</summary>
    public int? LastSpeechEnd()
    {
        var threshold = VoiceActivity.Threshold(_levels);
        for (var i = _levels.Count - 1; i >= 0; i--)
        {
            if (_levels[i] > threshold) return (_firstFrame + i + 1) * VoiceActivity.FrameSamples;
        }
        return null;
    }

    /// <summary>
    /// Whether any audio after <paramref name="sample"/> is speech, the frame straddling it included. With
    /// <paramref name="includePartial"/> the last, not yet complete frame counts too (at the end of the recording).
    /// </summary>
    public bool SpeechAfter(int sample, bool includePartial = false)
    {
        var threshold = VoiceActivity.Threshold(_levels);
        var from = Math.Max(0, sample / VoiceActivity.FrameSamples - _firstFrame);
        for (var i = from; i < _levels.Count; i++)
        {
            if (_levels[i] > threshold) return true;
        }
        return includePartial && _partialCount > 0 && SampleCount > sample
            && AudioLevel.Rms(_partial.AsSpan(0, _partialCount)) > threshold;
    }
}
