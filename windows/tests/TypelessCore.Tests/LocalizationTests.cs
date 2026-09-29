using System.Text.RegularExpressions;

namespace TypelessCore.Tests;

/// <summary>The L(…) strings and the translations in i18n/strings.json.</summary>
public class LocalizationTests
{
    [Fact]
    public void InterpolationsBecomeNumberedPlaceholders()
    {
        var version = "1.2.0";
        var percent = 42;
        LocalizedText text = $"Downloading {version}… {percent}%";
        Assert.Equal("Downloading 1.2.0… 42%", text.Text);
        Assert.Equal("Downloading {0}… {1}%", text.Key);
        Assert.Equal(new[] { "1.2.0", "42" }, text.Arguments);
    }

    [Fact]
    public void ChineseAndEnglishComeFromTheCode()
    {
        var version = "1.2.0";
        Assert.Equal("有新版本 1.2.0", Localized($"有新版本 {version}", $"Version {version} is available", AppLanguage.Zh));
        Assert.Equal("Version 1.2.0 is available", Localized($"有新版本 {version}", $"Version {version} is available", AppLanguage.En));
    }

    [Fact]
    public void OtherLanguagesComeFromTheTable()
    {
        var version = "1.2.0";
        Assert.Equal("バージョン 1.2.0 が利用可能です", Localized($"有新版本 {version}", $"Version {version} is available", AppLanguage.Ja));
        Assert.Equal("Einstellungen", Localized("设置", "Settings", AppLanguage.De));
        // A translation may reorder the placeholders.
        var (actual, expected) = ("2", "3");
        Assert.Equal("다운로드한 버전이 3이 아니라 2입니다", Localized("x", $"the download is version {actual}, not {expected}", AppLanguage.Ko));
    }

    [Fact]
    public void MissingTranslationsFallBackToEnglish() =>
        Assert.Equal("Not translated 7", Localized("没有翻译", $"Not translated {7}", AppLanguage.Fr));

    [Fact]
    public void EveryTranslationKeepsThePlaceholders()
    {
        Assert.True(Translations.Table.Count > 200);
        static string Placeholders(string text) =>
            string.Join(",", Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order());
        foreach (var (key, translations) in Translations.Table)
        {
            foreach (var language in TypelessCore.Localization.Supported.Where(l => l is not (AppLanguage.Zh or AppLanguage.En)))
            {
                Assert.True(translations.TryGetValue(language.RawValue(), out var text), $"{key} has no {language.RawValue()} translation");
                Assert.Equal(Placeholders(key), Placeholders(text!));
            }
        }
    }

    [Fact]
    public void SystemLanguagesMatchByLanguageCode()
    {
        Assert.Equal(AppLanguage.Pt, Matching("pt-BR"));
        Assert.Equal(AppLanguage.Zh, Matching("zh-Hant-TW"));
        Assert.Equal(AppLanguage.Ja, Matching("ja"));
        Assert.Null(Matching("it-IT"));
        Assert.Null(Matching("system"));
        Assert.Equal(AppLanguage.System, ParseLanguage("system"));
        Assert.Equal(AppLanguage.Ru, ParseLanguage("ru"));
    }

    [Fact]
    public void FillLeavesUnknownBracesAlone() =>
        Assert.Equal("a {x} {5} {", Translations.Fill("{0} {x} {5} {", ["a"]));
}
