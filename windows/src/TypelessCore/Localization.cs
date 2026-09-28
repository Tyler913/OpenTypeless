using System.Globalization;

namespace TypelessCore;

/// <summary>
/// Tiny two-language localisation: every user-facing string is written inline as <c>L("中文", "English")</c>.
/// No resource files or string tables, and switching language takes effect immediately.
/// </summary>
public enum AppLanguage { System, Zh, En }

public static class Localization
{
    public const string SettingsKey = "appLanguage";

    /// <summary>Returns the stored preference ("system", "zh", "en" or null). The app points this at its settings.</summary>
    public static Func<string?> Preference { get; set; } = () => null;

    public static string RawValue(this AppLanguage language) => language switch
    {
        AppLanguage.Zh => "zh",
        AppLanguage.En => "en",
        _ => "system",
    };

    public static AppLanguage ParseLanguage(string? raw) => raw switch
    {
        "zh" => AppLanguage.Zh,
        "en" => AppLanguage.En,
        _ => AppLanguage.System,
    };

    public static AppLanguage Current => ParseLanguage(Preference());

    /// <summary>The language actually used for display (resolves <see cref="AppLanguage.System"/>).</summary>
    public static AppLanguage Resolved => Current switch
    {
        AppLanguage.Zh or AppLanguage.En => Current,
        _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Zh : AppLanguage.En,
    };

    public static string L(string zh, string en) => Resolved == AppLanguage.Zh ? zh : en;
}
