using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TypelessCore;

/// <summary>
/// Localisation without resource files: every user-facing string is written inline with its Chinese and English
/// text, as <c>L($"有新版本 {version}", $"Version {version} is available")</c>. The other languages come from
/// i18n/strings.json (embedded at build time), keyed by the English text with its interpolations numbered in
/// order ("Version {0} is available"). A string with no translation shows in English. Switching language takes
/// effect immediately. <c>python3 i18n/check.py</c> lists strings that still need translating.
/// </summary>
public enum AppLanguage { System, Zh, En, Ja, Ko, Es, Pt, Fr, De, Ru }

public static class Localization
{
    public const string SettingsKey = "appLanguage";

    /// <summary>Returns the stored preference ("system", "zh", "en", … or null). The app points this at its settings.</summary>
    public static Func<string?> Preference { get; set; } = () => null;

    /// <summary>The languages the UI can be shown in, in menu order.</summary>
    public static AppLanguage[] Supported { get; } = Enum.GetValues<AppLanguage>().Where(l => l != AppLanguage.System).ToArray();

    public static string RawValue(this AppLanguage language) =>
        language == AppLanguage.System ? "system" : language.ToString().ToLowerInvariant();

    public static AppLanguage ParseLanguage(string? raw) =>
        Supported.FirstOrDefault(l => l.RawValue() == raw, AppLanguage.System);

    /// <summary>The language's name in itself, for language menus ("" for System, which needs a localised label).</summary>
    public static string NativeName(this AppLanguage language) => language switch
    {
        AppLanguage.Zh => "简体中文",
        AppLanguage.En => "English",
        AppLanguage.Ja => "日本語",
        AppLanguage.Ko => "한국어",
        AppLanguage.Es => "Español",
        AppLanguage.Pt => "Português",
        AppLanguage.Fr => "Français",
        AppLanguage.De => "Deutsch",
        AppLanguage.Ru => "Русский",
        _ => "",
    };

    /// <summary>The culture used to format dates in this language.</summary>
    public static CultureInfo Culture(this AppLanguage language) => language switch
    {
        AppLanguage.Zh => CultureInfo.GetCultureInfo("zh-CN"),
        AppLanguage.En => CultureInfo.GetCultureInfo("en-US"),
        AppLanguage.Ja => CultureInfo.GetCultureInfo("ja-JP"),
        AppLanguage.Ko => CultureInfo.GetCultureInfo("ko-KR"),
        AppLanguage.Es => CultureInfo.GetCultureInfo("es-ES"),
        AppLanguage.Pt => CultureInfo.GetCultureInfo("pt-BR"),
        AppLanguage.Fr => CultureInfo.GetCultureInfo("fr-FR"),
        AppLanguage.De => CultureInfo.GetCultureInfo("de-DE"),
        AppLanguage.Ru => CultureInfo.GetCultureInfo("ru-RU"),
        _ => CultureInfo.CurrentCulture,
    };

    public static AppLanguage Current => ParseLanguage(Preference());

    /// <summary>The language actually used for display: the chosen one, or the system language if the app supports it.</summary>
    public static AppLanguage Resolved => Current is var chosen and not AppLanguage.System
        ? chosen
        : Matching(CultureInfo.CurrentUICulture.Name) ?? AppLanguage.En;

    /// <summary>The supported language for a culture name such as "pt-BR" or "zh-Hant-TW", if any.</summary>
    public static AppLanguage? Matching(string culture)
    {
        var code = culture.Split('-', '_')[0].ToLowerInvariant();
        return Supported.Where(l => l.RawValue() == code).Select(l => (AppLanguage?)l).FirstOrDefault();
    }

    public static string L(LocalizedText zh, LocalizedText en) => Localized(zh, en, Resolved);

    internal static string Localized(LocalizedText zh, LocalizedText en, AppLanguage language) => language switch
    {
        AppLanguage.Zh => zh.Text,
        AppLanguage.En or AppLanguage.System => en.Text,
        _ => Translations.Table.TryGetValue(en.Key, out var translations)
             && translations.TryGetValue(language.RawValue(), out var template)
            ? Translations.Fill(template, en.Arguments)
            : en.Text,
    };
}

/// <summary>
/// One side of an <c>L(…)</c> call: the finished text, plus the template ("Version {0} is available") and the
/// interpolated values that a translation is filled in with. Built from a string or an interpolated string.
/// </summary>
[InterpolatedStringHandler]
public readonly struct LocalizedText
{
    private readonly StringBuilder? _text;
    private readonly StringBuilder? _key;
    private readonly List<string>? _arguments;
    private readonly string? _plain;

    public LocalizedText(int literalLength, int formattedCount)
    {
        _text = new StringBuilder(literalLength);
        _key = new StringBuilder(literalLength);
        _arguments = new List<string>(formattedCount);
    }

    private LocalizedText(string plain) => _plain = plain;

    public static implicit operator LocalizedText(string text) => new(text);

    public string Text => _plain ?? _text?.ToString() ?? "";
    internal string Key => _plain ?? _key?.ToString() ?? "";
    internal IReadOnlyList<string> Arguments => _arguments ?? [];

    public void AppendLiteral(string literal)
    {
        _text!.Append(literal);
        _key!.Append(literal);
    }

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);

    public void AppendFormatted<T>(T value, int alignment, string? format = null)
    {
        var text = value is IFormattable formattable ? formattable.ToString(format, null) : value?.ToString() ?? "";
        if (alignment != 0) text = alignment > 0 ? text.PadLeft(alignment) : text.PadRight(-alignment);
        _key!.Append('{').Append(_arguments!.Count).Append('}');
        _text!.Append(text);
        _arguments.Add(text);
    }
}

internal static class Translations
{
    /// <summary>English template → language code → translation, from i18n/strings.json.</summary>
    public static readonly Dictionary<string, Dictionary<string, string>> Table = Load();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(Translations).Assembly.GetManifestResourceStream("strings.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream) ?? [];
    }

    /// <summary>Replaces each {n} in the template with the n-th argument.</summary>
    public static string Fill(string template, IReadOnlyList<string> arguments)
    {
        var result = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var close = template[i] == '{' ? template.IndexOf('}', i + 1) : -1;
            if (close > i && int.TryParse(template.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < arguments.Count)
            {
                result.Append(arguments[index]);
                i = close + 1;
            }
            else
            {
                result.Append(template[i]);
                i++;
            }
        }
        return result.ToString();
    }
}
