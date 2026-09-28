using Microsoft.Win32;

namespace OpenTypeless.Services;

/// <summary>
/// Open at login through the per-user <c>Run</c> key. "Needs approval" is the Windows counterpart of the macOS
/// Login Items approval: the entry exists but the user disabled it in Task Manager → Startup apps.
/// </summary>
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "OpenTypeless";

    public static string CommandLine => $"\"{Environment.ProcessPath}\" --autostart";

    private static bool Registered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>Task Manager stores 0x02 (enabled) or 0x03 (disabled) in the first byte.</summary>
    private static bool DisabledByUser
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        }
    }

    public static bool IsEnabled => Registered && !DisabledByUser;
    public static bool NeedsApproval => Registered && DisabledByUser;

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException(L("无法打开启动项注册表", "Can't open the startup registry key"));
        if (enabled) key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the Run entry pointing at this copy of the app after it's moved or updated.</summary>
    public static void RefreshPathIfRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != CommandLine && !IsDevelopmentBuild)
        {
            key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
        }
    }

    /// <summary>A build running straight out of the source tree (not an installed copy).</summary>
    public static bool IsDevelopmentBuild =>
        (Environment.ProcessPath ?? "").Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns it on once, the first time an installed copy runs (the user asked for auto-start).</summary>
    public static void EnableByDefaultOnce(AppSettings settings)
    {
        if (settings.DidEnableLaunchAtLoginByDefault || IsDevelopmentBuild) return;
        settings.DidEnableLaunchAtLoginByDefault = true;
        try { Set(true); } catch { /* best effort */ }
    }
}
