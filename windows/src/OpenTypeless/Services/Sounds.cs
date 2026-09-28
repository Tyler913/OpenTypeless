using System.Runtime.InteropServices;
using OpenTypeless.Native;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// The soft start / stop chimes (the macOS app uses the system "Tink" and "Pop" at 35 % volume). They're
/// synthesised once in memory, so nothing depends on which sounds a Windows edition ships.
/// </summary>
public static class Sounds
{
    public enum Kind { Start, Stop }

    private static readonly Lazy<nint> StartSound = new(() => Pin(Tink()));
    private static readonly Lazy<nint> StopSound = new(() => Pin(Pop()));

    public static void Play(Kind kind)
    {
        var pointer = kind == Kind.Start ? StartSound.Value : StopSound.Value;
        Win32.PlaySound(pointer, 0, Win32.SND_MEMORY | Win32.SND_ASYNC | Win32.SND_NODEFAULT);
    }

    /// <summary>PlaySound reads the buffer while playing asynchronously, so it must never move or be freed.</summary>
    private static nint Pin(byte[] wav)
    {
        var pointer = Marshal.AllocHGlobal(wav.Length);
        Marshal.Copy(wav, 0, pointer, wav.Length);
        return pointer;
    }

    private const int Rate = 44_100;
    private const double Volume = 0.35;

    /// <summary>A short, bright "tink": two inharmonic partials with a fast exponential decay.</summary>
    private static byte[] Tink()
    {
        var n = (int)(Rate * 0.16);
        var samples = new short[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var envelope = Math.Exp(-t * 38) * Math.Min(1, t / 0.002);
            var s = 0.62 * Math.Sin(2 * Math.PI * 2093 * t) + 0.28 * Math.Sin(2 * Math.PI * 5230 * t) + 0.1 * Math.Sin(2 * Math.PI * 3140 * t);
            samples[i] = (short)(s * envelope * Volume * short.MaxValue);
        }
        return Wav.Encode(samples, Rate);
    }

    /// <summary>A soft "pop": a quick downward pitch sweep, like a bubble.</summary>
    private static byte[] Pop()
    {
        var n = (int)(Rate * 0.12);
        var samples = new short[n];
        var phase = 0.0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var frequency = 420 + 520 * Math.Exp(-t * 45);
            phase += 2 * Math.PI * frequency / Rate;
            var envelope = Math.Exp(-t * 42) * Math.Min(1, t / 0.003);
            samples[i] = (short)(Math.Sin(phase) * envelope * Volume * 1.2 * short.MaxValue);
        }
        return Wav.Encode(samples, Rate);
    }
}
