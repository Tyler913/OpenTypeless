using System.Text;
using System.Text.Json.Serialization;

namespace TypelessCore;

/// <summary>A spoken shortcut: say <see cref="Trigger"/> in a dictation and <see cref="Expansion"/> goes in in its place, word for word.</summary>
public sealed record Snippet
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("trigger")] public string Trigger { get; init; } = "";
    [JsonPropertyName("expansion")] public string Expansion { get; init; } = "";
}

/// <summary>
/// Puts snippets' expansions in place of their triggers in a dictation's final text.
///
/// It's done in code once the text is final (cleaned up, or as transcribed when clean-up was off or fell back), never by
/// the clean-up model: a model paraphrases and translates a long expansion, and the clean-up's guards would take one for
/// an answer. The triggers reach speech-to-text and the clean-up as vocabulary instead (see <see cref="Vocabulary"/>), so
/// they come through as written. Like OpenWhispr's snippets (longest trigger first) and Superwhisper's replacements (code,
/// no AI), with these differences:
/// - a trigger is found however the clean-up wrote it: in any case, in full-width or half-width forms, with spaces put in
///   between Chinese and English, or a line break for a space;
/// - word boundaries only count at a Latin edge. Chinese and Japanese have no spaces between words, so "我的邮箱" is found
///   in "发到我的邮箱吧", which a <c>\b</c> regex misses, while "email" is not found in "emails";
/// - the text is searched once, so an expansion that went in is never searched for other triggers.
/// </summary>
public static class SnippetExpander
{
    /// <summary>Wispr Flow's limits: a trigger is a short phrase, an expansion up to a long template.</summary>
    public const int MaxTriggerLength = 60;
    public const int MaxExpansionLength = 4000;

    /// <summary>
    /// <paramref name="text"/> with every trigger in it replaced by its snippet's expansion. Where triggers overlap, the
    /// longest wins. A dictation that is nothing but one trigger, with the punctuation the clean-up gave it, becomes exactly
    /// the expansion. Snippets with no trigger or a blank expansion are left out.
    /// </summary>
    public static string Expand(string text, IReadOnlyList<Snippet> snippets)
    {
        var patterns = new List<Pattern>();
        for (var index = 0; index < snippets.Count; index++)
        {
            var snippet = snippets[index];
            var trigger = UnitsOf(snippet.Trigger);
            if (trigger.Length == 0 || snippet.Expansion.Trim().Length == 0) continue;
            patterns.Add(new Pattern(trigger, snippet.Expansion, index));
        }
        if (patterns.Count == 0 || text.Length == 0) return text;
        var runes = text.EnumerateRunes().ToArray();
        // Where each rune starts in the text, so what's kept is copied from it as it is.
        var offsets = new int[runes.Length + 1];
        for (var k = 0; k < runes.Length; k++) offsets[k + 1] = offsets[k] + runes[k].Utf16SequenceLength;
        var units = Normalize(runes);

        // Where each character is, so a trigger is only tried where its first character is.
        var positions = new Dictionary<int, List<int>>();
        for (var index = 0; index < units.Count; index++)
        {
            if (!positions.TryGetValue(units[index].Scalar, out var list)) positions[units[index].Scalar] = list = [];
            list.Add(index);
        }
        // Longest first: a longer trigger is the more specific one ("my address in English" over "my address").
        var claimed = new bool[units.Count];
        var matches = new List<Match>();
        foreach (var pattern in patterns.OrderByDescending(p => p.Units.Length).ThenBy(p => p.Order))
        {
            var n = pattern.Units.Length;
            if (!positions.TryGetValue(pattern.Units[0], out var candidates)) continue;
            var next = 0;
            foreach (var i in candidates)
            {
                if (i < next || i + n > units.Count || !Found(pattern, i, units) || Array.IndexOf(claimed, true, i, n) >= 0) continue;
                for (var k = i; k < i + n; k++) claimed[k] = true;
                matches.Add(new Match(units[i].Start, units[i + n - 1].End, pattern.Expansion));
                next = i + n;
            }
        }
        if (matches.Count == 0) return text;
        matches.Sort((a, b) => a.Start.CompareTo(b.Start));

        // The dictation was the trigger alone: the full stop the clean-up gave it isn't part of the snippet.
        if (matches.Count == 1)
        {
            var only = matches[0];
            var alone = true;
            for (var k = 0; k < runes.Length && alone; k++)
            {
                if ((k < only.Start || k >= only.End) && !IsSpaceOrClausePunctuation(runes[k])) alone = false;
            }
            if (alone) return only.Expansion;
        }

        var output = new StringBuilder(text.Length);
        Rune? last = null;
        // Appends a piece, with a space first where it meets an expansion and the two would otherwise run together.
        void Append(ReadOnlySpan<char> piece, bool spaced)
        {
            if (piece.IsEmpty) return;
            Rune.DecodeFromUtf16(piece, out var first, out _);
            if (spaced && last is { } previous && NeedsSpace(previous, first)) output.Append(' ');
            output.Append(piece);
            Rune.DecodeLastFromUtf16(piece, out var end, out _);
            last = end;
        }

        var cursor = 0;
        var expanded = false;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (cursor < match.Start) Append(text.AsSpan(offsets[cursor], offsets[match.Start] - offsets[cursor]), expanded);
            Append(match.Expansion, true);
            cursor = match.End;
            expanded = true;
            // An expansion that ends a sentence or is a block of lines already ends there: the clean-up's punctuation after
            // the trigger would double it ("…回复你。。").
            Rune.DecodeLastFromUtf16(match.Expansion, out var final, out _);
            if (IsClausePunctuation(Fold(final)) || match.Expansion.Contains('\n') || match.Expansion.Contains('\r'))
            {
                var limit = index + 1 < matches.Count ? matches[index + 1].Start : runes.Length;
                while (cursor < limit && IsClausePunctuation(Fold(runes[cursor]))) cursor++;
            }
        }
        if (cursor < runes.Length) Append(text.AsSpan(offsets[cursor]), expanded);
        return output.ToString();
    }

    /// <summary>
    /// The snippets' triggers, then the vocabulary terms that aren't among them, for speech-to-text's spelling hints and the
    /// clean-up's vocabulary: a trigger has to come through both as written for its snippet to go in. Triggers go first,
    /// since hints stop after the first terms (see VocabularyHints). Without snippets, <paramref name="terms"/> as they are.
    /// </summary>
    public static List<string> Vocabulary(IReadOnlyList<Snippet> snippets, IReadOnlyList<string> terms)
    {
        if (snippets.Count == 0) return terms.ToList();
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var term in snippets.Select(s => s.Trigger).Concat(terms))
        {
            var trimmed = term.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed.ToLowerInvariant())) continue;
            result.Add(trimmed);
        }
        return result;
    }

    /// <summary>
    /// A trigger as it's compared, for telling whether two triggers are the same one ("My email." and "my email" are).
    /// Empty when there's nothing to find.
    /// </summary>
    public static string Key(string trigger)
    {
        var key = new StringBuilder();
        foreach (var value in UnitsOf(trigger)) key.Append(new Rune(value).ToString());
        return key.ToString();
    }

    // MARK: Matching

    private sealed record Pattern(int[] Units, string Expansion, int Order);

    /// <summary>Where a trigger was found, in the text's runes.</summary>
    private readonly record struct Match(int Start, int End, string Expansion);

    /// <summary>One character of the text as compared, with the runes of the text it stands for.</summary>
    private readonly record struct Unit(int Scalar, int Start, int End);

    private static bool Found(Pattern pattern, int i, List<Unit> units)
    {
        var n = pattern.Units.Length;
        for (var k = 0; k < n; k++)
        {
            if (units[i + k].Scalar != pattern.Units[k]) return false;
        }
        // A trigger that starts or ends with a Latin letter or digit is a whole word there. Next to Chinese, Japanese or
        // Korean (whose particles attach to the word before) anything goes.
        if (IsLatinWord(pattern.Units[0]) && i > 0 && IsLatinWord(units[i - 1].Scalar)) return false;
        if (IsLatinWord(pattern.Units[n - 1]) && i + n < units.Count && IsLatinWord(units[i + n].Scalar)) return false;
        return true;
    }

    /// <summary>
    /// The text as compared: folded (see <see cref="Fold"/>), each run of spaces or line breaks as one space, and none at all
    /// next to Chinese or Japanese, where the clean-up puts them around English words ("我的GitHub" is "我的 GitHub").
    /// </summary>
    private static List<Unit> Normalize(Rune[] runes)
    {
        var units = new List<Unit>(runes.Length);
        var i = 0;
        while (i < runes.Length)
        {
            if (!Rune.IsWhiteSpace(runes[i]))
            {
                units.Add(new Unit(Fold(runes[i]), i, i + 1));
                i++;
                continue;
            }
            var j = i + 1;
            while (j < runes.Length && Rune.IsWhiteSpace(runes[j])) j++;
            var spaceless = (i > 0 && JoinsWithoutSpace(Fold(runes[i - 1]))) || (j < runes.Length && JoinsWithoutSpace(Fold(runes[j])));
            if (!spaceless) units.Add(new Unit(0x20, i, j));
            i = j;
        }
        return units;
    }

    /// <summary>A trigger as compared: like the text, without the spaces around it or the punctuation typed after it.</summary>
    private static int[] UnitsOf(string trigger)
    {
        var units = Normalize(trigger.EnumerateRunes().ToArray()).Select(u => u.Scalar).ToList();
        while (units.Count > 0 && (units[^1] == 0x20 || IsClausePunctuation(units[^1]))) units.RemoveAt(units.Count - 1);
        while (units.Count > 0 && units[0] == 0x20) units.RemoveAt(0);
        return units.ToArray();
    }

    /// <summary>Full-width ASCII as ASCII ("ＧＰＴ", "，"), then lowercase.</summary>
    private static int Fold(Rune rune)
    {
        var value = rune.Value;
        if (value is >= 0xFF01 and <= 0xFF5E) value -= 0xFEE0;
        return Rune.ToLowerInvariant(new Rune(value)).Value;
    }

    // MARK: Joining

    /// <summary>
    /// Two Latin words, or Chinese or Japanese next to a Latin word, spaced the way the clean-up writes them ("发到
    /// name@example.com 吧"). Korean particles attach to the word before, so nothing goes in next to Hangul.
    /// </summary>
    private static bool NeedsSpace(Rune a, Rune b)
    {
        bool latinA = IsLatinWord(Fold(a)), latinB = IsLatinWord(Fold(b));
        bool hanA = WordCount.IsCjkWordCharacter(a.Value), hanB = WordCount.IsCjkWordCharacter(b.Value);
        return latinA && (latinB || hanB) || hanA && latinB;
    }

    // MARK: Characters

    /// <summary>A letter, digit or mark outside Chinese, Japanese and Korean.</summary>
    private static bool IsLatinWord(int value) =>
        Rune.IsValid(value) && WordCount.IsWordCharacter(value) && !TranscriptJoiner.IsCJK(new Rune(value));

    private static bool JoinsWithoutSpace(int value) => Rune.IsValid(value) && TranscriptJoiner.JoinsWithoutSpace(new Rune(value));

    /// <summary>What ends a clause or a sentence, as folded: full-width ，。！？；： are ASCII by then.</summary>
    private static bool IsClausePunctuation(int value) =>
        value is 0x2E or 0x2C or 0x3B or 0x3A or 0x21 or 0x3F // . , ; : ! ?
            or 0x3002 or 0x3001 or 0x2026; // 。、…

    private static bool IsSpaceOrClausePunctuation(Rune rune) => Rune.IsWhiteSpace(rune) || IsClausePunctuation(Fold(rune));
}
