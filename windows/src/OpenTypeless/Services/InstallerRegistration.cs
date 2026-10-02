using Microsoft.Win32;

namespace OpenTypeless.Services;

/// <summary>
/// The entry the installer (installer\OpenTypeless.iss) adds under Settings → Apps. In-app updates replace the files
/// without the installer, so this copy keeps the entry's version current; WinGet reads it to tell what's installed.
/// A copy unzipped by hand has no entry, and a copy elsewhere doesn't touch another copy's.
/// </summary>
public static class InstallerRegistration
{
    /// <summary>The installer's AppId plus Inno Setup's "_is1".</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenTypeless_is1";

    /// <summary>Uninstaller files the installer puts in the app folder (unins000.exe, unins000.dat…), kept across updates.</summary>
    public static bool IsUninstallerFile(string name) =>
        name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(name).ToLowerInvariant() is ".exe" or ".dat" or ".msg";

    public static void SyncVersion(string version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0) return;
            var here = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)), here, StringComparison.OrdinalIgnoreCase)) return;
            if (key.GetValue("DisplayVersion") as string == version) return;
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            AppLog.Debug("update", $"installed version shown as {version}");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            AppLog.Debug("update", "couldn't update the installed version: " + error.Message);
        }
    }
}
