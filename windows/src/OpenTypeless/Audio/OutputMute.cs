using System.Runtime.InteropServices;
using OpenTypeless.Services;

namespace OpenTypeless.Audio;

/// <summary>
/// Silences the speakers while recording, so music or a video doesn't play on over the dictation, and turns them back
/// on afterwards, by muting the default output endpoint (every endpoint has a mute, in software where the hardware has
/// none). Only what this changed is undone: an output that was already muted stays muted. UI thread only.
/// </summary>
public sealed class OutputMute
{
    /// <summary>
    /// The endpoint ID of the output this muted, kept until it's unmuted, so a crash mid-recording doesn't leave the PC
    /// silent (see <see cref="RestoreLeftover"/>).
    /// </summary>
    private static string MarkerPath => Path.Combine(AppPaths.Support, "muted-output.txt");

    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private CancellationTokenSource? _pending;

    /// <summary>Mutes the default output after <paramref name="delay"/> (so the start sound isn't cut off), unless <see cref="Restore"/> comes first.</summary>
    public void Mute(TimeSpan delay)
    {
        _pending?.Cancel();
        var pending = new CancellationTokenSource();
        _pending = pending;
        _ = MuteLater();

        async Task MuteLater()
        {
            try { await Task.Delay(delay, pending.Token); } catch (OperationCanceledException) { return; }
            MuteNow();
        }
    }

    public void Restore()
    {
        _pending?.Cancel();
        _pending = null;
        Undo();
    }

    /// <summary>Unmutes an output a crash or a forced shutdown left muted. Called at launch.</summary>
    public static void RestoreLeftover() => Undo();

    private static void MuteNow()
    {
        if (File.Exists(MarkerPath)) return;
        WithEndpoint(null, (id, volume) =>
        {
            if (volume.GetMute(out var muted) < 0 || muted) return;
            // Written first: if the app dies in between, unmuting an output that never got muted is harmless.
            File.WriteAllText(MarkerPath, id);
            volume.SetMute(true, 0);
        });
    }

    private static void Undo()
    {
        string id;
        try
        {
            if (!File.Exists(MarkerPath)) return;
            id = File.ReadAllText(MarkerPath).Trim();
            File.Delete(MarkerPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("audio", "couldn't read what was muted: " + error.Message);
            return;
        }
        // The output may have been unplugged meanwhile; then there's nothing left to undo.
        if (id.Length > 0) WithEndpoint(id, (_, volume) => volume.SetMute(false, 0));
    }

    /// <summary>Runs <paramref name="action"/> on an output's volume control: the one with this ID, or the default output.</summary>
    private static void WithEndpoint(string? id, Action<string, IAudioEndpointVolume> action)
    {
        AudioRecorder.IMMDeviceEnumerator? enumerator = null;
        AudioRecorder.IMMDevice? device = null;
        object? volume = null;
        try
        {
            enumerator = (AudioRecorder.IMMDeviceEnumerator)new AudioRecorder.MMDeviceEnumeratorComObject();
            var found = id == null
                ? enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device)
                : enumerator.GetDevice(id, out device);
            if (found < 0 || device == null || device.GetId(out var deviceId) < 0) return;
            var iid = IID_IAudioEndpointVolume;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, 0, out volume) < 0) return;
            action(deviceId, (IAudioEndpointVolume)volume);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("audio", "couldn't change the output's mute: " + error.Message);
        }
        finally
        {
            if (volume != null) Marshal.ReleaseComObject(volume);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDB, nint eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDB, nint eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
