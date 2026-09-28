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
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4)), int.MaxValue);
            var body = offset + 8;
            if (id.SequenceEqual("data"u8))
            {
                var end = (int)Math.Min((long)body + size, data.Length);
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

/// <summary>Streams samples to disk as they arrive so a crash mid-recording never loses audio.</summary>
public sealed class WavFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _lock = new();
    private int _dataBytes;
    private bool _closed;

    public string Path { get; }

    public WavFileWriter(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _stream.Write(Wav.Header(0));
        _stream.Flush();
    }

    public void Append(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        lock (_lock)
        {
            if (_closed) return;
            _stream.Write(MemoryMarshal.AsBytes(samples));
            _dataBytes += samples.Length * 2;
        }
    }

    /// <summary>Patches the RIFF/data sizes so the file is valid. Safe to call more than once.</summary>
    public void FinalizeHeader()
    {
        lock (_lock)
        {
            if (_closed) return;
            Span<byte> buffer = stackalloc byte[4];
            _stream.Seek(4, SeekOrigin.Begin);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(36 + _dataBytes));
            _stream.Write(buffer);
            _stream.Seek(40, SeekOrigin.Begin);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)_dataBytes);
            _stream.Write(buffer);
            _stream.Seek(0, SeekOrigin.End);
            _stream.Flush(flushToDisk: true);
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
            _stream.Dispose();
        }
    }

    public void Dispose() => Close();
}
