using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace TypelessCore;

public static class AudioFormat
{
    /// <summary>Everything downstream works on 16 kHz mono signed 16-bit PCM.</summary>
    public const int SampleRate = 16_000;
    public const int BytesPerSample = 2;

    public static double Seconds(int sampleCount) => (double)sampleCount / SampleRate;

    public static int SampleCount(double seconds) => (int)(seconds * SampleRate);
}

public static class Wav
{
    /// <summary>44-byte canonical RIFF header followed by little-endian PCM16 samples.</summary>
    public static byte[] Encode(ReadOnlySpan<short> samples, int sampleRate = AudioFormat.SampleRate)
    {
        var data = new byte[44 + samples.Length * 2];
        WriteHeader(data, samples.Length * 2, sampleRate);
        // x64 and ARM64 Windows are both little-endian, so the raw buffer is already in WAV order.
        MemoryMarshal.AsBytes(samples).CopyTo(data.AsSpan(44));
        return data;
    }

    public static byte[] Header(int dataByteCount, int sampleRate = AudioFormat.SampleRate)
    {
        var data = new byte[44];
        WriteHeader(data, dataByteCount, sampleRate);
        return data;
    }

    private static void WriteHeader(Span<byte> d, int dataByteCount, int sampleRate)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        var byteRate = (uint)sampleRate * channels * bitsPerSample / 8;
        const ushort blockAlign = channels * bitsPerSample / 8;

        Encoding.ASCII.GetBytes("RIFF", d);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], (uint)(36 + dataByteCount));
        Encoding.ASCII.GetBytes("WAVE", d[8..]);
        Encoding.ASCII.GetBytes("fmt ", d[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(d[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(d[22..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(d[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(d[28..], byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(d[32..], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(d[34..], bitsPerSample);
        Encoding.ASCII.GetBytes("data", d[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(d[40..], (uint)dataByteCount);
    }

    /// <summary>Reads PCM16 mono samples back out of a canonical WAV (as written by <see cref="Encode"/> / <see cref="WavFileWriter"/>).</summary>
    public static short[]? DecodeSamples(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8)) return null;
        // Walk chunks to find "data", so files with extra chunks still work.
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = data.Slice(offset, 4);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var size = (int)Math.Min(declared, int.MaxValue);
            var body = offset + 8;
            if (id.SequenceEqual("data"u8))
            {
                // A recording that was never finalized (the app quit or crashed mid-way) still says 0 here, streaming
                // writers put 0xFFFFFFFF, and a cut-off file says more than it has: then the audio is the rest of the file.
                var unknown = declared == 0 || declared == uint.MaxValue || (long)body + declared > data.Length;
                var end = unknown ? data.Length : body + size;
                var count = (end - body) / 2;
                var samples = new short[count];
                data.Slice(body, count * 2).CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
                return samples;
            }
            offset = (int)Math.Min((long)body + size + (size % 2), int.MaxValue);
        }
        return null;
    }

    /// <summary>
    /// Describes the "fmt " chunk of a WAV file, or null. Used to decide whether a file can be read directly
    /// (16 kHz mono PCM16) or needs converting first.
    /// </summary>
    public static (int Format, int Channels, int SampleRate, int BitsPerSample)? ReadFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8)) return null;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4)), int.MaxValue);
            if (data.Slice(offset, 4).SequenceEqual("fmt "u8) && offset + 8 + 16 <= data.Length)
            {
                var f = data.Slice(offset + 8);
                return (BinaryPrimitives.ReadUInt16LittleEndian(f), BinaryPrimitives.ReadUInt16LittleEndian(f[2..]),
                        (int)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]), BinaryPrimitives.ReadUInt16LittleEndian(f[14..]));
            }
            offset = (int)Math.Min((long)offset + 8 + size + (size % 2), int.MaxValue);
        }
        return null;
    }

    internal static uint ReadLE32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
}

/// <summary>
/// Reads a 16 kHz mono PCM16 WAV (as <see cref="WavFileWriter"/> writes it) a block at a time, so a recording an hour long
/// is never all in memory: a transcribed file's recording is handed to the pipeline this way (see
/// <see cref="FileTranscription"/>). Throws <see cref="InvalidDataException"/> for a file that isn't such a WAV.
/// </summary>
public sealed class WavFileReader : IDisposable
{
    private readonly FileStream _stream;
    private long _remaining;

    /// <summary>How many samples the file holds.</summary>
    public int SampleCount { get; }

    public WavFileReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16);
        try
        {
            var fileSize = _stream.Length;
            var riff = new byte[12];
            if (ReadFully(riff) < 12 || !riff.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !riff.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new InvalidDataException("not a WAV file");
            }
            // Walks the chunks to "data", checking "fmt " on the way (files from other writers carry extra chunks).
            long offset = 12;
            var formatOk = false;
            long? dataBytes = null;
            var header = new byte[8];
            while (offset + 8 <= fileSize)
            {
                _stream.Seek(offset, SeekOrigin.Begin);
                if (ReadFully(header) < 8) break;
                var declared = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var body = offset + 8;
                if (header.AsSpan(0, 4).SequenceEqual("fmt "u8))
                {
                    var format = new byte[16];
                    if (ReadFully(format) < 16) break;
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                    var channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
                    var rate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
                    // 1 = PCM; 0xFFFE = WAVE_FORMAT_EXTENSIBLE, which some writers (Media Foundation) use for plain PCM too.
                    formatOk = (tag is 1 or 0xFFFE) && channels == 1 && rate == AudioFormat.SampleRate && bits == 16;
                }
                else if (header.AsSpan(0, 4).SequenceEqual("data"u8))
                {
                    // Never finalized (the app quit mid-way), written by a streaming writer, or cut off: then the audio is
                    // the rest of the file (as Wav.DecodeSamples reads it).
                    var unknown = declared == 0 || declared == uint.MaxValue || body + declared > fileSize;
                    dataBytes = unknown ? fileSize - body : declared;
                    break;
                }
                offset = body + declared + (declared % 2);
            }
            if (!formatOk || dataBytes is not { } bytes) throw new InvalidDataException("not a 16 kHz mono 16-bit WAV file");
            _remaining = bytes - bytes % 2;
            SampleCount = (int)Math.Min(_remaining / 2, int.MaxValue);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>The next block of at most <paramref name="maxSamples"/> samples; null once the whole file has been read.</summary>
    public short[]? Read(int maxSamples)
    {
        if (_remaining <= 0) return null;
        var bytes = new byte[(int)Math.Min(_remaining, Math.Max(1, maxSamples) * 2L)];
        var read = ReadFully(bytes);
        if (read < 2)
        {
            _remaining = 0;
            return null;
        }
        _remaining -= read;
        var samples = new short[read / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    private int ReadFully(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = _stream.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Streams samples to disk as they arrive so a crash mid-recording never loses audio.</summary>
public sealed class WavFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _lock = new();
    private int _dataBytes;
    private bool _closed;
    private Exception? _failure;

    public string Path { get; }

    /// <summary>
    /// The first write that failed (the disk filled up, the drive went away). Nothing more is written after it: the
    /// dictation goes on from the audio in memory, and the file keeps what reached it.
    /// </summary>
    public Exception? Failure
    {
        get { lock (_lock) return _failure; }
    }

    public WavFileWriter(string path)
    {
        Path = path;
        // Unbuffered (a buffer size of 1 turns it off), like the macOS FileHandle: each block is in the file as soon as
        // it's appended, so a recording the app never got to close (it quit or crashed) still has all its audio.
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
        _stream.Write(Wav.Header(0));
        _stream.Flush();
    }

    /// <summary>Runs on the capture thread, where an exception would end the app: a failed write is only noted.</summary>
    public void Append(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        lock (_lock)
        {
            if (_closed || _failure != null) return;
            try
            {
                _stream.Write(MemoryMarshal.AsBytes(samples));
                _dataBytes += samples.Length * 2;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _failure = error;
            }
        }
    }

    /// <summary>Patches the RIFF/data sizes so the file is valid. Safe to call more than once.</summary>
    public void FinalizeHeader()
    {
        lock (_lock)
        {
            if (_closed) return;
            Span<byte> buffer = stackalloc byte[4];
            try
            {
                _stream.Seek(4, SeekOrigin.Begin);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(36 + _dataBytes));
                _stream.Write(buffer);
                _stream.Seek(40, SeekOrigin.Begin);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)_dataBytes);
                _stream.Write(buffer);
                _stream.Seek(0, SeekOrigin.End);
                _stream.Flush(flushToDisk: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The header keeps saying 0 bytes, which Wav.DecodeSamples reads as "to the end of the file".
                _failure ??= error;
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_closed) return;
        }
        FinalizeHeader();
        lock (_lock)
        {
            _closed = true;
            try { _stream.Dispose(); } catch (IOException) { }
        }
    }

    public void Dispose() => Close();
}
