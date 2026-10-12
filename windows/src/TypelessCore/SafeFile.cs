using System.Globalization;
using System.Text;

namespace TypelessCore;

/// <summary>
/// The app's own small files (settings, usage totals) are rewritten whole on every change. A write goes to a temporary
/// file that is flushed to disk before it replaces the old one, so a power cut can't leave a file of zeros; a read is
/// tried again while antivirus or OneDrive holds the file for a moment; and a file that still can't be read is moved
/// aside, as <c>&lt;name&gt;.corrupt-&lt;yyyyMMdd-HHmmss&gt;</c>, rather than replaced by the defaults on the next save.
/// </summary>
public static class SafeFile
{
    /// <summary>Replaces the file's contents (UTF-8, no byte-order mark). Throws what the file system throws.</summary>
    public static void WriteAllText(string path, string text)
    {
        var temp = path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(text);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads the file, trying again a few times when it's in use or access is refused for a moment, then throws the
    /// last error. A missing file throws at once.
    /// </summary>
    public static string ReadAllText(string path, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException)
                                      && e is not (FileNotFoundException or DirectoryNotFoundException) && attempt < attempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    /// <summary>Renames a file that couldn't be read or parsed out of the way. Returns where it went, or null if it couldn't be moved.</summary>
    public static string? MoveAside(string path, DateTime? now = null)
    {
        try
        {
            var aside = AsidePath(path, now ?? DateTime.Now);
            File.Move(path, aside);
            return aside;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>usage.json</c> → <c>usage.json.corrupt-20261011-143005</c> (local time), numbered if one from that second is already there.</summary>
    internal static string AsidePath(string path, DateTime date)
    {
        var stem = path + ".corrupt-" + date.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = stem;
        for (var number = 2; File.Exists(candidate); number++) candidate = $"{stem}-{number}";
        return candidate;
    }
}
