namespace TypelessCore;

/// <summary>
/// How long a speech-to-text route usually takes, relative to the length of the audio, and so when a chunk is late
/// enough to ask the backup route too. Waits scale with audio length (a 28 s chunk takes longer than a 3 s one), so
/// the median of recent wait ÷ audio-seconds ratios is kept rather than a fixed number of seconds.
/// </summary>
public sealed class TranscriptionLatency(int window = 20, double minimumDelay = 3, double maximumDelay = 25)
{
    /// <summary>Until three chunks have been timed: 0.3 s per second of audio plus 1.5 s, a conservative guess.</summary>
    internal const double DefaultRatio = 0.3;
    internal const double DefaultOverhead = 1.5;

    private readonly object _lock = new();
    private readonly List<double> _ratios = new();

    public double MinimumDelay => minimumDelay;
    public double MaximumDelay => maximumDelay;

    /// <summary>A chunk of <paramref name="audioSeconds"/> came back after <paramref name="latency"/> seconds.</summary>
    public void Record(double latency, double audioSeconds)
    {
        if (!double.IsFinite(latency) || latency < 0) return;
        lock (_lock)
        {
            _ratios.Add(latency / Math.Max(audioSeconds, 1));
            if (_ratios.Count > window) _ratios.RemoveRange(0, _ratios.Count - window);
        }
    }

    public int Samples
    {
        get { lock (_lock) return _ratios.Count; }
    }

    /// <summary>The wait expected for a chunk of this length.</summary>
    public double Expected(double audioSeconds)
    {
        double[] recent;
        lock (_lock) recent = _ratios.ToArray();
        if (recent.Length < 3) return DefaultRatio * audioSeconds + DefaultOverhead;
        return ConnectionCheck.Median(recent) * Math.Max(audioSeconds, 1);
    }

    /// <summary>How long to wait for a chunk before asking the backup too: twice the expected wait plus a second.</summary>
    public double HedgeDelay(double audioSeconds) => Math.Min(maximumDelay, Math.Max(minimumDelay, Expected(audioSeconds) * 2 + 1));
}
