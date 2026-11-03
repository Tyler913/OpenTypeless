namespace OpenTypeless.Audio;

/// <summary>
/// The microphones and the default one, read off the UI thread for the General page. Listing the endpoints and reading
/// each one's properties goes through the audio service and takes tens of milliseconds (more with Bluetooth devices
/// about); on the UI thread, every 1.5 s while the page was open, it held clicks up. The last read is kept, so the page
/// opens with it.
/// </summary>
public static class MicrophoneList
{
    public sealed record Snapshot(List<MicrophoneInfo> All, MicrophoneInfo? Default);

    private static volatile Snapshot? _last;
    private static Task<Snapshot>? _reading;
    private static readonly object Lock = new();

    /// <summary>The newest read, or null before the first one is in.</summary>
    public static Snapshot? Last => _last;

    /// <summary>Reads the devices on a worker thread; one read at a time (asked for meanwhile, it's that read).</summary>
    public static Task<Snapshot> ReadAsync()
    {
        lock (Lock)
        {
            if (_reading is { IsCompleted: false } running) return running;
            return _reading = Task.Run(() =>
            {
                var snapshot = new Snapshot(AudioRecorder.Microphones(), AudioRecorder.DefaultMicrophone());
                _last = snapshot;
                return snapshot;
            });
        }
    }
}
