namespace OpenTypeless.Session;

public abstract record HudPhase
{
    public sealed record Hidden : HudPhase;
    public sealed record Recording : HudPhase;
    /// <summary>Transcribing and cleaning up: one state, the user doesn't need the details.</summary>
    public sealed record Working : HudPhase;
    /// <summary>Nothing to paste into, so the text went to the clipboard.</summary>
    public sealed record Copied : HudPhase;
    public sealed record Error(string Message) : HudPhase;
}

/// <summary>What the recording capsule shows. UI-thread only.</summary>
public sealed class HudModel
{
    public event Action? PhaseChanged;
    public event Action? LevelsChanged;

    private HudPhase _phase = new HudPhase.Hidden();
    public HudPhase Phase
    {
        get => _phase;
        set
        {
            if (_phase == value) return;
            _phase = value;
            PhaseChanged?.Invoke();
        }
    }

    public float[] Levels { get; private set; } = new float[18];
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public void Push(float level)
    {
        // Perceptual scaling so normal speech fills the bars.
        var scaled = Math.Min(1f, MathF.Sqrt(level) * 3.2f);
        Array.Copy(Levels, 1, Levels, 0, Levels.Length - 1);
        Levels[^1] = scaled;
        LevelsChanged?.Invoke();
    }

    public void ResetLevels()
    {
        Levels = new float[Levels.Length];
        LevelsChanged?.Invoke();
    }

    public void SetLevels(float[] levels)
    {
        Levels = levels;
        LevelsChanged?.Invoke();
    }
}
