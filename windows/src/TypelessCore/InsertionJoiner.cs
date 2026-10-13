using System.Globalization;
using System.Text;

namespace TypelessCore;

/// <summary>
/// Fits dictated text to the text around the cursor it's pasted at, the way someone typing it there would: a space
/// where it would otherwise run into the word before or after it, a lowercase first word in the middle of a sentence,
/// and no full stop that would end the sentence early ("Hello there.Next sentence" → "Hello there. Next sentence").
///
/// Decided from the actual neighbours, unlike OpenWhispr (a trailing space unless the text is CJK) or whisper-writer and
/// Handy (a trailing space, a dropped final period, by setting). Whatever can't be told for sure is left as it is, which
/// is how the text would be pasted without this.
/// </summary>
public static class InsertionJoiner
{
    /// <param name="Text">The text to paste.</param>
    /// <param name="Changes">
    /// What was changed, for the session log ("space before", "lowercase first word", "no final full stop",
    /// "space after"); empty when nothing was.
    /// </param>
    public sealed record Result(string Text, IReadOnlyList<string> Changes);

    /// <summary>
    /// <paramref name="before"/> and <paramref name="after"/> are the text just before the cursor (or the selection the
    /// text replaces) and just after it, as read from the field: "" at the start or end of the field, null when it
    /// couldn't be read. Unless both sides are known, or at the start of the field (empty or not: an app that can't tell
    /// where its cursor is may say so), nothing is changed. A dozen characters each side are plenty.
    /// <paramref name="isCommonWord"/> tells whether a lowercase word is an ordinary English dictionary word.
    /// </summary>
    public static Result Fit(string text, string? before, string? after, IReadOnlyList<string> vocabulary, Func<string, bool> isCommonWord)
    {
        var unchanged = new Result(text, []);
        if (before == null || after == null) return unchanged;
        // Each side as the base characters of its graphemes ("é" written as "e" + an accent counts as "e").
        var left = Bases(before);
        var right = Bases(after);
        var textBases = Bases(text);
        // A block of lines (a list, paragraphs) stands on its own rather than inside a sentence.
        if (left.Count == 0 || textBases.Count == 0 || text.EnumerateRunes().Any(r => KindOf(r, null) == Kind.Newline)) return unchanged;
        var previous = left[^1];
        var first = textBases[0];
        Rune? next = right.Count > 0 ? right[0] : null;
        var previousKind = KindOf(previous, SecondLast(left));
        // The cursor is inside a word: what the speaker meant there can't be told.
        if (previousKind == Kind.Word && next is { } n && KindOf(n, previous) == Kind.Word) return unchanged;
        // Japanese doesn't put spaces around Latin words the way Chinese text does here.
        var japanese = before.EnumerateRunes().Any(IsKana) || text.EnumerateRunes().Any(IsKana) || after.EnumerateRunes().Any(IsKana);
        Rune? mark = null;
        for (var i = left.Count - 1; i >= 0; i--)
        {
            if (KindOf(left[i], null) == Kind.Space) continue;
            mark = left[i];
            break;
        }
        var body = text;
        var changes = new List<string>();

        if (mark is { } m && (KindOf(m, null) == Kind.Word || m.Value is ',' or ';')
            && LowercasingFirstWord(body, vocabulary, isCommonWord) is { } lowered)
        {
            body = lowered;
            changes.Add("lowercase first word");
        }
        if (RunsOn(right, mark is { } sentence && IsMidSentence(sentence)) && DroppingFinalStop(body) is { } trimmed)
        {
            body = trimmed;
            changes.Add("no final full stop");
        }
        if (NeedsSpace(previous, previousKind, first, KindOf(first, previous), japanese))
        {
            body = " " + body;
            changes.Insert(0, "space before");
        }
        var ending = Bases(body);
        if (next is { } b && ending.Count > 0)
        {
            var last = ending[^1];
            if (NeedsSpace(last, KindOf(last, SecondLast(ending)), b, KindOf(b, last), japanese))
            {
                body += " ";
                changes.Add("space after");
            }
        }
        return new Result(body, changes);
    }

    // MARK: Characters

    internal enum Kind { Space, Newline, Word, Cjk, Closing, Other }

    /// <summary>
    /// What a character is for spacing: <c>Word</c> is a letter or digit of a script that puts spaces between words
    /// (Latin, Cyrillic, Korean…), <c>Cjk</c> one of a script that doesn't (Han, kana, bopomofo, full-width forms).
    /// <paramref name="previous"/> tells a straight quote that closes (after a word) from one that opens.
    /// </summary>
    internal static Kind KindOf(Rune r, Rune? previous)
    {
        if (Newlines.Contains(r.Value)) return Kind.Newline;
        if (r.Value == '\t' || Rune.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator) return Kind.Space;
        if (Rune.IsLetter(r) || Rune.IsNumber(r)) return IsWithoutSpaces(r) ? Kind.Cjk : Kind.Word;
        if (Closers.Contains(r.Value)) return Kind.Closing;
        if (r.Value is '"' or '\'' && previous is { } p && KindOf(p, null) is Kind.Word or Kind.Cjk or Kind.Closing) return Kind.Closing;
        return Kind.Other;
    }

    private static readonly HashSet<int> Newlines = ['\n', '\r', 0x0B, 0x0C, 0x85, 0x2028, 0x2029];
    /// <summary>Punctuation a following word is separated from by a space.</summary>
    private static readonly HashSet<int> Closers = ['.', ',', '!', '?', ';', ':', ')', ']', '}', 0x2026, 0x201D, 0x2019, 0xBB, '%'];

    /// <summary>
    /// The characters of <paramref name="runes"/> without what only attaches to the one before: combining marks, the
    /// zero-width joiner, variation selectors and skin-tone modifiers.
    /// </summary>
    internal static List<Rune> Bases(IEnumerable<Rune> runes) => runes.Where(r =>
        Rune.GetUnicodeCategory(r) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        && r.Value is not (0x200D or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or >= 0x1F3FB and <= 0x1F3FF)).ToList();

    internal static List<Rune> Bases(string text) => Bases(text.EnumerateRunes());

    /// <summary>Scripts written without spaces between words: Han in every plane, kana, bopomofo, and full-width forms.</summary>
    private static bool IsWithoutSpaces(Rune r) =>
        TranscriptJoiner.JoinsWithoutSpace(r) || IsHan(r) || IsKana(r) || r.Value is >= 0x3100 and <= 0x312F or >= 0x31A0 and <= 0x31BF;

    private static bool IsHan(Rune r) => r.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
        or >= 0x20000 and <= 0x323AF; // extensions B and on, and the compatibility supplement

    private static bool IsKana(Rune r) => r.Value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F
        or >= 0x1B000 and <= 0x1B16F;

    private static Rune? SecondLast(List<Rune> runes) => runes.Count >= 2 ? runes[^2] : null;

    // MARK: Rules

    /// <summary>
    /// Whether a space belongs between two neighbouring characters: between words, after closing punctuation, and
    /// between Chinese and Latin letters or digits, as the clean-up prompt writes them ("用 SwiftUI 写"). Never next to a
    /// space, a line break, an opening bracket or quote, or between CJK characters.
    /// </summary>
    private static bool NeedsSpace(Rune a, Kind ka, Rune b, Kind kb, bool japanese) => (ka, kb) switch
    {
        (Kind.Word, Kind.Word) or (Kind.Closing, Kind.Word) => true,
        (Kind.Cjk, Kind.Word) => !japanese && IsHan(a),
        (Kind.Word, Kind.Cjk) => !japanese && IsHan(b),
        _ => false,
    };

    /// <summary>Before the cursor, the sentence is still going: the last thing there (spaces aside) is a word or a comma.</summary>
    private static bool IsMidSentence(Rune mark) =>
        KindOf(mark, null) is Kind.Word or Kind.Cjk || mark.Value is ',' or ';' or '，' or '、' or '；';

    /// <summary>
    /// The text after the cursor carries on the sentence the dictation goes into: it starts (spaces aside) with a
    /// lowercase letter, or with any word while the sentence before the cursor is still going.
    /// </summary>
    private static bool RunsOn(List<Rune> after, bool midSentence)
    {
        foreach (var next in after)
        {
            var kind = KindOf(next, null);
            if (kind == Kind.Space) continue;
            if (Rune.GetUnicodeCategory(next) == UnicodeCategory.LowercaseLetter) return true;
            return midSentence && kind is Kind.Word or Kind.Cjk;
        }
        return false;
    }

    /// <summary>Abbreviations whose full stop stays when they end the text.</summary>
    private static readonly HashSet<string> Abbreviations = ["etc", "vs", "mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr", "inc", "ltd", "corp"];

    /// <summary>
    /// The text without its final "." or "。", when that ends a sentence after a word: not "…" or "...", nor the full
    /// stop of an abbreviation ("etc.", "e.g.", "U.S.") or an initial ("J."). Null when it stays.
    /// </summary>
    internal static string? DroppingFinalStop(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        if (runes.Count == 0 || runes[^1].Value is not ('.' or '。')) return null;
        var stop = runes[^1];
        var beforeStop = Bases(runes.Take(runes.Count - 1));
        if (beforeStop.Count == 0 || KindOf(beforeStop[^1], null) is not (Kind.Word or Kind.Cjk)) return null;
        if (stop.Value == '.')
        {
            var start = runes.Count - 1;
            while (start > 0 && KindOf(runes[start - 1], null) != Kind.Space) start--;
            var word = runes.Skip(start).Take(runes.Count - 1 - start).ToList();
            var spelled = string.Concat(word.Select(r => r.ToString()));
            if (spelled.Contains('.') || Bases(word).Count(r => Rune.IsLetter(r) || Rune.IsNumber(r)) == 1
                || Abbreviations.Contains(spelled.ToLowerInvariant())) return null;
        }
        return text[..^stop.Utf16SequenceLength];
    }

    /// <summary>
    /// The text with its first word lowercased, when that word is only capitalised for starting a sentence: an ordinary
    /// English word ("Then", "Maybe", "Don't"). Never "I" ("I'm"), a single letter, an acronym, a word with a capital
    /// inside (GitHub, OpenAI) or an abbreviation ("Mr."), a vocabulary term, or a word the dictionary doesn't know as an
    /// everyday one in lowercase (most names). Null when it stays.
    /// </summary>
    internal static string? LowercasingFirstWord(string text, IReadOnlyList<string> vocabulary, Func<string, bool> isCommonWord)
    {
        var token = new StringBuilder();
        foreach (var r in text.EnumerateRunes())
        {
            if (KindOf(r, null) is Kind.Space or Kind.Newline) break;
            token.Append(r.ToString());
        }
        var tokenText = token.ToString();
        var core = tokenText;
        while (core.Length > 0 && (Closers.Contains(core[^1]) || core[^1] is '"' or '\'')) core = core[..^1];
        if (core.Length < 2 || core[0] is not (>= 'A' and <= 'Z')) return null;
        static bool IsLower(char c) => c is >= 'a' and <= 'z';
        for (var i = 1; i < core.Length; i++)
        {
            if (IsLower(core[i])) continue;
            // An apostrophe inside a word ("Don't", "It's"), never right after its first letter ("I'm").
            if (core[i] is not ('\'' or '’') || i < 2 || i + 1 >= core.Length || !IsLower(core[i - 1]) || !IsLower(core[i + 1])) return null;
        }
        // "Mr.", "Dr.", "St.": short words with a full stop are abbreviations.
        if (tokenText.Length > core.Length && tokenText[core.Length] == '.' && core.Length <= 3) return null;
        var lower = core.ToLowerInvariant();
        var terms = vocabulary.Select(t => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "");
        if (terms.Contains(lower) || !isCommonWord(lower.Replace('’', '\''))) return null;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
}
