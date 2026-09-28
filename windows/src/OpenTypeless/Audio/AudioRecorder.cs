using System.Runtime.InteropServices;
using TypelessCore;

namespace OpenTypeless.Audio;

/// <summary>
/// Captures the default microphone and delivers 16 kHz mono Int16 samples, using WASAPI shared mode with the
/// system resampler (<c>AUTOCONVERTPCM</c>) so no conversion code runs in the app.
///
/// Capture runs on its own thread. If the default input changes mid-recording (a headset connects) or the device
/// disappears, the stream is reopened on the new default device so a long dictation keeps going, like the
/// macOS engine rebuild on a route change.
/// </summary>
public sealed class AudioRecorder
{
    /// <summary>Called on the capture thread, in order.</summary>
    public Action<short[]>? OnSamples;
    /// <summary>Called on the capture thread with the RMS (0…1) of each delivered block.</summary>
    public Action<float>? OnLevel;
    /// <summary>Called on the capture thread when capture fails irrecoverably.</summary>
    public Action<Exception>? OnFailure;

    public bool IsRecording { get; private set; }

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _reopenRequested;
    private AutoResetEvent? _wake;

    public sealed class RecorderException(string message) : Exception(message);

    /// <summary>Opens the device and starts capturing; throws if the microphone can't be opened.</summary>
    public void Start()
    {
        if (IsRecording) return;
        _stopRequested = false;
        _reopenRequested = false;
        _wake = new AutoResetEvent(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => CaptureLoop(started)) { IsBackground = true, Name = "OpenTypeless capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try
        {
            if (!started.Task.Wait(TimeSpan.FromSeconds(5))) throw new RecorderException(L("打开麦克风超时", "Timed out opening the microphone"));
        }
        catch (AggregateException e) when (e.InnerException != null)
        {
            _thread.Join(1000);
            throw e.InnerException;
        }
        catch
        {
            _stopRequested = true;
            _wake?.Set();
            throw;
        }
        IsRecording = true;
    }

    /// <summary>Stops capturing. Everything captured so far has been delivered through <see cref="OnSamples"/> on return.</summary>
    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        _stopRequested = true;
        _wake?.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _wake?.Dispose();
        _wake = null;
    }

    private void CaptureLoop(TaskCompletionSource started)
    {
        // Developer aid: OPENTYPELESS_TEST_AUDIO=<16 kHz mono WAV> replaces the microphone with that file, streamed
        // in real time, so the whole app can be tested end to end on machines without a usable microphone.
        if (Environment.GetEnvironmentVariable("OPENTYPELESS_TEST_AUDIO") is { Length: > 0 } testAudio)
        {
            PlayTestAudio(testAudio, started);
            return;
        }
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        var notifications = new DeviceNotifications(this);
        enumerator.RegisterEndpointNotificationCallback(notifications);
        var pending = new List<short>(2048);
        try
        {
            var first = true;
            while (!_stopRequested)
            {
                Stream? stream;
                try
                {
                    stream = Stream.Open(enumerator, _wake!);
                }
                catch (Exception error)
                {
                    if (first)
                    {
                        started.TrySetException(error);
                        return;
                    }
                    Flush(pending);
                    OnFailure?.Invoke(error);
                    return;
                }
                if (first) started.TrySetResult();
                first = false;
                _reopenRequested = false;

                try
                {
                    while (!_stopRequested && !_reopenRequested)
                    {
                        _wake!.WaitOne(100);
                        stream.Drain(pending);
                        // ~40 ms blocks: small enough for a lively level meter, like the macOS tap size.
                        if (pending.Count >= 640) Flush(pending);
                    }
                    if (_stopRequested) stream.Drain(pending);
                }
                catch (COMException)
                {
                    // Device invalidated (unplugged, format change…): reopen on whatever is the default now.
                }
                finally
                {
                    stream.Dispose();
                }
            }
        }
        finally
        {
            Flush(pending);
            enumerator.UnregisterEndpointNotificationCallback(notifications);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void PlayTestAudio(string path, TaskCompletionSource started)
    {
        short[] samples;
        try
        {
            samples = Wav.DecodeSamples(File.ReadAllBytes(path)) ?? throw new RecorderException("OPENTYPELESS_TEST_AUDIO is not a WAV file");
        }
        catch (Exception error)
        {
            started.TrySetException(error);
            return;
        }
        started.TrySetResult();
        const int block = 640; // 40 ms
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;
        var pending = new List<short>(block);
        while (!_stopRequested)
        {
            _wake!.WaitOne(10);
            var due = (int)(clock.Elapsed.TotalSeconds * AudioFormat.SampleRate);
            while (sent + block <= due)
            {
                for (var i = 0; i < block; i++) pending.Add(sent + i < samples.Length ? samples[sent + i] : (short)0);
                sent += block;
                Flush(pending);
            }
        }
    }

    private void Flush(List<short> pending)
    {
        if (pending.Count == 0) return;
        var samples = pending.ToArray();
        pending.Clear();
        OnSamples?.Invoke(samples);
        OnLevel?.Invoke(AudioLevel.Rms(samples));
    }

    /// <summary>One open WASAPI capture stream.</summary>
    private sealed class Stream : IDisposable
    {
        private const int eCapture = 1, eConsole = 0, CLSCTX_ALL = 23, AUDCLNT_SHAREMODE_SHARED = 0;
        private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int E_NOTFOUND = unchecked((int)0x80070490);
        private const int E_ACCESSDENIED = unchecked((int)0x80070005);

        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        private readonly IMMDevice _device;
        private readonly IAudioClient _client;
        private readonly IAudioCaptureClient _capture;

        private Stream(IMMDevice device, IAudioClient client, IAudioCaptureClient capture)
        {
            _device = device;
            _client = client;
            _capture = capture;
        }

        public static Stream Open(IMMDeviceEnumerator enumerator, AutoResetEvent wake)
        {
            var hr = enumerator.GetDefaultAudioEndpoint(eCapture, eConsole, out var device);
            if (hr == E_NOTFOUND || device == null) throw new RecorderException(L("没有可用的麦克风", "No microphone available"));
            Check(hr);
            var iid = IID_IAudioClient;
            hr = device.Activate(ref iid, CLSCTX_ALL, 0, out var clientObject);
            if (hr == E_ACCESSDENIED) throw Denied();
            Check(hr);
            var client = (IAudioClient)clientObject;
            var format = new WAVEFORMATEX
            {
                wFormatTag = 1, // PCM
                nChannels = 1,
                nSamplesPerSec = AudioFormat.SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = AudioFormat.SampleRate * 2,
            };
            hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED,
                                   AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                   2_000_000 /* 200 ms */, 0, ref format, 0);
            if (hr == E_ACCESSDENIED) throw Denied();
            Check(hr);
            Check(client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle()));
            var captureIid = IID_IAudioCaptureClient;
            Check(client.GetService(ref captureIid, out var captureObject));
            var stream = new Stream(device, client, (IAudioCaptureClient)captureObject);
            Check(client.Start());
            return stream;
        }

        private static RecorderException Denied() =>
            new(L("没有麦克风权限：设置 → 隐私和安全性 → 麦克风", "No microphone access: Settings → Privacy & security → Microphone"));

        private static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public unsafe void Drain(List<short> into)
        {
            while (true)
            {
                Check(_capture.GetNextPacketSize(out var packet));
                if (packet == 0) return;
                Check(_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == 0)
                {
                    for (var i = 0; i < frames; i++) into.Add(0);
                }
                else
                {
                    into.AddRange(new ReadOnlySpan<short>((void*)data, (int)frames));
                }
                Check(_capture.ReleaseBuffer(frames));
            }
        }

        public void Dispose()
        {
            try { _client.Stop(); } catch (COMException) { }
            Marshal.ReleaseComObject(_capture);
            Marshal.ReleaseComObject(_client);
            Marshal.ReleaseComObject(_device);
        }
    }

    /// <summary>Default-device changes arrive on a system thread; they only raise a flag for the capture loop.</summary>
    [ComVisible(true)]
    public sealed class DeviceNotifications(AudioRecorder owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow != 1 /* eCapture */ || role != 0 /* eConsole */ || !owner.IsRecording) return;
            owner._reopenRequested = true;
            try { owner._wake?.Set(); } catch (ObjectDisposedException) { }
        }

        public void OnDeviceStateChanged(string deviceId, int newState) { }
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }

    // MARK: COM declarations

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, nint sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }
}
