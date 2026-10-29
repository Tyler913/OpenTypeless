using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TypelessCore;

/// <summary>A spoken shortcut: say <see cref="Trigger"/> in a dictation and <see cref="Expansion"/> goes in in its place, word for word.</summary>
public sealed record Snippet
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("trigger")] public string Trigger { get; init; } = "";
    [JsonPropertyName("expansion")] public string Expansion { get; init; } = "";

    /// <summary>
    /// A saved list, read entry by entry: one that can't be read (null, or without its text) is left out rather than
    /// losing the others. Snippets are told apart by ID, so one with none, or with another's, gets its own;
    /// <c>Repaired</c> says the list should be saved again so the new IDs last.
    /// </summary>
    public static (List<Snippet> Snippets, bool Repaired) ReadList(JsonNode? saved)
    {
        var snippets = new List<Snippet>();
        var repaired = false;
        if (saved is not JsonArray entries) return (snippets, repaired);
        var seen = new HashSet<string>();
        foreach (var node in entries)
        {
            if (node is not JsonObject entry || Text(entry, "trigger") is not { } trigger || Text(entry, "expansion") is not { } expansion) continue;
            var id = Text(entry, "id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                id = Guid.NewGuid().ToString();
                seen.Add(id);
                repaired = true;
            }
            snippets.Add(new Snippet { Id = id, Trigger = trigger, Expansion = expansion });
        }
        return (snippets, repaired);

        static string? Text(JsonObject entry, string key) =>
            entry[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}

/// <summary>
/// Puts snippets' expansions in place of their triggers in a dictation's final text.
///
/// It's done in code once the text is final (cleaned up, or as transcribed when clean-up was off or fell back), never by
/// the clean-up model: a model paraphrases and translates a long expansion, and the clean-up's guards would take one for
/// an answer. The triggers reach speech-to-text and the clean-up as vocabulary instead (see <see cref="Vocabulary"/>), so
/// they come through as written. Like OpenWhispr's snippets (longest trigger first) and Superwhisper's replacements (code,
/// no AI), with these differences:
/// - a trigger is found however it was written: in any case, in full-width or half-width forms, composed or decomposed
///   ("é" or e + ◌́, a Hangul syllable or its jamo), with spaces put in between Chinese and English, or a line break for a
///   space. Text is compared a whole character (grapheme) at a time, so a trigger never takes part of one: "か" isn't
///   found in "が" typed as か + ◌゙;
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
            var keys = KeysOf(snippet.Trigger);
            if (keys.Length == 0 || snippet.Expansion.Trim().Length == 0) continue;
            patterns.Add(new Pattern(keys, new Piece(snippet.Expansion), index));
        }
        if (patterns.Count == 0 || text.Length == 0) return text;
        var clusters = Clusters(text);
        var units = Normalize(clusters);

        // Where each character is, so a trigger is only tried where its first character is.
        var positions = new Dictionary<string, List<int>>();
        for (var index = 0; index < units.Count; index++)
        {
            if (!positions.TryGetValue(units[index].Key, out var list)) positions[units[index].Key] = list = [];
            list.Add(index);
        }
        // Longest first: a longer trigger is the more specific one ("my address in English" over "my address").
        var claimed = new bool[units.Count];
        var matches = new List<Match>();
        foreach (var pattern in patterns.OrderByDescending(p => p.Keys.Length).ThenBy(p => p.Order))
        {
            var n = pattern.Keys.Length;
            if (!positions.TryGetValue(pattern.Keys[0], out var candidates)) continue;
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
            for (var k = 0; k < clusters.Count && alone; k++)
            {
                if ((k < only.Start || k >= only.End) && !clusters[k].IsSpace && !IsClausePunctuation(clusters[k].Base)) alone = false;
            }
            if (alone) return only.Expansion.Text;
        }

        var output = new StringBuilder(text.Length);
        // The last character put out, as compared.
        int? last = null;
        // Appends a piece, with a space first where it meets an expansion and the two would otherwise run together.
        void Append(ReadOnlySpan<char> piece, int first, int final, bool spaced)
        {
            if (spaced && last is { } previous && NeedsSpace(previous, first)) output.Append(' ');
            output.Append(piece);
            last = final;
        }
        void AppendText(int start, int end, bool spaced)
        {
            if (start >= end) return;
            var from = clusters[start].Offset;
            var to = clusters[end - 1].Offset + clusters[end - 1].Length;
            Append(text.AsSpan(from, to - from), clusters[start].Base, clusters[end - 1].Base, spaced);
        }

        var cursor = 0;
        var expanded = false;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            AppendText(cursor, match.Start, expanded);
            var expansion = match.Expansion;
            Append(expansion.Text, expansion.First, expansion.Last, true);
            cursor = match.End;
            expanded = true;
            // An expansion that ends a sentence or is a block of lines already ends there: the clean-up's punctuation after
            // the trigger, with any spaces before it ("réponse !"), would double it ("…回复你。。"). Not across a line break.
            if (expansion.EndsBlock)
            {
                var limit = index + 1 < matches.Count ? matches[index + 1].Start : clusters.Count;
                var end = cursor;
                while (end < limit && clusters[end].IsSpace && !clusters[end].IsLineBreak) end++;
                var spaces = end;
                while (end < limit && !clusters[end].IsSpace && IsClausePunctuation(clusters[end].Base)) end++;
                if (end > spaces) cursor = end;
            }
        }
        AppendText(cursor, clusters.Count, expanded);
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
    /// A trigger as it's compared, for telling whether two triggers are the same one ("My email." and "my email" are, and
    /// so are "café" composed and decomposed). Empty when there's nothing to find. It's in NFC, so the ordinal comparison
    /// here gives what macOS' canonical comparison of strings does.
    /// </summary>
    public static string Key(string trigger) => string.Concat(KeysOf(trigger));

    // MARK: Matching

    private sealed record Pattern(string[] Keys, Piece Expansion, int Order);

    /// <summary>Where a trigger was found, in the text's characters.</summary>
    private readonly record struct Match(int Start, int End, Piece Expansion);

    /// <summary>One character of the text as compared (a run of spaces counts as one), with the characters it stands for.</summary>
    private readonly record struct Unit(string Key, int Base, int Start, int End);

    /// <summary>
    /// One character (grapheme cluster) at <c>Offset</c>: its canonical composition, folded (see <see cref="Fold"/>), and
    /// what kind it is. <c>Base</c> is its first scalar as compared, which says what script it's in.
    /// </summary>
    private readonly record struct Cluster(string Key, int Base, bool IsSpace, bool IsLineBreak, int Offset, int Length);

    /// <summary>An expansion, with what it starts and ends with, for joining it to the text around it.</summary>
    private sealed class Piece
    {
        public Piece(string text)
        {
            var clusters = Clusters(text);
            Text = text;
            First = clusters.Count > 0 ? clusters[0].Base : 0x20;
            Last = clusters.Count > 0 ? clusters[^1].Base : 0x20;
            EndsBlock = (clusters.Count > 0 && IsClausePunctuation(clusters[^1].Base)) || clusters.Any(c => c.IsLineBreak);
        }

        public string Text { get; }
        public int First { get; }
        public int Last { get; }
        /// <summary>It ends a sentence, or it's a block of lines.</summary>
        public bool EndsBlock { get; }
    }

    private static bool Found(Pattern pattern, int i, List<Unit> units)
    {
        var n = pattern.Keys.Length;
        for (var k = 0; k < n; k++)
        {
            if (units[i + k].Key != pattern.Keys[k]) return false;
        }
        // A trigger that starts or ends with a Latin letter or digit is a whole word there. Next to Chinese, Japanese or
        // Korean (whose particles attach to the word before) anything goes.
        if (IsLatinWord(units[i].Base) && i > 0 && IsLatinWord(units[i - 1].Base)) return false;
        if (IsLatinWord(units[i + n - 1].Base) && i + n < units.Count && IsLatinWord(units[i + n].Base)) return false;
        return true;
    }

    /// <summary>The text's characters (grapheme clusters, as Swift's <c>Character</c>), each as compared.</summary>
    private static List<Cluster> Clusters(string text)
    {
        var clusters = new List<Cluster>();
        Span<char> buffer = stackalloc char[2];
        var offset = 0;
        while (offset < text.Length)
        {
            var length = Math.Max(1, StringInfo.GetNextTextElementLength(text.AsSpan(offset)));
            var element = text.Substring(offset, length);
            var isSpace = true;
            var isLineBreak = false;
            foreach (var rune in element.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune)) isSpace = false;
                if (IsLineBreak(rune.Value)) isLineBreak = true;
            }
            var key = new StringBuilder(length);
            foreach (var rune in Composed(element).EnumerateRunes())
            {
                var written = new Rune(Fold(rune)).EncodeToUtf16(buffer);
                key.Append(buffer[..written]);
            }
            var folded = key.ToString();
            var first = folded.Length > 0 && Rune.DecodeFromUtf16(folded, out var rune0, out _) == OperationStatus.Done ? rune0.Value : 0;
            clusters.Add(new Cluster(folded, first, isSpace, isLineBreak, offset, length));
            offset += length;
        }
        return clusters;
    }

    /// <summary>
    /// The text as compared: each run of spaces or line breaks as one space, and none at all next to Chinese or Japanese,
    /// where the clean-up puts them around English words ("我的GitHub" is "我的 GitHub").
    /// </summary>
    private static List<Unit> Normalize(List<Cluster> clusters)
    {
        var units = new List<Unit>(clusters.Count);
        var i = 0;
        while (i < clusters.Count)
        {
            if (!clusters[i].IsSpace)
            {
                units.Add(new Unit(clusters[i].Key, clusters[i].Base, i, i + 1));
                i++;
                continue;
            }
            var j = i + 1;
            while (j < clusters.Count && clusters[j].IsSpace) j++;
            var spaceless = (i > 0 && IsSpaceless(clusters[i - 1].Base)) || (j < clusters.Count && IsSpaceless(clusters[j].Base));
            if (!spaceless) units.Add(new Unit(" ", 0x20, i, j));
            i = j;
        }
        return units;
    }

    /// <summary>A trigger as compared: like the text, without the spaces around it or the punctuation typed after it.</summary>
    private static string[] KeysOf(string trigger)
    {
        var units = Normalize(Clusters(trigger));
        while (units.Count > 0 && (units[^1].Key == " " || IsClausePunctuation(units[^1].Base))) units.RemoveAt(units.Count - 1);
        while (units.Count > 0 && units[0].Key == " ") units.RemoveAt(0);
        return units.Select(u => u.Key).ToArray();
    }

    /// <summary>
    /// The character's canonical composition (NFC), so "é" typed as e + ◌́ compares like é, and a Hangul syllable like its
    /// jamo. Most characters are one scalar NFC leaves alone, which skips the conversion.
    /// </summary>
    private static string Composed(string element)
    {
        if (Rune.DecodeFromUtf16(element, out var only, out var consumed) == OperationStatus.Done && consumed == element.Length
            && only.Value is < 0x300 or (>= 0x3040 and <= 0x30FF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7A3))
        {
            return element;
        }
        try
        {
            return element.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate: compared as it is.
            return element;
        }
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
    private static bool NeedsSpace(int a, int b) =>
        (IsLatinWord(a) && (IsLatinWord(b) || IsHanOrKana(b))) || (IsHanOrKana(a) && IsLatinWord(b));

    // MARK: Characters
    //
    // One classifier for word boundaries, spaces and joining, covering every block of each script (TranscriptJoiner's
    // ranges, which join transcripts, leave out the supplementary planes).

    /// <summary>Han in every block (the supplementary planes too, e.g. 𠮷) and kana, where words aren't separated by spaces.</summary>
    internal static bool IsHanOrKana(int value) =>
        value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF // Han
            or >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0x1B000 and <= 0x1B16F or >= 0xFF66 and <= 0xFF9F; // kana

    /// <summary>Hangul syllables and jamo, half-width ones too.</summary>
    internal static bool IsHangul(int value) =>
        value is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7AF
            or >= 0xD7B0 and <= 0xD7FF or >= 0xFFA0 and <= 0xFFDC;

    /// <summary>Chinese and Japanese characters and their punctuation and symbols: no space belongs next to them.</summary>
    internal static bool IsSpaceless(int value) =>
        IsHanOrKana(value)
        || value is >= 0x2E80 and <= 0x2FDF or >= 0x2FF0 and <= 0x2FFF // radicals, ideographic description
            or >= 0x3000 and <= 0x303F // CJK punctuation
            or >= 0x3100 and <= 0x312F or >= 0x3190 and <= 0x31EF // bopomofo, kanbun, strokes
            or >= 0x3200 and <= 0x33FF // enclosed and compatibility forms
            or >= 0xFE10 and <= 0xFE1F or >= 0xFE30 and <= 0xFE4F // vertical and compatibility punctuation
            or >= 0xFF00 and <= 0xFF65 or >= 0xFFE0 and <= 0xFFEF; // full-width and half-width punctuation and symbols

    /// <summary>A letter, digit or mark outside Chinese, Japanese and Korean.</summary>
    internal static bool IsLatinWord(int value) =>
        Rune.IsValid(value) && WordCount.IsWordCharacter(value) && !IsSpaceless(value) && !IsHangul(value);

    private static bool IsLineBreak(int value) => value is 0x0A or 0x0B or 0x0C or 0x0D or 0x85 or 0x2028 or 0x2029;

    /// <summary>What ends a clause or a sentence, as folded: full-width ，。！？；： are ASCII by then.</summary>
    private static bool IsClausePunctuation(int value) =>
        value is 0x2E or 0x2C or 0x3B or 0x3A or 0x21 or 0x3F // . , ; : ! ?
            or 0x3002 or 0x3001 or 0x2026 or 0xFF61 or 0xFF64; // 。、… and half-width ｡､
}
