using System.Globalization;
using System.Text;

namespace TypelessCore;

/// <summary>
/// A word the user fixed by hand after dictating: what speech recognition produced, and what they
/// changed it to.
/// </summary>
public sealed record Correction(string Heard, string Corrected);

/// <summary>
/// Learns vocabulary from the edits people make to dictated text, the way Wispr Flow and Typeless do.
///
/// Only small, sound-alike replacements are learned ("TypeList" → "Typeless", "逻辑鼠标" → "罗技鼠标").
/// Everything else is ignored on purpose: rewrites, pure insertions or deletions, changed numbers (facts,
/// not recognition errors), capitalising the first letter, and swapping one ordinary word for another.
/// A wrong entry in the vocabulary costs more than a missed one.
/// </summary>
public static class CorrectionLearner
{
    // MARK: Locating the dictated text

    /// <summary>
    /// The text that now sits where the dictated text was inserted. <paramref name="before"/> is the field's content
    /// right after the paste, <paramref name="after"/> its content now. Returns null when that can't be told reliably: the
    /// dictated text isn't found, or the field changed outside it (sent, cleared, edited elsewhere).
    /// </summary>
    public static string? EditedRegion(string inserted, string before, string after)
    {
        inserted = NormalizeNewlines(inserted);
        before = NormalizeNewlines(before);
        after = NormalizeNewlines(after);
        if (inserted.Length == 0) return null;
        var start = before.LastIndexOf(inserted, StringComparison.Ordinal);
        if (start < 0) return null;
        var prefix = before[..start];
        var suffix = before[(start + inserted.Length)..];
        if (after.Length < prefix.Length + suffix.Length
            || !after.StartsWith(prefix, StringComparison.Ordinal) || !after.EndsWith(suffix, StringComparison.Ordinal)) return null;
        return after[prefix.Length..(after.Length - suffix.Length)];
    }

    // MARK: Finding corrections

    /// <summary>
    /// Learnable corrections between the dictated text and the user's edited version of it.
    /// <paramref name="isCommonWord"/> tells whether a single English word is an ordinary dictionary word.
    /// </summary>
    public static List<Correction> Corrections(string original, string edited, Func<string, bool>? isCommonWord = null)
    {
        if (original == edited) return [];
        // A heavy rewrite says nothing about recognition errors.
        if (PolishMetrics.Similarity(original, edited) < 0.5) return [];
        var hunks = Changes(original, edited);
        if (hunks.Count > 5) return [];
        var seen = new HashSet<Correction>();
        return hunks.Where(c => IsLearnable(c, isCommonWord ?? (_ => false)) && seen.Add(c)).ToList();
    }

    /// <summary>Replacements between two texts, as the smallest runs of changed words (CJK: characters).</summary>
    internal static List<Correction> Changes(string original, string edited)
    {
        var a = Tokens(original);
        var b = Tokens(edited);
        // Longest common subsequence over token text.
        int n = a.Count, m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i].Text == b[j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        var result = new List<Correction>();
        var hunkA = new List<Token>();
        var hunkB = new List<Token>();
        void Flush()
        {
            if (hunkA.Count > 0 || hunkB.Count > 0) result.Add(new Correction(Span(hunkA, original), Span(hunkB, edited)));
            hunkA.Clear();
            hunkB.Clear();
        }
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Text == b[y].Text)
            {
                Flush();
                x++;
                y++;
            }
            else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                hunkB.Add(b[y++]);
            }
            else
            {
                hunkA.Add(a[x++]);
            }
        }
        Flush();
        return result;
    }

    internal static bool IsLearnable(Correction c, Func<string, bool> isCommonWord)
    {
        var heard = c.Heard.Trim();
        var term = c.Corrected.Trim();
        // Pure insertions and deletions aren't recognition errors.
        if (heard.Length == 0 || term.Length == 0 || heard == term) return false;
        if (TextMetrics.CharacterCount(term) > 40 || LatinWords(term).Count > 4) return false;
        var cjk = term.EnumerateRunes().Count(TranscriptJoiner.IsCJK);
        var letters = term.EnumerateRunes().Count(Rune.IsLetter);
        if (letters < 2 || cjk > 8) return false;
        // One changed Chinese character is too little to be a word worth learning.
        if (cjk == letters && cjk < 2) return false;
        // Numbers are facts the speaker changed, not something recognition got wrong.
        if (Digits(heard) != Digits(term)) return false;
        if (heard.ToLowerInvariant() == term.ToLowerInvariant())
        {
            // Only distinctive casing is worth learning (SwiftUI, iOS, GitHub), not "hello" → "Hello".
            return HasInnerCapital(term);
        }
        // A different-sounding word is a change of mind, not a mishearing.
        if (PhoneticDistance(heard, term) > 0.5) return false;
        // Swapping one ordinary word for another is an edit, not a name or term.
        var heardWords = LatinWords(heard);
        var termWords = LatinWords(term);
        if (heardWords.Count == 1 && termWords.Count == 1 && cjk == 0 && !HasInnerCapital(term)
            && isCommonWord(heardWords[0]) && isCommonWord(termWords[0]))
        {
            return false;
        }
        return true;
    }

    // MARK: Helpers

    internal readonly record struct Token(string Text, int Start, int End);

    /// <summary>
    /// Latin words (with joiners like "." "-" "+" "#"), single CJK characters, and single punctuation marks.
    /// Whitespace separates tokens but isn't one, so "Envious Sales" → "EnviousSales" is one replacement.
    /// </summary>
    internal static List<Token> Tokens(string text)
    {
        var result = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var character = Rune.GetRuneAt(text, index);
            var next = index + character.Utf16SequenceLength;
            if (Rune.IsWhiteSpace(character))
            {
                index = next;
            }
            else if (IsWordCharacter(character))
            {
                var end = next;
                while (end < text.Length)
                {
                    var c = Rune.GetRuneAt(text, end);
                    if (IsWordCharacter(c))
                    {
                        end += c.Utf16SequenceLength;
                        continue;
                    }
                    var after = end + c.Utf16SequenceLength;
                    // Joiners inside a word ("Node.js", "gpt-5") and trailing "+"/"#" ("C++", "C#").
                    if (c.Value is '.' or '_' or '-' or '\'' && after < text.Length && IsWordCharacter(Rune.GetRuneAt(text, after)))
                    {
                        end = after;
                        continue;
                    }
                    if (c.Value is '+' or '#')
                    {
                        end = after;
                        continue;
                    }
                    break;
                }
                result.Add(new Token(text[index..end], index, end));
                index = end;
            }
            else
            {
                result.Add(new Token(text[index..next], index, next));
                index = next;
            }
        }
        return result;
    }

    private static bool IsWordCharacter(Rune c) => !TranscriptJoiner.IsCJK(c) && (Rune.IsLetter(c) || Rune.IsNumber(c));

    private static string Span(List<Token> tokens, string text) =>
        tokens.Count == 0 ? "" : text[tokens[0].Start..tokens[^1].End];

    internal static List<string> LatinWords(string text) =>
        text.Split(c => !(char.IsAscii(c) && char.IsLetterOrDigit(c))).Where(w => char.IsLetter(w[0])).ToList();

    private static bool HasInnerCapital(string text) =>
        text.Split(char.IsWhiteSpace).Any(word => word.Skip(1).Any(char.IsUpper));

    /// <summary>Chinese numerals count as digits too: 五十 → 六十 is a changed number, not a mishearing.</summary>
    private const string ChineseNumerals = "〇零一二两三四五六七八九十百千万亿壹贰叁肆伍陆柒捌玖拾佰仟";

    private static string Digits(string text) =>
        new(text.Where(c => char.IsNumber(c) || ChineseNumerals.Contains(c)).ToArray());

    /// <summary>
    /// How differently two spellings sound, 0 (same) … 1: Chinese is compared by pinyin, so homophones
    /// like 逻辑 / 罗技 count as identical, and "swift you eye" is close to "SwiftUI".
    /// </summary>
    internal static double PhoneticDistance(string a, string b)
    {
        var x = PhoneticKey(a);
        var y = PhoneticKey(b);
        if (x.Length == 0 || y.Length == 0) return 1;
        return (double)PolishMetrics.EditDistance(x.ToCharArray(), y.ToCharArray()) / Math.Max(x.Length, y.Length);
    }

    internal static string PhoneticKey(string text)
    {
        var latin = Transliteration.ToLatin(text) ?? text;
        var plain = new StringBuilder();
        foreach (var c in latin.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAscii(c) && char.IsLetterOrDigit(c)) plain.Append(char.ToLowerInvariant(c));
        }
        return plain.ToString();
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
