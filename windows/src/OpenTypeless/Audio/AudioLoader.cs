using TypelessCore;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OpenTypeless.Audio;

/// <summary>
/// Reads the sound of an audio or video file Windows can decode (WAV, MP3, M4A, WMA, FLAC, MP4, MOV…) as 16 kHz mono
/// 16-bit samples, written to a WAV file by the system transcoder (Media Foundation): the app writes it straight into the
/// record's recording, so an hour of audio is never all in memory, and the command line's <c>--transcribe-file</c> reads
/// it back whole (<see cref="Load"/>). A WAV already in that format is copied as it is. Shared by the app and the
/// command line; what goes wrong is a <see cref="FileTranscriptionException"/>.
/// </summary>
public static class AudioLoader
{
    private static readonly string[] AudioExtensions =
        [".m4a", ".mp3", ".wav", ".wma", ".aac", ".flac", ".aif", ".aiff", ".ogg", ".opus", ".amr"];
    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".3gp"];

    /// <summary>What the file picker offers and drops are accepted as.</summary>
    public static IEnumerable<string> Extensions => AudioExtensions.Concat(VideoExtensions);

    public static bool IsMedia(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's length in seconds (0 when Windows doesn't say), once it's known to be there and readable.</summary>
    public static async Task<double> Duration(string path)
    {
        var file = await Open(path);
        if (await HasNoAudioTrack(file)) throw new FileTranscriptionException(FileTranscriptionException.Kind.NoAudioTrack);
        try
        {
            var music = await file.Properties.GetMusicPropertiesAsync();
            if (music.Duration > TimeSpan.Zero) return music.Duration.TotalSeconds;
            var video = await file.Properties.GetVideoPropertiesAsync();
            return Math.Max(0, video.Duration.TotalSeconds);
        }
        catch (Exception)
        {
            // The property system doesn't know the format; the transcoder may still read it.
            return 0;
        }
    }

    /// <summary>
    /// Writes the sound of <paramref name="input"/> to <paramref name="output"/> as a 16 kHz mono PCM16 WAV.
    /// <paramref name="progress"/> gets how far it has got, 0…1, on a background thread.
    /// </summary>
    public static async Task Transcode(string input, string output, IProgress<double>? progress = null,
                                       CancellationToken cancellationToken = default)
    {
        var file = await Open(input);
        if (IsReadyAsItIs(file.Path))
        {
            await Copy(file.Path, output, cancellationToken);
            progress?.Report(1);
            return;
        }
        if (await HasNoAudioTrack(file)) throw new FileTranscriptionException(FileTranscriptionException.Kind.NoAudioTrack);
        StorageFile target;
        try
        {
            var full = Path.GetFullPath(output);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(full)!);
            target = await folder.CreateFileAsync(Path.GetFileName(full), CreationCollisionOption.ReplaceExisting);
        }
        catch (Exception error)
        {
            throw FileTranscriptionException.Writing(error);
        }
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm(AudioFormat.SampleRate, 1, 16);
        PrepareTranscodeResult prepared;
        try
        {
            prepared = await new MediaTranscoder().PrepareFileTranscodeAsync(file, target, profile);
        }
        catch (Exception error)
        {
            throw FileTranscriptionException.Reading(error);
        }
        if (!prepared.CanTranscode)
        {
            throw new FileTranscriptionException(FileTranscriptionException.Kind.Unsupported, prepared.FailureReason.ToString());
        }
        try
        {
            await prepared.TranscodeAsync().AsTask(cancellationToken, new Percent(progress));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw FileTranscription.IsDiskFull(error) ? FileTranscriptionException.Writing(error) : FileTranscriptionException.Reading(error);
        }
    }

    /// <summary>The whole sound of <paramref name="path"/> at once, for the command line (the app streams it from disk).</summary>
    public static async Task<short[]> Load(string path)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"opentypeless-{Guid.NewGuid():N}.wav");
        try
        {
            await Transcode(path, temp);
            using var reader = new WavFileReader(temp);
            var samples = new List<short>(reader.SampleCount);
            while (reader.Read(1 << 16) is { } block) samples.AddRange(block);
            return samples.ToArray();
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// The file, once it's known to be there and readable: opening it tells a file the user may not read apart from a
    /// damaged one, which the transcoder doesn't.
    /// </summary>
    private static async Task<StorageFile> Open(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception error)
        {
            throw FileTranscriptionException.Reading(error);
        }
        if (!File.Exists(full)) throw new FileTranscriptionException(FileTranscriptionException.Kind.NotFound);
        try
        {
            using (new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
            return await StorageFile.GetFileFromPathAsync(full);
        }
        catch (Exception error)
        {
            throw FileTranscriptionException.Reading(error);
        }
    }

    /// <summary>A WAV already 16 kHz mono PCM16, which needs no transcoding.</summary>
    private static bool IsReadyAsItIs(string path)
    {
        try
        {
            using var reader = new WavFileReader(path);
            return reader.SampleCount > 0;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task Copy(string source, string destination, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw FileTranscriptionException.Writing(error);
        }
    }

    /// <summary>
    /// Whether a video has no audio track: the transcoder would only say it can't transcode it. An audio file, or a video
    /// Windows can't open as a clip, is left to the transcoder to judge.
    /// </summary>
    private static async Task<bool> HasNoAudioTrack(StorageFile file)
    {
        if (!file.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file);
            return clip.EmbeddedAudioTracks.Count == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The transcoder's percentage as 0…1.</summary>
    private sealed class Percent(IProgress<double>? target) : IProgress<double>
    {
        public void Report(double value) => target?.Report(Math.Clamp(value / 100, 0, 1));
    }
}
