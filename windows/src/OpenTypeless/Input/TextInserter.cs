using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTypeless.Native;

namespace OpenTypeless.Input;

/// <summary>
/// Inserts text at the cursor of the foreground app by pasting it.
///
/// The text goes on the clipboard with *delayed rendering*: Windows asks this app for it (WM_RENDERFORMAT)
/// only when some app actually reads it. That tells us whether Ctrl+V landed, even in browsers, Electron apps
/// and elevated windows that UI Automation can't see into. If it landed, the previous clipboard is restored;
/// if nothing asked for the text, it's left on the clipboard for the user to paste themselves.
/// </summary>
public static class TextInserter
{
    public enum Outcome
    {
        /// <summary>The foreground app read the text in response to Ctrl+V.</summary>
        Pasted,
        /// <summary>Ctrl+V was sent but nothing read the text; it's on the clipboard.</summary>
        NotPasted,
    }

    /// <summary>The window that owns our clipboard writes (the tray's message window), and receives WM_RENDERFORMAT.</summary>
    public static nint ClipboardOwner { get; set; }

    /// <summary>How long to wait for the target app to read the clipboard after Ctrl+V.</summary>
    private static readonly TimeSpan PasteTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>The text on offer while a paste is in flight. UI thread only (the owner window lives there).</summary>
    private sealed class Promise(string text)
    {
        public string Text { get; } = text;
        /// <summary>Ctrl+V has been sent. A read before that is a clipboard monitor, not the paste.</summary>
        public bool Armed { get; set; }
        public bool Requested { get; set; }
        /// <summary>The clipboard's sequence number once our entry is in place (rendering the data may bump it).</summary>
        public uint Sequence { get; set; }
    }

    private static Promise? _promise;

    public static async Task<Outcome> Insert(string text, bool restoreClipboard)
    {
        if (ClipboardOwner == 0)
        {
            // No owner window (never in the app itself): paste eagerly and assume it landed.
            ClipboardSnapshot.SetText(text, transient: false);
            await Task.Delay(60);
            PostControlV();
            return Outcome.Pasted;
        }

        var saved = restoreClipboard ? ClipboardSnapshot.Take() : null;
        // Set only after offering: emptying the clipboard tells its previous owner (often this app) it's gone.
        _promise = null;
        if (!ClipboardSnapshot.OfferText(ClipboardOwner))
        {
            CopyToClipboard(text);
            return Outcome.NotPasted;
        }
        var promise = new Promise(text) { Sequence = Win32.GetClipboardSequenceNumber() };
        _promise = promise;

        await Task.Delay(60);
        promise.Armed = true;
        PostControlV();

        var clock = Stopwatch.StartNew();
        while (!promise.Requested && clock.Elapsed < PasteTimeout && _promise == promise)
        {
            await Task.Delay(20);
        }
        var ours = _promise == promise && Win32.GetClipboardSequenceNumber() == promise.Sequence;
        // Someone else changed the clipboard meanwhile: leave their content alone.
        if (!ours) return promise.Requested ? Outcome.Pasted : Outcome.NotPasted;

        if (promise.Requested)
        {
            // Let the app finish reading any other formats before swapping the clipboard.
            await Task.Delay(150);
            if (_promise != promise || Win32.GetClipboardSequenceNumber() != promise.Sequence) return Outcome.Pasted;
            _promise = null;
            if (saved != null) saved.Restore(); else CopyToClipboard(text);
            return Outcome.Pasted;
        }
        // Nothing pasted: replace the promise with a plain copy so the text stays available (and
        // clipboard history may record it) after this app stops serving it.
        _promise = null;
        CopyToClipboard(text);
        return Outcome.NotPasted;
    }

    public static void CopyToClipboard(string text) => ClipboardSnapshot.SetText(text, transient: false);

    /// <summary>
    /// A native app whose menu bar is active (after a lone Alt tap) swallows Ctrl+V and reports the menu as focused.
    /// Leaves that mode the way the user would, with Esc (twice at most: an open menu, then the menu bar). Context
    /// menus are left alone.
    /// </summary>
    public static async Task LeaveMenuMode()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var foreground = Win32.GetForegroundWindow();
            if (foreground == 0) return;
            var thread = Win32.GetWindowThreadProcessId(foreground, out var pid);
            if (pid == (uint)Environment.ProcessId) return;
            var gui = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
            if (!Win32.GetGUIThreadInfo(thread, ref gui)
                || (gui.flags & Win32.GUI_INMENUMODE) == 0 || (gui.flags & Win32.GUI_POPUPMENUMODE) != 0) return;
            Services.AppLog.Debug("paste", "foreground app is in menu mode; sending Esc");
            var inputs = new[] { Win32.Key(Win32.VK_ESCAPE, up: false), Win32.Key(Win32.VK_ESCAPE, up: true) };
            Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Clipboard messages for the owner window. Returns true when handled. WM_RENDERFORMAT arrives while the
    /// reading app holds the clipboard open, so the data is set without opening it.
    /// </summary>
    public static bool HandleClipboardMessage(nint hwnd, uint msg, nint wParam)
    {
        switch (msg)
        {
            case Win32.WM_RENDERFORMAT:
                if (_promise is { } promise && (uint)wParam == Win32.CF_UNICODETEXT)
                {
                    // Rendered once, the data stays on the clipboard: a later read by the target app is invisible, so
                    // an early read by a monitor ends up as "not pasted", which keeps the text on the clipboard.
                    var rendered = ClipboardSnapshot.PutText(promise.Text);
                    if (promise.Armed) promise.Requested = rendered;
                    promise.Sequence = Win32.GetClipboardSequenceNumber();
                }
                return true;
            case Win32.WM_RENDERALLFORMATS:
                // The owner window is going away (the app is quitting) while our entry is still offered.
                if (_promise is { } pending && Win32.OpenClipboard(hwnd))
                {
                    try
                    {
                        if (Win32.GetClipboardOwner() == hwnd) ClipboardSnapshot.PutText(pending.Text);
                    }
                    finally
                    {
                        Win32.CloseClipboard();
                    }
                }
                return true;
            case Win32.WM_DESTROYCLIPBOARD:
                // Someone emptied the clipboard, so our offer is gone.
                _promise = null;
                return true;
        }
        return false;
    }

    private static void PostControlV()
    {
        var inputs = new[]
        {
            Win32.Key(Win32.VK_CONTROL, up: false),
            Win32.Key(Win32.VK_V, up: false),
            Win32.Key(Win32.VK_V, up: true),
            Win32.Key(Win32.VK_CONTROL, up: true),
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
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
            if (!PutText(text)) return false;
            if (transient) MarkTransient();
            return true;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    /// <summary>
    /// Puts a delayed-rendering text entry on the clipboard: <paramref name="owner"/> gets WM_RENDERFORMAT when an app
    /// reads it. Marked transient, so clipboard history and clipboard managers skip it rather than reading it
    /// eagerly (which would look like a paste).
    /// </summary>
    public static bool OfferText(nint owner)
    {
        if (!Open(owner)) return false;
        try
        {
            Win32.EmptyClipboard();
            MarkTransient();
            // A null handle means "render on request" (the call returns null either way, so check the result).
            Win32.SetClipboardData(Win32.CF_UNICODETEXT, 0);
            return Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT);
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    /// <summary>Sets CF_UNICODETEXT on a clipboard that is already open.</summary>
    public static bool PutText(string text)
    {
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        return Put(Win32.CF_UNICODETEXT, bytes);
    }

    private static void MarkTransient()
    {
        Put(ExcludeFromMonitors, BitConverter.GetBytes(0));
        Put(CanIncludeInHistory, BitConverter.GetBytes(0));
        Put(CanUploadToCloud, BitConverter.GetBytes(0));
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
    private static bool Open(nint? owner = null)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Win32.OpenClipboard(owner ?? TextInserter.ClipboardOwner)) return true;
            Thread.Sleep(15);
        }
        return false;
    }
}
