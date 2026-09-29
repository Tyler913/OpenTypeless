using Microsoft.UI.Dispatching;

namespace OpenTypeless.Audio;

/// <summary>Listens to a microphone while the user tests it on the General page, and reports how loud it is. UI-thread only.</summary>
public sealed class MicrophoneTester
{
    private readonly AudioRecorder _recorder = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>Raised on the UI thread with each new level.</summary>
    public event Action? Changed;

    /// <summary>0…1, scaled like the recording capsule so normal speech fills most of the bar.</summary>
    public float Level { get; private set; }
    /// <summary>A slowly falling peak marker.</summary>
    public float Peak { get; private set; }
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public void Start(string? deviceId)
    {
        Stop();
        _recorder.DeviceId = deviceId;
        _recorder.OnLevel = level => _dispatcher.TryEnqueue(() => Push(level));
        try
        {
            _recorder.Start();
            IsRunning = true;
            Error = null;
        }
        catch (Exception error)
        {
            Error = error.Message;
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        _recorder.Stop();
        _recorder.OnLevel = null;
        IsRunning = false;
        Level = 0;
        Peak = 0;
        Changed?.Invoke();
    }

    private void Push(float raw)
    {
        if (!IsRunning) return;
        var scaled = Math.Min(1f, MathF.Sqrt(raw) * 3.2f);
        Level = Math.Max(scaled, Level * 0.75f); // jumps up at once, falls back gently
        Peak = Math.Max(scaled, Peak * 0.97f);
        Changed?.Invoke();
    }
}
