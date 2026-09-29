namespace TypelessCore;

/// <summary>
/// How long a speech-to-text chunk usually takes, and so when it is late enough to ask the backup route too.
///
/// Fitted on 128 timed dictations (microsoft/mai-transcribe-2 through OpenRouter, 1–27 s chunks): the wait barely
/// grows with the length of the audio, about 0.39 s plus 0.02 s per second of audio (a Theil–Sen line through the
/// chunks left after dropping those more than three MADs above it). Every ordinary answer, including a slower mode
/// around 1.6–2.3 s, came back within 2 s of that line; the few that didn't (4.7–10.6 s) are what the backup is for.
/// So the backup is asked once a chunk is <see cref="Margin"/> past the line. A route that is slower or faster across
/// the board shifts the line by the median of its recent residuals, which the odd very late answer doesn't move.
/// </summary>
public sealed class TranscriptionLatency(int window = 20, double minimumDelay = 1.5, double maximumDelay = 25)
{
    public const double BaseSeconds = 0.39;
    public const double SecondsPerAudioSecond = 0.02;
    public const double Margin = 2.0;

    private readonly object _lock = new();
    private readonly List<double> _residuals = new();

    public double MinimumDelay => minimumDelay;
    public double MaximumDelay => maximumDelay;

    internal static double Baseline(double audioSeconds) => BaseSeconds + SecondsPerAudioSecond * Math.Max(audioSeconds, 0);

    /// <summary>A chunk of <paramref name="audioSeconds"/> came back after <paramref name="latency"/> seconds.</summary>
    public void Record(double latency, double audioSeconds)
    {
        if (!double.IsFinite(latency) || latency < 0) return;
        lock (_lock)
        {
            _residuals.Add(latency - Baseline(audioSeconds));
            if (_residuals.Count > window) _residuals.RemoveRange(0, _residuals.Count - window);
        }
    }

    public int Samples
    {
        get { lock (_lock) return _residuals.Count; }
    }

    /// <summary>How far this route runs from the fitted line: the median recent residual, 0 until three chunks are timed.</summary>
    public double Shift
    {
        get
        {
            double[] recent;
            lock (_lock) recent = _residuals.ToArray();
            return recent.Length >= 3 ? ConnectionCheck.Median(recent) : 0;
        }
    }

    /// <summary>The wait expected for a chunk of this length.</summary>
    public double Expected(double audioSeconds) => Baseline(audioSeconds) + Shift;

    /// <summary>How long to wait for a chunk before asking the backup too: the expected wait plus <see cref="Margin"/>.</summary>
    public double HedgeDelay(double audioSeconds) => Math.Min(maximumDelay, Math.Max(minimumDelay, Expected(audioSeconds) + Margin));
}
