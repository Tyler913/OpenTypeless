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
/// Only small, sound-alike replacements are learned ("TypeList" → "Typeless", "逻辑鼠标" → "罗技鼠标", "克劳德" →
/// "Claude"); a fix of one Chinese character learns the word it belongs to ("罗级鼠标" → "罗技鼠标" learns 罗技).
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

    /// <summary>One replacement between the dictated text and the edited one, and why it isn't learned (null when it is).</summary>
    public sealed record Change(Correction Correction, string? Rejected);

    /// <summary>What the learner made of an edit: why it was skipped as a whole, or each replacement and its verdict.</summary>
    public sealed record Review(string? Skipped, IReadOnlyList<Change> Changes)
    {
        public List<Correction> Learnable => Changes.Where(c => c.Rejected == null).Select(c => c.Correction).Distinct().ToList();
    }

    /// <summary>
    /// Learnable corrections between the dictated text and the user's edited version of it.
    /// <paramref name="isCommonWord"/> tells whether a single English word is an ordinary dictionary word;
    /// <paramref name="words"/> splits Chinese text into words (see <see cref="Review"/>).
    /// </summary>
    public static List<Correction> Corrections(string original, string edited, Func<string, bool>? isCommonWord = null,
                                               Func<string, IReadOnlyList<(int Start, int End)>?>? words = null) =>
        ReviewEdit(original, edited, isCommonWord, words).Learnable;

    /// <summary>
    /// Every replacement between the two texts with its verdict. A fix inside Chinese text is widened to the whole
    /// word it falls in (per <paramref name="words"/>, run on the edited text), so correcting one wrong character of
    /// 罗级鼠标 learns 罗技, heard as 罗级. Without a word segmenter nothing is widened.
    /// </summary>
    public static Review ReviewEdit(string original, string edited, Func<string, bool>? isCommonWord = null,
                                    Func<string, IReadOnlyList<(int Start, int End)>?>? words = null)
    {
        if (original == edited) return new Review("unchanged", []);
        // A heavy rewrite says nothing about recognition errors.
        if (PolishMetrics.Similarity(original, edited) < 0.5) return new Review("rewrite", []);
        var hunks = Changes(original, edited, words?.Invoke(edited));
        if (hunks.Count > 5) return new Review($"{hunks.Count} changes", []);
        return new Review(null, hunks.Select(c => new Change(c, Rejection(c, isCommonWord ?? (_ => false)))).ToList());
    }

    /// <summary>
    /// Replacements between two texts, as the smallest runs of changed words (CJK: characters), each widened to
    /// whole Chinese words when the edited text's <paramref name="words"/> are given.
    /// </summary>
    internal static List<Correction> Changes(string original, string edited, IReadOnlyList<(int Start, int End)>? words = null)
    {
        var a = Tokens(original);
        var b = Tokens(edited);
        // Longest common subsequence over token text. The tokens both texts start and end with are left out of the
        // table: after a small fix to a long dictation they are nearly all of it, and this runs on the UI thread.
        int n = a.Count, m = b.Count;
        var start = 0;
        while (start < n && start < m && a[start].Text == b[start].Text) start++;
        var end = 0;
        while (end < n - start && end < m - start && a[n - 1 - end].Text == b[m - 1 - end].Text) end++;
        int endA = n - end, endB = m - end;
        var lcs = new int[endA - start + 1, endB - start + 1];
        for (var i = endA - start - 1; i >= 0; i--)
        {
            for (var j = endB - start - 1; j >= 0; j--)
            {
                lcs[i, j] = a[start + i].Text == b[start + j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        // Whether the walk below skips an edited token rather than an original one, as a table over every token would
        // decide. Inside the table it is the usual comparison (the shared end adds the same to both sides); past its
        // edge only the shared end is left, and the full table skips on the side that is less far into it.
        bool SkipEdited(int x, int y) => x >= endA || y >= endB
            ? x - endA > y - endB
            : lcs[x - start, y + 1 - start] >= lcs[x + 1 - start, y - start];
        // Hunks as token index ranges [start, end), and which original token each unchanged edited token matches.
        var hunks = new List<(int A0, int A1, int B0, int B1)>();
        var matchOf = new int[m];
        Array.Fill(matchOf, -1);
        int x = 0, y = 0, startA = 0, startB = 0;
        void Flush()
        {
            if (x > startA || y > startB) hunks.Add((startA, x, startB, y));
        }
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Text == b[y].Text)
            {
                Flush();
                matchOf[y] = x;
                x++;
                y++;
                startA = x;
                startB = y;
            }
            else if (y < m && SkipEdited(x, y))
            {
                y++;
            }
            else
            {
                x++;
            }
        }
        Flush();
        var result = new List<Correction>();
        foreach (var (a0, a1, b0, b1) in hunks)
        {
            int l = 0, r = 0;
            if (words is { Count: > 0 } && a1 > a0 && b1 > b0)
            {
                var (left, right) = Widening(b, b0, b1, edited, words);
                // Only across unchanged tokens that line up on both sides.
                while (l < left && a0 - l - 1 >= 0 && matchOf[b0 - l - 1] == a0 - l - 1) l++;
                while (r < right && a1 + r < n && matchOf[b1 + r] == a1 + r) r++;
            }
            result.Add(new Correction(Span(a, a0 - l, a1 + r, original), Span(b, b0 - l, b1 + r, edited)));
        }
        return result;
    }

    /// <summary>Particles and the like: a fix of one of these is grammar, never part of a word worth learning.</summary>
    private const string Particles = "的地得了着过吗呢吧啊呀嘛么哦哈";
    /// <summary>Widening a fix never makes a Chinese word longer than this.</summary>
    private const int MaxWidenedCharacters = 4;

    /// <summary>
    /// How many tokens to add on the left and right of a hunk of the edited text so it covers whole words. A fix that
    /// is still a single character after that (the word segmenter splits names it doesn't know, like 千|问 or 飞|书)
    /// takes in the neighbouring single-character words too.
    /// </summary>
    private static (int Left, int Right) Widening(List<Token> tokens, int b0, int b1, string text, IReadOnlyList<(int Start, int End)> words)
    {
        var changed = string.Concat(tokens.Skip(b0).Take(b1 - b0).Select(t => t.Text));
        if (!changed.EnumerateRunes().Any(TranscriptJoiner.IsCJK) || (changed.Length == 1 && Particles.Contains(changed[0]))) return (0, 0);
        int lower = tokens[b0].Start, upper = tokens[b1 - 1].End;
        var first = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].End > lower) { first = i; break; }
        }
        var last = -1;
        for (var i = words.Count - 1; i >= 0; i--)
        {
            if (words[i].Start < upper) { last = i; break; }
        }
        if (first < 0 || last < first) return (0, 0);
        bool IsSingle(int index)
        {
            var word = text[words[index].Start..words[index].End];
            return word.EnumerateRunes().Count() == 1 && word.EnumerateRunes().All(TranscriptJoiner.IsCJK) && !Particles.Contains(word[0]);
        }
        int Length() => text[words[first].Start..words[last].End].EnumerateRunes().Count();
        if (Length() == 1)
        {
            while (first > 0 && IsSingle(first - 1) && words[first - 1].End == words[first].Start && Length() < MaxWidenedCharacters) first--;
            while (last + 1 < words.Count && IsSingle(last + 1) && words[last].End == words[last + 1].Start && Length() < MaxWidenedCharacters) last++;
        }
        if (Length() > MaxWidenedCharacters) return (0, 0);
        int from = Math.Min(lower, words[first].Start), to = Math.Max(upper, words[last].End);
        // Only whole tokens: a word boundary inside a Latin token widens nothing on that side.
        var left = 0;
        for (var i = b0 - 1; i >= 0 && tokens[i].Start >= from; i--) left++;
        var right = 0;
        for (var i = b1; i < tokens.Count && tokens[i].End <= to; i++) right++;
        return (left, right);
    }

    internal static bool IsLearnable(Correction c, Func<string, bool> isCommonWord) => Rejection(c, isCommonWord) == null;

    /// <summary>Why a replacement isn't learned, or null when it is.</summary>
    internal static string? Rejection(Correction c, Func<string, bool> isCommonWord)
    {
        var heard = c.Heard.Trim();
        var term = c.Corrected.Trim();
        // Pure insertions and deletions aren't recognition errors.
        if (heard.Length == 0 || term.Length == 0 || heard == term) return "insertion or deletion";
        if (TextMetrics.CharacterCount(term) > 40 || LatinWords(term).Count > 4) return "too long";
        var cjk = term.EnumerateRunes().Count(TranscriptJoiner.IsCJK);
        var letters = term.EnumerateRunes().Count(Rune.IsLetter);
        if (letters < 2) return "too short";
        if (cjk > 8) return "too long";
        // One Chinese character is too little to be a word worth learning.
        if (cjk == letters && cjk < 2) return "single character";
        // Numbers are facts the speaker changed, not something recognition got wrong.
        if (Digits(heard) != Digits(term)) return "numbers changed";
        if (heard.ToLowerInvariant() == term.ToLowerInvariant())
        {
            // Only distinctive casing is worth learning (SwiftUI, iOS, GitHub), not "hello" → "Hello".
            return HasInnerCapital(term) ? null : "only capitalisation";
        }
        // A different-sounding word is a change of mind, not a mishearing.
        if (!SoundsAlike(heard, term)) return "sounds different";
        // Swapping one ordinary word for another is an edit, not a name or term.
        var heardWords = LatinWords(heard);
        var termWords = LatinWords(term);
        if (heardWords.Count == 1 && termWords.Count == 1 && cjk == 0 && !HasInnerCapital(term)
            && isCommonWord(heardWords[0]) && isCommonWord(termWords[0]))
        {
            return "ordinary words";
        }
        return null;
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

    private static string Span(List<Token> tokens, int start, int end, string text) =>
        end <= start ? "" : text[tokens[start].Start..tokens[end - 1].End];

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

    /// <summary>
    /// Close enough in spelling (<see cref="PhoneticDistance"/>), or in sound: English words that are spelt differently
    /// but sound alike ("Versel" / "Vercel"), and English terms heard as Chinese (克劳德 / Claude, 杰森 / JSON).
    /// </summary>
    internal static bool SoundsAlike(string a, string b)
    {
        if (PhoneticDistance(a, b) <= 0.5) return true;
        var x = SoundKey(a);
        var y = SoundKey(b);
        if (x.Length < 2 || y.Length < 2) return false;
        return (double)PolishMetrics.EditDistance(x.ToCharArray(), y.ToCharArray()) / Math.Max(x.Length, y.Length) <= 1.0 / 3;
    }

    /// <summary>How a text sounds: each Latin word (Chinese as pinyin) reduced to its consonant sounds, Metaphone-style.</summary>
    internal static string SoundKey(string text)
    {
        var latin = Transliteration.ToLatin(text) ?? text;
        var plain = new StringBuilder();
        foreach (var c in latin.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsAscii(c) && char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Concat(plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Metaphone));
    }

    /// <summary>A simplified Metaphone: consonant sounds of one lowercase word (vowels only at the start, as "A").</summary>
    internal static string Metaphone(string word)
    {
        var w = new StringBuilder(word);
        if (w.Length >= 2 && word[..2] is "kn" or "gn" or "pn" or "wr" or "ae") w.Remove(0, 1);
        if (w.Length > 0 && w[0] == 'x') w[0] = 's';
        if (w.Length >= 2 && w[0] == 'w' && w[1] == 'h') w.Remove(1, 1);
        var s = w.ToString();
        char? At(int i) => i >= 0 && i < s.Length ? s[i] : null;
        static bool IsVowel(char? c) => c is 'a' or 'e' or 'i' or 'o' or 'u';
        static bool IsFront(char? c) => c is 'i' or 'e' or 'y';
        var key = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            char? prev = At(i - 1), next = At(i + 1), after = At(i + 2);
            if (c == prev && c != 'c') continue;
            switch (c)
            {
                case 'a' or 'e' or 'i' or 'o' or 'u':
                    if (i == 0) key.Append('A');
                    break;
                case 'b':
                    if (!(prev == 'm' && next == null)) key.Append('B');
                    break;
                case 'c':
                    if (next == 'h' || (next == 'i' && after == 'a')) key.Append('X');
                    else if (IsFront(next)) { if (prev != 's') key.Append('S'); }
                    else key.Append('K');
                    break;
                case 'd':
                    key.Append(next == 'g' && IsFront(after) ? 'J' : 'T');
                    break;
                case 'g':
                    if (next == 'h' && !IsVowel(after)) break;
                    if (next == 'n' && after == null) break;
                    if (prev == 'd' && IsFront(next)) break;
                    key.Append(IsFront(next) ? 'J' : 'K');
                    break;
                case 'h':
                    if (IsVowel(next) && prev is not ('c' or 's' or 'p' or 't' or 'g')) key.Append('H');
                    break;
                case 'k':
                    if (prev != 'c') key.Append('K');
                    break;
                case 'p':
                    key.Append(next == 'h' ? 'F' : 'P');
                    break;
                case 'q':
                    key.Append('K');
                    break;
                case 's':
                    key.Append(next == 'h' || (next == 'i' && after is 'o' or 'a') ? 'X' : 'S');
                    break;
                case 't':
                    if (next == 'i' && after is 'o' or 'a') key.Append('X');
                    else if (next == 'h') key.Append('0');
                    else if (!(next == 'c' && after == 'h')) key.Append('T');
                    break;
                case 'v':
                    key.Append('F');
                    break;
                case 'w' or 'y':
                    if (IsVowel(next)) key.Append(char.ToUpperInvariant(c));
                    break;
                case 'x':
                    key.Append("KS");
                    break;
                case 'z':
                    key.Append('S');
                    break;
                default:
                    key.Append(char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
                    break;
            }
        }
        return key.ToString();
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
