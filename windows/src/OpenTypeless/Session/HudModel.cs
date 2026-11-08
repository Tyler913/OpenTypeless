using OpenTypeless.Services;
using TypelessCore;

namespace OpenTypeless.Session;

public abstract record HudPhase
{
    public sealed record Hidden : HudPhase;
    public sealed record Recording : HudPhase;
    /// <summary>
    /// Transcribing and cleaning up: one state, the user doesn't need the details. Dots riding a wave, and a ring round
    /// the ■ button filling as the answers come back (<see cref="HudModel.Progress"/>).
    /// </summary>
    public sealed record Working : HudPhase;
    /// <summary>
    /// Nothing to paste into, so the text went to the clipboard; <c>SavedToScratchpad</c>: it was added to the
    /// Scratchpad too (see <see cref="ScratchpadCapture"/>).
    /// </summary>
    public sealed record Copied(bool SavedToScratchpad = false) : HudPhase;
    /// <summary>New words were learned from the user's fixes to the last dictation.</summary>
    public sealed record Learned(string Terms) : HudPhase;
    /// <summary>The answer to a question about the selection (see <see cref="SelectionCommand.Reply"/>), also on the clipboard.</summary>
    public sealed record Answer(string Text) : HudPhase;
    /// <summary>This month's spend has reached a share of the monthly limit, or the limit (see <see cref="SpendLimit"/>).</summary>
    public sealed record Spending(SpendReminder Reminder) : HudPhase;
    public sealed record Error(string Message) : HudPhase;
}

/// <summary>What the recording capsule shows. UI-thread only.</summary>
public sealed class HudModel
{
    public event Action? PhaseChanged;
    public event Action? LevelsChanged;
    public event Action? PreviewChanged;
    public event Action? BadgeChanged;
    /// <summary>The capsule's ✕, and its ■ while the text is on its way: the same as Esc.</summary>
    public event Action? CancelRequested;
    /// <summary>The capsule's ↑: ends the recording as letting go of the key would.</summary>
    public event Action? FinishRequested;

    public void RequestCancel() => CancelRequested?.Invoke();
    public void RequestFinish() => FinishRequested?.Invoke();

    private string? _badge;
    /// <summary>The mode the dictation is in, when it isn't plain dictation ("→ EN" for a translation into English).</summary>
    public string? Badge
    {
        get => _badge;
        set
        {
            if (_badge == value) return;
            _badge = value;
            BadgeChanged?.Invoke();
        }
    }

    private string _preview = "";
    /// <summary>Live preview of what's being said (empty when it's off or hasn't heard anything yet).</summary>
    public string Preview
    {
        get => _preview;
        set
        {
            if (_preview == value) return;
            _preview = value;
            PreviewChanged?.Invoke();
        }
    }

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

    /// <summary>Where the processing ring stands; the capsule advances it on every frame while working.</summary>
    public ProcessingProgress Progress { get; private set; } = new(polishes: true);

    /// <summary>Empties the processing ring for a new dictation.</summary>
    public void StartProgress(bool polishes) => Progress = new ProcessingProgress(polishes);

    public float[] Levels { get; private set; } = new float[18];

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

    /// <summary>How much each bar of the waveform shrinks with its distance from the middle one.</summary>
    private static readonly float[] WaveformTaper = [1f, 0.86f, 0.72f, 0.58f, 0.46f];

    /// <summary>
    /// The capsule's 9 waveform bars (0…1) from the recent levels: the newest level in the middle and
    /// older ones further out on both sides, so speech ripples outwards, tapering towards the ends. The right side runs
    /// half a step behind the left (each bar the mean of two levels), so the two sides don't mirror each other. The same
    /// as the macOS capsule.
    /// </summary>
    public static float[] Waveform(float[] levels)
    {
        var middle = WaveformTaper.Length - 1;
        float Level(int age) => age < levels.Length ? levels[levels.Length - 1 - age] : 0;
        var bars = new float[2 * middle + 1];
        for (var i = 0; i < bars.Length; i++)
        {
            var distance = Math.Abs(i - middle);
            var level = i <= middle ? Level(distance) : (Level(distance) + Level(distance + 1)) / 2;
            bars[i] = level * WaveformTaper[distance];
        }
        return bars;
    }
}
