using System.Diagnostics;
using Microsoft.Win32;

namespace OpenTypeless.Services;

/// <summary>
/// Windows asks for far less than macOS: the global keyboard hook and pasting need no permission (except into
/// apps running as administrator). The microphone is governed by Settings → Privacy &amp; security → Microphone,
/// whose switches live in the capability consent store.
/// </summary>
public static class Permissions
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>
    /// False when any of the three switches that apply to a desktop app is off: device-wide (HKLM),
    /// "Microphone access" for this user, or "Let desktop apps access your microphone".
    /// </summary>
    public static bool MicrophoneGranted =>
        !IsDenied(Registry.LocalMachine, ConsentStore)
        && !IsDenied(Registry.CurrentUser, ConsentStore)
        && !IsDenied(Registry.CurrentUser, ConsentStore + @"\NonPackaged");

    private static bool IsDenied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void OpenMicrophoneSettings() => Open("ms-settings:privacy-microphone");

    public static void OpenStartupAppsSettings() => Open("ms-settings:startupapps");

    public static void Open(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // Nothing sensible to do if the shell refuses.
        }
    }
}
