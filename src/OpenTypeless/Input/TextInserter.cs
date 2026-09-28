using System.Runtime.InteropServices;
using OpenTypeless.Native;

namespace OpenTypeless.Input;

/// <summary>Inserts text at the cursor of the foreground app by pasting it, then restores the clipboard.</summary>
public static class TextInserter
{
    /// <summary>The window that owns our clipboard writes (the tray's message window).</summary>
    public static nint ClipboardOwner { get; set; }

    public static async Task<bool> Insert(string text, bool restoreClipboard)
    {
        var saved = restoreClipboard ? ClipboardSnapshot.Take() : null;

        if (!ClipboardSnapshot.SetText(text, transient: restoreClipboard)) return false;
        var ourChange = Win32.GetClipboardSequenceNumber();

        await Task.Delay(60);
        if (!PostControlV()) return false;

        if (restoreClipboard)
        {
            // Give the target app time to read the clipboard before putting the old contents back.
            await Task.Delay(600);
            if (Win32.GetClipboardSequenceNumber() == ourChange) saved?.Restore();
        }
        return true;
    }

    public static void CopyToClipboard(string text) => ClipboardSnapshot.SetText(text, transient: false);

    private static bool PostControlV()
    {
        var inputs = new[]
        {
            Win32.Key(Win32.VK_CONTROL, up: false),
            Win32.Key(Win32.VK_V, up: false),
            Win32.Key(Win32.VK_V, up: true),
            Win32.Key(Win32.VK_CONTROL, up: true),
        };
        return Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>()) == inputs.Length;
    }
}

/// <summary>A copy of every memory-backed clipboard format, so the user's clipboard survives a paste.</summary>
public sealed class ClipboardSnapshot
{
    private readonly List<(uint Format, byte[] Data)> _items = new();

    // GDI-handle formats can't be copied as bytes; their memory-backed siblings (CF_DIB…) carry the same data.
    private static readonly HashSet<uint> HandleFormats = [2 /* BITMAP */, 3 /* METAFILEPICT */, 9 /* PALETTE */, 14 /* ENHMETAFILE */,
                                                          0x80 /* OWNERDISPLAY */, 0x82, 0x83, 0x8E /* DSP* */];
    private const long MaxBytes = 64L * 1024 * 1024;

    private static readonly uint ExcludeFromMonitors = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistory = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadToCloud = Win32.RegisterClipboardFormat("CanUploadToCloudClipboard");

    public static ClipboardSnapshot Take()
    {
        var snapshot = new ClipboardSnapshot();
        if (!Open()) return snapshot;
        try
        {
            long total = 0;
            uint format = 0;
            while ((format = Win32.EnumClipboardFormats(format)) != 0)
            {
                if (HandleFormats.Contains(format)) continue;
                var handle = Win32.GetClipboardData(format);
                if (handle == 0) continue;
                var size = (long)Win32.GlobalSize(handle);
                if (size <= 0 || total + size > MaxBytes) continue;
                var pointer = Win32.GlobalLock(handle);
                if (pointer == 0) continue;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, (int)size);
                    snapshot._items.Add((format, bytes));
                    total += size;
                }
                finally
                {
                    Win32.GlobalUnlock(handle);
                }
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return snapshot;
    }

    public void Restore()
    {
        if (!Open()) return;
        try
        {
            Win32.EmptyClipboard();
            foreach (var (format, data) in _items) Put(format, data);
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    /// <param name="transient">Tells clipboard history and clipboard managers not to record this temporary entry.</param>
    public static bool SetText(string text, bool transient)
    {
        if (!Open()) return false;
        try
        {
            Win32.EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * 2];
            System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            if (!Put(Win32.CF_UNICODETEXT, bytes)) return false;
            if (transient)
            {
                Put(ExcludeFromMonitors, BitConverter.GetBytes(0));
                Put(CanIncludeInHistory, BitConverter.GetBytes(0));
                Put(CanUploadToCloud, BitConverter.GetBytes(0));
            }
            return true;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static bool Put(uint format, byte[] data)
    {
        if (format == 0) return false;
        var handle = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (handle == 0) return false;
        var pointer = Win32.GlobalLock(handle);
        if (pointer == 0)
        {
            Win32.GlobalFree(handle);
            return false;
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        Win32.GlobalUnlock(handle);
        if (Win32.SetClipboardData(format, handle) == 0)
        {
            Win32.GlobalFree(handle);
            return false;
        }
        return true; // the system owns the memory now
    }

    /// <summary>Another app may hold the clipboard for a moment; retry briefly.</summary>
    private static bool Open()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Win32.OpenClipboard(TextInserter.ClipboardOwner)) return true;
            Thread.Sleep(15);
        }
        return false;
    }
}
