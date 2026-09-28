using System.Diagnostics;

namespace OpenTypeless.Services;

/// <summary>
/// <c>OpenTypeless.exe --apply-update &lt;pid&gt; &lt;install folder&gt;</c>, run by <see cref="Updater"/> from the unpacked new
/// version. Waits for the running copy to exit, moves the install folder aside, copies the new files in its place
/// (putting the old folder back if that fails) and starts the app from the install folder again, so the Start menu
/// shortcut and the login entry keep working. Settings, keys and history live elsewhere and aren't touched.
/// </summary>
public static class UpdateInstaller
{
    public const string Argument = "--apply-update";

    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, Argument);
        if (index < 0 || index + 2 >= args.Length || !int.TryParse(args[index + 1], out var pid)) return 2;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[index + 2]));
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        // Only ever replace a folder that holds OpenTypeless, and never the one this installer runs from or the one
        // holding settings and history.
        if (!File.Exists(Path.Combine(target, "OpenTypeless.exe")) || Overlaps(target, source) || Overlaps(target, AppPaths.Support))
        {
            return 2;
        }
        if (!WaitForExit(pid)) return 1;

        try
        {
            Install(source, target);
            AppLog.Debug("update", $"installed {source} -> {target}");
        }
        catch (Exception error)
        {
            // The app reports that the version didn't change once it's running again.
            AppLog.Debug("update", "install failed: " + error);
        }
        Process.Start(new ProcessStartInfo(Path.Combine(target, "OpenTypeless.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = target,
        })?.Dispose();
        return 0;
    }

    /// <summary>True if the two folders are the same or one is inside the other.</summary>
    public static bool Overlaps(string a, string b)
    {
        var x = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)) + Path.DirectorySeparatorChar;
        var y = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    private static void Install(string source, string target)
    {
        var backup = target + ".old";
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        // Antivirus scanners and Explorer windows can hold the folder for a moment after the app exits.
        Retry(() => Directory.Move(target, backup));
        try
        {
            CopyDirectory(source, target);
        }
        catch
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Retry(() => Directory.Move(backup, target));
            throw;
        }
        // The new copy deletes the backup once it runs (it can't be deleted while this installer is starting it).
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.GetDirectories(source))
        {
            CopyDirectory(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when (attempt < 20 && error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
