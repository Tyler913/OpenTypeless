namespace TypelessCore;

/// <summary>
/// Where microphone samples go. While the microphone is kept warm between dictations, the last moments are held in
/// a short pre-roll; when a dictation starts, that pre-roll is handed over first and everything after it follows in
/// order, so the first syllable is there even when speaking starts right as the key goes down.
///
/// Thread-safe: <see cref="Deliver"/> runs on the capture thread, <see cref="Begin"/> / <see cref="End"/> on the UI thread.
/// </summary>
public sealed class AudioSink
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly List<short> _preRoll = new();
    private Action<short[]>? _handler;

    /// <param name="preRollSeconds">Audio kept while no dictation is running.</param>
    public AudioSink(double preRollSeconds = 0.4)
    {
        _capacity = AudioFormat.SampleCount(preRollSeconds);
    }

    public bool IsRecording
    {
        get { lock (_lock) return _handler != null; }
    }

    /// <summary>Samples from the microphone, in order.</summary>
    public void Deliver(short[] samples)
    {
        lock (_lock)
        {
            if (_handler != null)
            {
                _handler(samples);
                return;
            }
            _preRoll.AddRange(samples);
            if (_preRoll.Count > _capacity) _preRoll.RemoveRange(0, _preRoll.Count - _capacity);
        }
    }

    /// <summary>Starts sending samples to <paramref name="handler"/>, beginning with the pre-roll. Returns how many pre-roll samples it got.</summary>
    public int Begin(Action<short[]> handler)
    {
        lock (_lock)
        {
            var held = _preRoll.ToArray();
            _preRoll.Clear();
            if (held.Length > 0) handler(held);
            _handler = handler;
            return held.Length;
        }
    }

    /// <summary>Stops sending samples on; the pre-roll starts filling again.</summary>
    public void End()
    {
        lock (_lock)
        {
            _handler = null;
            _preRoll.Clear();
        }
    }
}
