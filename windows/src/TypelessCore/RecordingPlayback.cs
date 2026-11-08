using System.Buffers.Binary;

namespace TypelessCore;

/// <summary>
/// Playing a dictation's recording back from History, to hear what was said next to what was transcribed. A recording
/// is one <c>audio.wav</c>, written as it was spoken (the chunks are cut from it in memory, never saved apart), so the
/// whole file, played once from the start, is what the speaker said. A transcribed file's is the copy the app decoded,
/// never the original.
/// </summary>
public static class RecordingPlayback
{
    /// <summary>
    /// Whether History offers to play a record's recording (<paramref name="status"/> as in session.json): there is one,
    /// and it's no longer being written. Once recording ends the file is complete, also while the dictation is processed.
    /// </summary>
    public static bool IsPlayable(string status, bool hasAudio) => hasAudio && status != "recording";

    /// <summary>
    /// How many bytes from the start of the file <see cref="Sound"/> is given: the header and any chunk before the
    /// samples fit easily.
    /// </summary>
    public const int HeaderBytes = 4096;

    /// <summary>Where the samples are in a recording's WAV file.</summary>
    /// <param name="Offset">Where the first sample starts, in bytes from the start of the file.</param>
    /// <param name="ByteCount">How many bytes of samples there are, a whole number of samples.</param>
    /// <param name="HeaderIsRight">
    /// The header gives that length itself, so the file plays as it is. A recording that was never finalized (the app
    /// quit or crashed while recording) says 0 there, and players stop at once; it's played with a header that says the
    /// right length (<see cref="Wav.Header"/>) in front of these bytes.
    /// </param>
    public readonly record struct SoundRange(long Offset, long ByteCount, bool HeaderIsRight)
    {
        public double Seconds => (double)(ByteCount / AudioFormat.BytesPerSample) / AudioFormat.SampleRate;
    }

    /// <summary>
    /// The samples of a 16 kHz mono 16-bit WAV, read from its first bytes (<paramref name="header"/>, at least up to the
    /// start of the samples) and its size, as <see cref="WavFileReader"/> reads them: an unknown length (0, as a recording
    /// never finalized says, or 0xFFFFFFFF from a streaming writer) or one longer than the file is the rest of the file.
    /// Null when there is nothing to play: not such a WAV, or no samples.
    /// </summary>
    public static SoundRange? Sound(ReadOnlySpan<byte> header, long fileSize)
    {
        if (header.Length < 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) return null;
        long offset = 12;
        var formatOk = false;
        while (offset + 8 <= header.Length)
        {
            var at = (int)offset;
            var id = header.Slice(at, 4);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(at + 4, 4));
            var body = offset + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (body + 16 > header.Length) return null;
                var format = header.Slice((int)body, 16);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                // 1 = PCM; 0xFFFE = WAVE_FORMAT_EXTENSIBLE, which some writers (Media Foundation) use for plain PCM too.
                formatOk = (tag is 1 or 0xFFFE) && channels == 1 && rate == AudioFormat.SampleRate && bits == 16;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!formatOk || fileSize <= body) return null;
                var available = fileSize - body;
                var known = declared != 0 && declared != uint.MaxValue && declared <= available;
                var length = known ? declared : available;
                var count = length - length % AudioFormat.BytesPerSample;
                if (count <= 0) return null;
                return new SoundRange(body, count, known && count == length);
            }
            offset = body + declared + (declared % 2);
        }
        return null;
    }
}
