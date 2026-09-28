using System.Runtime.InteropServices;

namespace TypelessCore;

/// <summary>
/// Latin spelling of any script through ICU's "Any-Latin" transform (Chinese → pinyin), the counterpart of
/// Foundation's <c>applyingTransform(.toLatin)</c>. .NET has no transliteration API, so this calls the ICU
/// that ships with the OS: <c>icu.dll</c> on Windows 10 1903 and later, versioned <c>libicui18n</c> elsewhere
/// (used by the tests on Linux). Returns null when ICU isn't available.
/// </summary>
public static class Transliteration
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate nint OpenDelegate(string id, int idLength, int direction, nint rules, int rulesLength, nint parseError, ref int status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TransformDelegate(nint transliterator, nint text, ref int textLength, int capacity, int start, ref int limit, ref int status);

    private sealed record Icu(nint Transliterator, TransformDelegate Transform);

    private const string TransformId = "Any-Latin; Latin-ASCII";
    private const int BufferOverflowError = 15;
    private static readonly object Lock = new();
    private static readonly Lazy<Icu?> Instance = new(Open, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Instance.Value != null;

    public static string? ToLatin(string text)
    {
        if (text.Length == 0) return text;
        if (Instance.Value is not { } icu) return null;
        lock (Lock)
        {
            var capacity = text.Length * 8 + 64;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var buffer = new char[capacity];
                text.CopyTo(0, buffer, 0, text.Length);
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    int length = text.Length, limit = text.Length, status = 0;
                    icu.Transform(icu.Transliterator, handle.AddrOfPinnedObject(), ref length, capacity, 0, ref limit, ref status);
                    if (status == BufferOverflowError)
                    {
                        capacity = length + 16;
                        continue;
                    }
                    return status > 0 ? null : new string(buffer, 0, Math.Min(length, capacity));
                }
                finally
                {
                    handle.Free();
                }
            }
            return null;
        }
    }

    private static Icu? Open()
    {
        try
        {
            foreach (var (library, suffix) in Candidates())
            {
                if (!NativeLibrary.TryLoad(library, typeof(Transliteration).Assembly, null, out var handle)) continue;
                if (!NativeLibrary.TryGetExport(handle, "utrans_openU" + suffix, out var open)
                    || !NativeLibrary.TryGetExport(handle, "utrans_transUChars" + suffix, out var transform))
                {
                    continue;
                }
                var status = 0;
                var transliterator = Marshal.GetDelegateForFunctionPointer<OpenDelegate>(open)(TransformId, TransformId.Length, 0, 0, 0, 0, ref status);
                if (status > 0 || transliterator == 0) continue;
                return new Icu(transliterator, Marshal.GetDelegateForFunctionPointer<TransformDelegate>(transform));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
        }
        return null;
    }

    private static IEnumerable<(string Library, string Suffix)> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            // The combined ICU of Windows 10 1903+, then the split one of earlier builds. Both export unversioned names.
            yield return ("icu.dll", "");
            yield return ("icuin.dll", "");
            yield break;
        }
        // Linux distributions ship ICU with the major version in the file and symbol names.
        for (var version = 90; version >= 50; version--)
        {
            yield return ($"libicui18n.so.{version}", $"_{version}");
        }
        yield return ("libicucore.dylib", "");
    }
}
