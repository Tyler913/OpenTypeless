using System.Globalization;
using System.Text;

namespace TypelessCore;

/// <summary>The languages Settings offers to translate dictations into. Any other can be typed in (see <see cref="TranslationTarget"/>).</summary>
public enum TranslationLanguage { English, Chinese, Japanese, Korean, Spanish, French, German, Portuguese, Russian }

public static class TranslationLanguages
{
    public static readonly TranslationLanguage[] All = Enum.GetValues<TranslationLanguage>();

    /// <summary>The code it's saved under, the same as on macOS.</summary>
    public static string Code(this TranslationLanguage language) => language switch
    {
        TranslationLanguage.English => "en",
        TranslationLanguage.Chinese => "zh-Hans",
        TranslationLanguage.Japanese => "ja",
        TranslationLanguage.Korean => "ko",
        TranslationLanguage.Spanish => "es",
        TranslationLanguage.French => "fr",
        TranslationLanguage.German => "de",
        TranslationLanguage.Portuguese => "pt",
        _ => "ru",
    };

    /// <summary>Its own name, as the picker shows it in every UI language.</summary>
    public static string NativeName(this TranslationLanguage language) => language switch
    {
        TranslationLanguage.English => "English",
        TranslationLanguage.Chinese => "简体中文",
        TranslationLanguage.Japanese => "日本語",
        TranslationLanguage.Korean => "한국어",
        TranslationLanguage.Spanish => "Español",
        TranslationLanguage.French => "Français",
        TranslationLanguage.German => "Deutsch",
        TranslationLanguage.Portuguese => "Português",
        _ => "Русский",
    };

    /// <summary>Its English name, as the translation prompt names it.</summary>
    public static string PromptName(this TranslationLanguage language) => language switch
    {
        TranslationLanguage.English => "English",
        TranslationLanguage.Chinese => "Simplified Chinese",
        TranslationLanguage.Japanese => "Japanese",
        TranslationLanguage.Korean => "Korean",
        TranslationLanguage.Spanish => "Spanish",
        TranslationLanguage.French => "French",
        TranslationLanguage.German => "German",
        TranslationLanguage.Portuguese => "Portuguese",
        _ => "Russian",
    };

    /// <summary>The short code the recording capsule shows ("→ EN").</summary>
    public static string Badge(this TranslationLanguage language) =>
        language == TranslationLanguage.Chinese ? "ZH" : language.Code().ToUpperInvariant();

    public static WritingScript Script(this TranslationLanguage language) => language switch
    {
        TranslationLanguage.Chinese => WritingScript.Han,
        TranslationLanguage.Japanese => WritingScript.Japanese,
        TranslationLanguage.Korean => WritingScript.Hangul,
        TranslationLanguage.Russian => WritingScript.Cyrillic,
        _ => WritingScript.Latin,
    };
}

/// <summary>
/// The language a translation dictation's text is written in: one of the listed languages, by its code, or any other the
/// user named. Saved as <see cref="Id"/>, in Settings and with the dictation in History (see <see cref="FromRecord"/>).
/// </summary>
public sealed record TranslationTarget
{
    /// <summary>A listed language's code ("en", "zh-Hans"…), or the name the user typed for another.</summary>
    public string Id { get; }

    private TranslationTarget(string id) => Id = id;

    public TranslationTarget(TranslationLanguage language) : this(language.Code()) { }

    /// <summary>Null for an empty name.</summary>
    public static TranslationTarget? From(string? id)
    {
        var trimmed = id?.Trim() ?? "";
        return trimmed.Length == 0 ? null : new TranslationTarget(trimmed);
    }

    /// <summary>The listed language, also when its name was typed in as another ("english", "日本語").</summary>
    public TranslationLanguage? Language
    {
        get
        {
            foreach (var language in TranslationLanguages.All)
            {
                if (language.Code() == Id) return language;
            }
            var name = Id.ToLowerInvariant();
            foreach (var language in TranslationLanguages.All)
            {
                if (language.PromptName().ToLowerInvariant() == name || language.NativeName().ToLowerInvariant() == name) return language;
            }
            return null;
        }
    }

    /// <summary>How the prompt names it.</summary>
    public string PromptName => Language?.PromptName() ?? Id;

    /// <summary>How Settings and History name it.</summary>
    public string DisplayName => Language?.NativeName() ?? Id;

    /// <summary>What the recording capsule shows while it records or processes a translation.</summary>
    public string Badge => "→ " + (Language?.Badge() ?? Prefix(Id, 10));

    /// <summary>What a translation must mostly be written in; null for a language typed in, which isn't checked.</summary>
    public WritingScript? Script => Language?.Script();

    /// <summary>The first <paramref name="count"/> characters as Swift counts them (grapheme clusters).</summary>
    private static string Prefix(string text, int count)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= count ? text : info.SubstringByTextElements(0, count);
    }

    // MARK: History

    /// <summary>
    /// What a translation dictation saves as its <c>mode</c> in History; plain dictation saves none, so records from before
    /// translation, and older versions reading newer records, see a dictation.
    /// </summary>
    public const string RecordMode = "translate";

    /// <summary>
    /// The target a History record names: its <c>mode</c> and <c>targetLanguage</c>. Null for a plain dictation, or a mode
    /// this version doesn't know.
    /// </summary>
    public static TranslationTarget? FromRecord(string? mode, string? targetLanguage) =>
        mode == RecordMode ? From(targetLanguage) : null;
}

/// <summary>
/// The key that, pressed while the dictation hotkey is held, turns the recording into a translation (see
/// <see cref="ModeChords{TMode}"/>). Either side's key counts. A modifier, so it types nothing and works for as long as
/// the hotkey is held.
/// </summary>
public enum TranslationChord { Shift, Control, Alt }

public static class TranslationChords
{
    public static readonly TranslationChord[] All = Enum.GetValues<TranslationChord>();

    /// <summary>The name it's saved under, the same as on macOS.</summary>
    public static string RawValue(this TranslationChord chord) => chord switch
    {
        TranslationChord.Control => "control",
        TranslationChord.Alt => "alt",
        _ => "shift",
    };

    public static TranslationChord? Parse(string raw) => All.Cast<TranslationChord?>().FirstOrDefault(c => c!.Value.RawValue() == raw);

    /// <summary>
    /// Both of its keys (Windows virtual-key codes), less the hotkey's own when that is one of them: a modifier-only
    /// hotkey such as Right Shift can't be its own chord, but Left Shift still is.
    /// </summary>
    public static IReadOnlySet<int> KeyCodes(this TranslationChord chord, int? excludingHotkey)
    {
        var both = chord switch
        {
            TranslationChord.Control => new HashSet<int> { 0xA2, 0xA3 },
            TranslationChord.Alt => new HashSet<int> { 0xA4, 0xA5 },
            _ => new HashSet<int> { 0xA0, 0xA1 },
        };
        if (excludingHotkey is { } hotkey) both.Remove(hotkey);
        return both;
    }
}

/// <summary>The writing systems translations are checked against (see <see cref="ScriptShare"/>).</summary>
public enum WritingScript
{
    Latin,
    Cyrillic,
    Hangul,
    /// <summary>Han ideographs: Chinese.</summary>
    Han,
    /// <summary>Kana, with or without Han ideographs: Japanese.</summary>
    Japanese,
}

/// <summary>
/// How much of a text is written in a script, counting words the way <see cref="WordCount"/> does: a Han character or a
/// kana is one, a run of letters of one script another (so "SwiftUI" counts as much as "写"). Digits, punctuation and
/// symbols don't count: numbers are kept as they are in every language.
/// </summary>
public static class ScriptShare
{
    /// <summary>Under this many words there's too little to tell ("OK", "好的").</summary>
    public const int MinimumWords = 4;

    /// <summary>
    /// The share of words in the target's script under which a translation is taken to have come back in another
    /// language. Names, code and technical terms are kept as they are, so a Chinese translation of a message full of them
    /// can be half Latin ("用 SwiftUI 写一个 macOS app，调用 OpenRouter 的 API…": 0.56), while a translation that didn't
    /// happen is at 0–0.3.
    /// </summary>
    public const double MinimumShare = 0.5;

    internal struct Counts
    {
        public int Latin, Cyrillic, Hangul, Han, Kana, Other;
        public readonly int Total => Latin + Cyrillic + Hangul + Han + Kana + Other;

        public readonly int Of(WritingScript script) => script switch
        {
            WritingScript.Latin => Latin,
            WritingScript.Cyrillic => Cyrillic,
            WritingScript.Hangul => Hangul,
            WritingScript.Han => Han,
            _ => Han + Kana,
        };
    }

    private enum Kind { Latin, Cyrillic, Hangul, Other }

    internal static Counts Count(string text)
    {
        var counts = new Counts();
        Kind? run = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            Kind kind;
            if (value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x3134F or >= 0xF900 and <= 0xFAFF)
            {
                counts.Han++;
                run = null;
                continue;
            }
            if (value is >= 0x3040 and <= 0x309F or >= 0x30A0 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F)
            {
                counts.Kana++;
                run = null;
                continue;
            }
            if (value is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0xC0 and <= 0xD6 or >= 0xD8 and <= 0xF6 or >= 0xF8 and <= 0x24F
                or >= 0x1E00 and <= 0x1EFF)
            {
                kind = Kind.Latin;
            }
            else if (value is >= 0x400 and <= 0x52F)
            {
                kind = Kind.Cyrillic;
            }
            else if (value is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F)
            {
                kind = Kind.Hangul;
            }
            else
            {
                switch (Rune.GetUnicodeCategory(rune))
                {
                    case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark:
                        // An accent written as its own character stays with its letter.
                        continue;
                    case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter:
                        kind = Kind.Other;
                        break;
                    default:
                        run = null;
                        continue;
                }
            }
            if (kind == run) continue;
            run = kind;
            switch (kind)
            {
                case Kind.Latin: counts.Latin++; break;
                case Kind.Cyrillic: counts.Cyrillic++; break;
                case Kind.Hangul: counts.Hangul++; break;
                default: counts.Other++; break;
            }
        }
        return counts;
    }

    /// <summary>The share of <paramref name="text"/>'s words written in <paramref name="script"/> (1 when it has none).</summary>
    public static double Share(WritingScript script, string text)
    {
        var counts = Count(text);
        return counts.Total == 0 ? 1 : (double)counts.Of(script) / counts.Total;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is mostly written in <paramref name="script"/>, or too short to tell. Han characters
    /// are shared by Chinese and Japanese, so once a text has ten Han characters or kana, kana tell them apart: every
    /// Japanese sentence has some (at least one character in ten), Chinese none.
    /// </summary>
    public static bool IsMostly(string text, WritingScript script)
    {
        var counts = Count(text);
        if (counts.Total < MinimumWords) return true;
        if (counts.Of(script) < counts.Total * MinimumShare) return false;
        var cjk = counts.Han + counts.Kana;
        if (cjk < 10) return true;
        return script switch
        {
            WritingScript.Japanese => counts.Kana * 10 >= cjk,
            WritingScript.Han => counts.Kana * 10 < cjk,
            _ => true,
        };
    }
}

/// <summary>
/// A translation dictation's text: its translation when that can be used as it is, otherwise its clean-up (one request
/// more), and the transcript as spoken when that fails too. One request when it works, so a translation is as quick as a
/// clean-up; OpenWhispr's translation chain always sends a second request after the clean-up instead.
/// </summary>
/// <param name="Translation">What the translation request gave, when anything came back. Billed even when it isn't used.</param>
/// <param name="TranslationError">What went wrong when nothing came back.</param>
/// <param name="CleanUp">The clean-up asked for instead, when the translation couldn't be used and clean-up is on.</param>
/// <param name="CleanUpError">What went wrong with that clean-up, when nothing came back.</param>
public sealed record TranslatedPolishResult(SegmentedPolishResult? Translation, ApiException? TranslationError,
                                            SegmentedPolishResult? CleanUp, ApiException? CleanUpError)
{
    /// <summary>
    /// Whether the translation goes in: every part of it came back usable. A translation with a part that didn't would go
    /// in half translated, so it's set aside as a whole.
    /// </summary>
    public bool Translated => Translation is { Fallbacks.Count: 0 };

    /// <summary>Why the translation wasn't used: the first unusable part's reason, or <c>Failed</c> when nothing came back.</summary>
    public PolishFallback? TranslationFallback =>
        Translated ? null : Translation?.Fallbacks.Select(p => p.Fallback).FirstOrDefault() ?? PolishFallback.Failed;
}

public static class TranslatedPolish
{
    /// <summary>
    /// Runs <paramref name="translate"/>; when its result can't be used as it is (see
    /// <see cref="TranslatedPolishResult.Translated"/>) or it fails, runs <paramref name="cleanUp"/> instead (null: clean-up
    /// is off, and the transcript goes in as spoken). Throws only when cancelled.
    /// </summary>
    public static async Task<TranslatedPolishResult> Run(Func<Task<SegmentedPolishResult>> translate, Func<Task<SegmentedPolishResult>>? cleanUp,
                                                         CancellationToken cancellationToken = default)
    {
        SegmentedPolishResult? translation = null;
        ApiException? translationError = null;
        try
        {
            translation = await translate().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var apiError = ApiException.From(error);
            if (apiError.IsCancelled) throw apiError;
            translationError = apiError;
        }
        if (translation is { Fallbacks.Count: 0 }) return new TranslatedPolishResult(translation, null, null, null);
        if (cancellationToken.IsCancellationRequested) throw ApiException.Cancelled();
        if (cleanUp is null) return new TranslatedPolishResult(translation, translationError, null, null);
        try
        {
            return new TranslatedPolishResult(translation, translationError, await cleanUp().ConfigureAwait(false), null);
        }
        catch (Exception error)
        {
            var apiError = ApiException.From(error);
            if (apiError.IsCancelled) throw apiError;
            return new TranslatedPolishResult(translation, translationError, null, apiError);
        }
    }
}
