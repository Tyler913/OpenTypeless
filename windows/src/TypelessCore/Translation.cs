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
/// How much of a translation is written in its target language's script, counting words the way
/// <see cref="WordCount"/> does: a Han character or a kana is one word, a run of letters or digits another ("e-mail",
/// "foo_bar" and "3.5" once each, so "SwiftUI" counts as much as "写"). Numbers don't count: they're kept as they are in
/// every language. Nor do names, code and terms kept from the transcript as they were, since a translation keeps them by
/// design ("Contact 欧阳娜娜.", "用 React、TypeScript 和 Vite。"); what's left of the other scripts is text that wasn't
/// translated.
/// </summary>
public static class ScriptShare
{
    /// <summary>Under this many words there's too little to tell ("OK", "好的", "Contact 欧阳娜娜.").</summary>
    public const int MinimumWords = 4;

    /// <summary>
    /// The share of the words in the target's script among those not kept as names, under which a translation is taken
    /// to have come back in another language: a translation that didn't happen is at 0–0.3, a real one near 1.
    /// </summary>
    public const double MinimumShare = 0.5;

    /// <summary>
    /// And among all the words, names included: a text that is almost all names and terms kept as they were, with no
    /// target-language text around them, wasn't translated either. A real one full of terms keeps a third
    /// ("用 React、TypeScript、Vite 和 Tailwind。": 2 of 6).
    /// </summary>
    public const double MinimumTargetShare = 0.2;

    /// <summary>
    /// A run of Han characters or kana up to this long, found as it is in the transcript, is a name kept as it was
    /// (a Chinese name is two to four characters: "张伟", "欧阳娜娜"); a longer one is a phrase left untranslated, and
    /// once a translation has one, its short runs are taken for more of the same ("Please fix the 验证码不显示 bug on
    /// the 登录页面").
    /// </summary>
    internal const int NameLength = 4;

    internal enum Kind { Latin, Cyrillic, Hangul, Other }

    /// <summary>A word as <see cref="WordCount"/> counts it: a run of letters or digits, or a run of Han characters and kana (each a word).</summary>
    /// <param name="IsCjk">A run of Han characters and kana.</param>
    /// <param name="LetterKind">A run of letters or digits: the script of its first letter; null for a number.</param>
    internal sealed record Token(string Text, bool IsCjk, Kind? LetterKind)
    {
        /// <summary>How many words <see cref="WordCount"/> counts in it.</summary>
        public int Words => IsCjk ? Text.EnumerateRunes().Count() : 1;
    }

    /// <summary><paramref name="text"/> cut into words with <see cref="WordCount"/>'s rules, joiners and all.</summary>
    internal static List<Token> Tokens(string text)
    {
        var runes = text.EnumerateRunes().Select(r => r.Value).ToArray();
        var tokens = new List<Token>();
        var word = new StringBuilder();
        var run = new StringBuilder();
        void EndWord()
        {
            if (word.Length == 0) return;
            tokens.Add(new Token(word.ToString(), false, KindOf(word.ToString())));
            word.Clear();
        }
        void EndRun()
        {
            if (run.Length == 0) return;
            tokens.Add(new Token(run.ToString(), true, null));
            run.Clear();
        }
        for (var i = 0; i < runes.Length; i++)
        {
            var scalar = runes[i];
            if (WordCount.IsCjkWordCharacter(scalar))
            {
                EndWord();
                run.Append(char.ConvertFromUtf32(scalar));
            }
            else if (WordCount.IsWordCharacter(scalar))
            {
                EndRun();
                word.Append(char.ConvertFromUtf32(scalar));
            }
            else if (word.Length > 0 && WordCount.IsJoiner(scalar) && i + 1 < runes.Length && WordCount.IsWordCharacter(runes[i + 1])
                     && !WordCount.IsCjkWordCharacter(runes[i + 1]))
            {
                word.Append(char.ConvertFromUtf32(scalar));
            }
            else
            {
                EndWord();
                EndRun();
            }
        }
        EndWord();
        EndRun();
        return tokens;
    }

    /// <summary>The script of a word's first letter; null when it has none (a number).</summary>
    private static Kind? KindOf(string word)
    {
        foreach (var rune in word.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0xC0 and <= 0xD6 or >= 0xD8 and <= 0xF6 or >= 0xF8 and <= 0x24F
                or >= 0x1E00 and <= 0x1EFF) return Kind.Latin;
            if (value is >= 0x400 and <= 0x52F) return Kind.Cyrillic;
            if (value is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) return Kind.Hangul;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter) return Kind.Other;
        }
        return null;
    }

    private static bool IsKana(int scalar) => scalar is >= 0x3040 and <= 0x30FF;

    /// <summary>
    /// A word kept from the transcript that reads as a name, code or a term rather than ordinary text left untranslated:
    /// it has a capital letter, a digit or a joiner ("SwiftUI", "GPT6", "e-mail", "foo_bar"), or its script has no
    /// capitals (Korean). "the" and "report" don't.
    /// </summary>
    internal static bool LooksLikeName(string word)
    {
        var cased = false;
        foreach (var rune in word.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.DecimalDigitNumber
                    or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber:
                    return true;
                case UnicodeCategory.LowercaseLetter:
                    cased = true;
                    break;
                default:
                    if (WordCount.IsJoiner(rune.Value)) return true;
                    break;
            }
        }
        return !cased;
    }

    private static bool Matches(Kind kind, WritingScript script) =>
        (kind, script) is (Kind.Latin, WritingScript.Latin) or (Kind.Cyrillic, WritingScript.Cyrillic) or (Kind.Hangul, WritingScript.Hangul);

    private static bool InScript(int scalar, WritingScript script) =>
        script == WritingScript.Japanese || (script == WritingScript.Han && !IsKana(scalar));

    /// <summary>The share of <paramref name="text"/>'s words written in <paramref name="script"/> (1 when it has none), names included.</summary>
    public static double Share(WritingScript script, string text)
    {
        int inScript = 0, total = 0;
        foreach (var token in Tokens(text))
        {
            if (token.IsCjk)
            {
                foreach (var rune in token.Text.EnumerateRunes())
                {
                    total++;
                    if (InScript(rune.Value, script)) inScript++;
                }
            }
            else if (token.LetterKind is { } kind)
            {
                total++;
                if (Matches(kind, script)) inScript++;
            }
        }
        return total == 0 ? 1 : (double)inScript / total;
    }

    /// <summary>
    /// Whether <paramref name="text"/>, a translation of <paramref name="transcript"/>, is mostly written in
    /// <paramref name="script"/>, or too short to tell. Words of other scripts found in the transcript or the speaker's
    /// vocabulary are names kept as they were (see <see cref="LooksLikeName"/>, <see cref="NameLength"/>), and only count
    /// against the translation when they're nearly all of it (<see cref="MinimumTargetShare"/>).
    ///
    /// Han characters are shared by Chinese and Japanese, so once a text has ten Han characters or kana, kana tell them
    /// apart: every Japanese sentence has some (at least one character in ten), Chinese none.
    /// </summary>
    public static bool IsMostly(string text, WritingScript script, string transcript = "", IReadOnlyList<string>? vocabulary = null)
    {
        var spoken = Tokens(transcript).Where(t => !t.IsCjk).Select(t => t.Text.ToLowerInvariant()).ToHashSet();
        var terms = (vocabulary ?? Array.Empty<string>()).SelectMany(term => Tokens(term)).ToList();
        var termWords = terms.Where(t => !t.IsCjk).Select(t => t.Text.ToLowerInvariant()).ToHashSet();
        var termRuns = terms.Where(t => t.IsCjk).Select(t => t.Text).ToHashSet();
        int target = 0, untranslated = 0, kept = 0, han = 0, kana = 0;
        // Short runs of another script's Han characters or kana found in the transcript: names, unless the translation
        // also left a phrase of that script untranslated.
        int names = 0, nameCharacters = 0;
        var leftAPhrase = false;
        foreach (var token in Tokens(text))
        {
            if (token.IsCjk)
            {
                var foreign = 0;
                foreach (var rune in token.Text.EnumerateRunes())
                {
                    if (IsKana(rune.Value)) kana++; else han++;
                    if (InScript(rune.Value, script)) target++; else foreign++;
                }
                if (foreign == 0) continue;
                if (termRuns.Contains(token.Text))
                {
                    kept++;
                }
                else if (token.Words <= NameLength && transcript.Contains(token.Text, StringComparison.Ordinal))
                {
                    names++;
                    nameCharacters += foreign;
                }
                else
                {
                    untranslated += foreign;
                    leftAPhrase = true;
                }
            }
            else if (token.LetterKind is { } kind)
            {
                var word = token.Text.ToLowerInvariant();
                if (Matches(kind, script)) target++;
                else if (termWords.Contains(word) || (spoken.Contains(word) && LooksLikeName(token.Text))) kept++;
                else untranslated++;
            }
        }
        if (leftAPhrase) untranslated += nameCharacters; else kept += names;
        var total = target + untranslated + kept;
        if (total < MinimumWords) return true;
        if (target < (target + untranslated) * MinimumShare || target < total * MinimumTargetShare) return false;
        var cjk = han + kana;
        if (cjk < 10) return true;
        return script switch
        {
            WritingScript.Japanese => kana * 10 >= cjk,
            WritingScript.Han => kana * 10 < cjk,
            _ => true,
        };
    }

    /// <summary>
    /// A text's length in words that compare across writing systems: a Han character or a hiragana counts half a word
    /// (Chinese and Japanese take about two for an English word), a run of katakana one (it's a loanword, however long:
    /// "ローカリゼーション" for "localization"), any other word one.
    /// </summary>
    public static double Length(string text)
    {
        var total = 0.0;
        foreach (var token in Tokens(text))
        {
            if (!token.IsCjk)
            {
                total++;
                continue;
            }
            var inKatakana = false;
            foreach (var rune in token.Text.EnumerateRunes())
            {
                var katakana = rune.Value is >= 0x30A0 and <= 0x30FF;
                if (katakana && !inKatakana) total++;
                else if (!katakana) total += 0.5;
                inKatakana = katakana;
            }
        }
        return total;
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
    /// is off, and the transcript goes in as spoken). Throws only when cancelled. Every answer that comes back goes into
    /// <paramref name="ledger"/> as soon as it's in, so a translation set aside for the clean-up is still counted when the
    /// dictation is cancelled while the clean-up runs.
    /// </summary>
    public static async Task<TranslatedPolishResult> Run(Func<Task<SegmentedPolishResult>> translate, Func<Task<SegmentedPolishResult>>? cleanUp,
                                                         CancellationToken cancellationToken = default, PolishLedger? ledger = null)
    {
        SegmentedPolishResult? translation = null;
        ApiException? translationError = null;
        try
        {
            translation = await translate().ConfigureAwait(false);
            ledger?.Add(translation);
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
            var cleaned = await cleanUp().ConfigureAwait(false);
            ledger?.Add(cleaned);
            return new TranslatedPolishResult(translation, translationError, cleaned, null);
        }
        catch (Exception error)
        {
            var apiError = ApiException.From(error);
            if (apiError.IsCancelled) throw apiError;
            return new TranslatedPolishResult(translation, translationError, null, apiError);
        }
    }
}

/// <summary>
/// The clean-up and translation answers one dictation got, kept as they come in. What they cost is counted from here,
/// also when the dictation is cancelled before its text goes in: a translation set aside for the clean-up was billed all
/// the same. Owned by one run of the processing, so a later dictation never counts it. Thread-safe.
/// </summary>
public sealed class PolishLedger
{
    private readonly List<SegmentedPolishResult> _results = new();

    public void Add(SegmentedPolishResult result)
    {
        lock (_results) _results.Add(result);
    }

    public IReadOnlyList<SegmentedPolishResult> All
    {
        get
        {
            lock (_results) return _results.ToList();
        }
    }
}
