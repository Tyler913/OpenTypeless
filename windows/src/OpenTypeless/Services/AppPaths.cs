namespace OpenTypeless.Services;

/// <summary>
/// Everything lives under <c>%LOCALAPPDATA%\OpenTypeless</c> (the Windows counterpart of
/// <c>~/Library/Application Support/OpenTypeless</c>): <c>settings.json</c> and <c>Sessions\</c>.
/// </summary>
public static class AppPaths
{
    public static string Support
    {
        get
        {
            // OPENTYPELESS_DATA_DIR points a test run at a throwaway folder instead of the real settings and history.
            var path = Environment.GetEnvironmentVariable("OPENTYPELESS_DATA_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTypeless");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string Sessions
    {
        get
        {
            var path = Path.Combine(Support, "Sessions");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string SettingsFile => Path.Combine(Support, "settings.json");
}
