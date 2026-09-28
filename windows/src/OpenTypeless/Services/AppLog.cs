using System.Diagnostics;

namespace OpenTypeless.Services;

/// <summary>
/// Debug log of hotkey and session events (not persisted by default). The macOS app writes these to the unified
/// log; here they go to the debugger output, and to <c>debug.log</c> in the data folder when the
/// <c>OPENTYPELESS_DEBUG</c> environment variable is set.
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static readonly string? FilePath =
        Environment.GetEnvironmentVariable("OPENTYPELESS_DEBUG") != null ? Path.Combine(AppPaths.Support, "debug.log") : null;

    public static void Debug(string category, string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {text}";
        System.Diagnostics.Debug.WriteLine(line);
        if (FilePath == null) return;
        lock (Lock)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch (IOException) { }
        }
    }
}
