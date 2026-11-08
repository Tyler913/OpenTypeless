using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>Playing a dictation's recording from History: which records offer it, and where a recording's samples are.</summary>
public class RecordingPlaybackTests
{
    /// <summary>testdata/playback-cases.json, shared with the macOS tests so both apps play recordings the same way.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "playback-cases.json")))!.AsObject();

    [Fact]
    public void PlayableMatchesSharedCases()
    {
        foreach (var node in Cases()["playable"]!.AsArray())
        {
            Assert.True(RecordingPlayback.IsPlayable((string)node!["status"]!, (bool)node["hasAudio"]!) == (bool)node["playable"]!,
                        node.ToJsonString());
        }
    }

    [Fact]
    public void SoundsMatchSharedCases()
    {
        foreach (var node in Cases()["sounds"]!.AsArray())
        {
            var name = (string)node!["name"]!;
            var sound = RecordingPlayback.Sound(Convert.FromHexString((string)node["header"]!), (long)node["fileSize"]!);
            if (node["sound"] is JsonObject expected)
            {
                Assert.True(sound == new RecordingPlayback.SoundRange((long)expected["offset"]!, (long)expected["byteCount"]!,
                                                                       (bool)expected["headerIsRight"]!), name);
            }
            else
            {
                Assert.True(sound == null, name);
            }
        }
    }

    /// <summary>The file WavFileWriter leaves, finalized or not (the app quit while recording), and what a player is then given.</summary>
    [Fact]
    public void RecordingsAsTheWriterLeavesThem()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var samples = Enumerable.Range(0, 16_000).Select(i => unchecked((short)(i * 7))).ToArray();
            var finished = Path.Combine(folder, "finished.wav");
            using (var writer = new WavFileWriter(finished)) writer.Append(samples);
            var file = File.ReadAllBytes(finished);
            var sound = RecordingPlayback.Sound(file.AsSpan(0, Math.Min(file.Length, RecordingPlayback.HeaderBytes)), file.Length);
            Assert.True(sound == new RecordingPlayback.SoundRange(44, 32_000, true));
            Assert.Equal(1.0, sound!.Value.Seconds);

            // Never finalized: the header still says no samples. Given the right header, the samples are all there.
            var unfinished = (byte[])file.Clone();
            Array.Clear(unfinished, 40, 4);
            if (RecordingPlayback.Sound(unfinished.AsSpan(0, Math.Min(unfinished.Length, RecordingPlayback.HeaderBytes)), unfinished.Length)
                is not { } repaired)
            {
                Assert.Fail("no sound in a recording never finalized");
                return;
            }
            Assert.False(repaired.HeaderIsRight);
            var playable = Wav.Header((int)repaired.ByteCount)
                .Concat(unfinished.Skip((int)repaired.Offset).Take((int)repaired.ByteCount)).ToArray();
            Assert.Equal(samples, Wav.DecodeSamples(playable));
            Assert.Equal(file, playable);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
