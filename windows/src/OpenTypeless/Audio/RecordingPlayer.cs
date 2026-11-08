using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using OpenTypeless.Services;
using TypelessCore;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace OpenTypeless.Audio;

/// <summary>
/// Plays a dictation's recording in History (see <see cref="RecordingPlayback"/>), on the output device Windows uses.
/// One at a time: starting another stops it, and so do a dictation starting (the microphone would hear it), History
/// going away (another page, the window closed, another dictation selected) and the record or its recording being
/// deleted, which stops it before the file goes. UI-thread only; the player's own events are brought back to it.
/// </summary>
public sealed class RecordingPlayer
{
    public static RecordingPlayer Shared { get; } = new();

    /// <summary>Raised on the UI thread when what plays, whether it plays, or how far it has got changes.</summary>
    public event Action? Changed;

    /// <summary>The record whose recording is playing or paused; null when none is.</summary>
    public string? RecordId { get; private set; }
    public bool IsPlaying { get; private set; }
    /// <summary>How far it has got and how long it is, in seconds.</summary>
    public double Elapsed { get; private set; }
    public double Duration { get; private set; }

    private MediaPlayer? _player;
    private MediaSource? _source;
    private Stream? _stream;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _ticker;

    private RecordingPlayer() { }

    /// <summary>
    /// Plays the record's recording, or pauses it or carries on when it's the one already playing. False when it
    /// can't be read.
    /// </summary>
    public bool Toggle(DictationRecord record)
    {
        if (record.Id == RecordId && _player != null)
        {
            if (IsPlaying)
            {
                _player.Pause();
                IsPlaying = false;
                _ticker?.Stop();
            }
            else
            {
                Play(_player);
            }
            Changed?.Invoke();
            return true;
        }
        Stop();
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        try
        {
            var (stream, sound) = Open(record.AudioPath);
            _stream = stream;
            // Kept as soon as they exist, so Stop lets go of them whatever goes wrong next.
            var player = new MediaPlayer { AutoPlay = false };
            _player = player;
            player.MediaEnded += OnEnded;
            player.MediaFailed += OnFailed;
            // Not a media session of the system's (the media keys and their overlay): it's a preview in a settings page.
            player.CommandManager.IsEnabled = false;
            _source = MediaSource.CreateFromStream(stream.AsRandomAccessStream(), "audio/wav");
            player.Source = _source;
            RecordId = record.Id;
            Duration = sound.Seconds;
            Elapsed = 0;
            Play(player);
        }
        catch (Exception error)
        {
            // Gone, unreadable, not a recording, or refused by Media Foundation: in a click handler any of these would
            // end the app.
            AppLog.Debug("playback", $"couldn't play {record.Id}: {error.Message}");
            Stop();
            return false;
        }
        Changed?.Invoke();
        return true;
    }

    public void Stop()
    {
        _ticker?.Stop();
        var player = _player;
        _player = null;
        if (player != null)
        {
            player.MediaEnded -= OnEnded;
            player.MediaFailed -= OnFailed;
            try
            {
                player.Pause();
                player.Source = null;
            }
            catch (COMException) { }
            player.Dispose();
        }
        _source?.Dispose();
        _source = null;
        _stream?.Dispose();
        _stream = null;
        var wasActive = RecordId != null;
        RecordId = null;
        IsPlaying = false;
        Elapsed = 0;
        Duration = 0;
        if (wasActive) Changed?.Invoke();
    }

    /// <summary>Stops it if it's this record's recording that is playing or paused.</summary>
    public void Stop(string recordId)
    {
        if (RecordId == recordId) Stop();
    }

    private void Play(MediaPlayer player)
    {
        player.Play();
        IsPlaying = true;
        if (_ticker == null && _dispatcher != null)
        {
            _ticker = _dispatcher.CreateTimer();
            _ticker.Interval = TimeSpan.FromMilliseconds(200);
            _ticker.Tick += (_, _) => Tick();
        }
        _ticker?.Start();
    }

    /// <summary>Moves the time on.</summary>
    private void Tick()
    {
        if (_player is not { } player || !IsPlaying) return;
        try
        {
            Elapsed = Math.Min(Duration, player.PlaybackSession.Position.TotalSeconds);
        }
        catch (COMException)
        {
            return;
        }
        Changed?.Invoke();
    }

    private void OnEnded(MediaPlayer sender, object args) =>
        _dispatcher?.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _player)) Stop();
        });

    private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = args.ErrorMessage;
        _dispatcher?.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _player)) return;
            AppLog.Debug("playback", $"{RecordId} failed: {message}");
            Stop();
        });
    }

    /// <summary>
    /// The recording to play: the file itself, or, for one never finalized (the app quit while recording), its samples
    /// behind a header that says how long they are, since players stop at once at a length of 0. The file is opened so
    /// that deleting it isn't held up.
    /// </summary>
    private static (Stream Stream, RecordingPlayback.SoundRange Sound) Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var header = new byte[(int)Math.Min(file.Length, RecordingPlayback.HeaderBytes)];
            file.ReadExactly(header);
            if (RecordingPlayback.Sound(header, file.Length) is not { } sound) throw new InvalidDataException("not a 16 kHz mono 16-bit WAV file");
            if (sound.HeaderIsRight)
            {
                file.Seek(0, SeekOrigin.Begin);
                return (file, sound);
            }
            var samples = new byte[sound.ByteCount];
            file.Seek(sound.Offset, SeekOrigin.Begin);
            file.ReadExactly(samples);
            file.Dispose();
            var head = Wav.Header(samples.Length);
            var memory = new MemoryStream(head.Length + samples.Length);
            memory.Write(head);
            memory.Write(samples);
            memory.Position = 0;
            return (memory, sound);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }
}
