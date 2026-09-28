using TypelessCore;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OpenTypeless.Cli;

/// <summary>
/// Reads any audio file Windows can decode and converts it to 16 kHz mono Int16. A WAV that is already in that
/// format is read directly; everything else goes through the system transcoder (Media Foundation).
/// </summary>
public static class AudioLoader
{
    public static async Task<short[]> Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("file not found", full);
        var bytes = await File.ReadAllBytesAsync(full);
        if (Wav.ReadFormat(bytes) is { Format: 1, Channels: 1, SampleRate: AudioFormat.SampleRate, BitsPerSample: 16 }
            && Wav.DecodeSamples(bytes) is { } direct)
        {
            return direct;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"opentypeless-{Guid.NewGuid():N}.wav");
        try
        {
            var input = await StorageFile.GetFileFromPathAsync(full);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(temp)!);
            var output = await folder.CreateFileAsync(Path.GetFileName(temp), CreationCollisionOption.ReplaceExisting);
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            profile.Audio = AudioEncodingProperties.CreatePcm(AudioFormat.SampleRate, 1, 16);
            var transcoder = new MediaTranscoder();
            var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, profile);
            if (!prepared.CanTranscode) throw new InvalidDataException($"unsupported format ({prepared.FailureReason})");
            await prepared.TranscodeAsync();
            var converted = await File.ReadAllBytesAsync(temp);
            return Wav.DecodeSamples(converted) ?? throw new InvalidDataException("the converted file has no audio data");
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
