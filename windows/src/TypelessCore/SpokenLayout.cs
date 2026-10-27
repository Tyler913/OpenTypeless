using System.Text;

namespace TypelessCore;

/// <summary>
/// Spoken layout commands: "new line" and "new paragraph" (换行 / 新段落, 改行, 줄 바꿈, neue Zeile…) said in a dictation
/// become a line break and a blank line, before the clean-up and whether or not there is one.
///
/// Only a command said as a phrase of its own counts: with a pause before and after it, which speech recognition writes as
/// punctuation around it ("Hi Anna. New line. Thanks…", "周报，换行，登录模块…"), or at the very start or end. Said
/// within a sentence ("add a new line to the config", "这里要不要换行", "每条都换行写") the words are part of the text
/// and stay. This is decided here, by rule, rather than by the clean-up model, which can't be relied on to tell the two
/// apart, isn't there when clean-up is off or fails, and would make ordinary dictations depend on how it reads them. The
/// price is that a command run into the words around it without a pause is missed and stays as words: never a line break
/// the speaker didn't ask for. A transcript without a command comes back exactly as it was.
/// </summary>
public static class SpokenLayout
{
    /// <summary>
    /// The commands, lowercased and without spaces (so "new line", "newline" and "New Line" are one; 줄 바꿈 and 줄바꿈
    /// too), with the line breaks each stands for. Those of every language the app speaks are recognised in any dictation.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Commands = BuildCommands();

    private static Dictionary<string, string> BuildCommands()
    {
        string[] line =
        [
            "newline", // English: "new line"
            "换行", "換行", // Chinese
            "改行", // Japanese
            "줄바꿈", "새줄", "새로운줄", // Korean
            "nuevalínea", "nuevalinea", // Spanish
            "novalinha", // Portuguese
            "àlaligne", "alaligne", "nouvelleligne", // French
            "neuezeile", // German
            "новаястрока", // Russian
        ];
        string[] paragraph =
        [
            "newparagraph",
            "新段落", "另起一段", "换一段", "換一段",
            "新しい段落", "改段落",
            "새단락", "새문단", "새로운단락", "새로운문단",
            "nuevopárrafo", "nuevoparrafo",
            "novoparágrafo", "novoparagrafo",
            "nouveauparagraphe",
            "neuerabsatz",
            "новыйабзац",
        ];
        var commands = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in line) commands[key.Normalize(NormalizationForm.FormC)] = "\n";
        foreach (var key in paragraph) commands[key.Normalize(NormalizationForm.FormC)] = "\n\n";
        return commands;
    }

    /// <summary>
    /// <paramref name="transcript"/> with each command said on its own turned into its line breaks. The punctuation before
    /// a command stays ("Hi Anna," keeps its comma, a line can end in "。"); the command, the punctuation after it and the
    /// spaces around the break go. A command at the very start or end gives a break there (see <see cref="Edges"/>).
    /// </summary>
    public static string Apply(string transcript)
    {
        var pieces = Clauses(transcript);
        if (!pieces.Any(piece => LineBreaks(piece.Clause) != null)) return transcript;
        var result = new StringBuilder();
        var afterBreak = false;
        foreach (var (clause, separator) in pieces)
        {
            if (LineBreaks(clause) is { } breaks)
            {
                while (result.Length > 0 && char.IsWhiteSpace(result[result.Length - 1]) && !IsNewline(result[result.Length - 1])) result.Length--;
                result.Append(breaks);
                afterBreak = true;
                continue;
            }
            result.Append(afterBreak ? DropLeadingSpace(clause) : clause).Append(separator);
            afterBreak = false;
        }
        return result.ToString();
    }

    /// <summary>
    /// Splits off the line breaks a command at the very start or end gave, which the clean-up would otherwise lose (its
    /// answer is trimmed): they're put back around its text. Without any there, <c>Body</c> is <paramref name="text"/> as it is.
    /// </summary>
    public static (string Leading, string Body, string Trailing) Edges(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (start == text.Length) return (new string('\n', Breaks(text)), "", "");
        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        var leading = Breaks(text[..start]);
        var trailing = Breaks(text[end..]);
        var from = leading > 0 ? start : 0;
        var to = trailing > 0 ? end : text.Length;
        return (new string('\n', leading), text[from..to], new string('\n', trailing));
    }

    /// <summary>The line breaks <paramref name="clause"/> (one piece between punctuation) stands for, or null when it isn't a command.</summary>
    internal static string? LineBreaks(string clause)
    {
        var key = new string(clause.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray()).Normalize(NormalizationForm.FormC);
        return key.Length > 0 && Commands.TryGetValue(key, out var breaks) ? breaks : null;
    }

    /// <summary>The text cut into clauses, each with the run of punctuation and spaces after it.</summary>
    internal static List<(string Clause, string Separator)> Clauses(string text)
    {
        var pieces = new List<(string Clause, string Separator)>();
        var start = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (!IsBoundary(text, index))
            {
                index++;
                continue;
            }
            var end = index;
            while (end < text.Length && (char.IsWhiteSpace(text[end]) || IsBoundary(text, end))) end++;
            pieces.Add((text[start..index], text[index..end]));
            start = end;
            index = end;
        }
        if (start < text.Length) pieces.Add((text[start..], ""));
        return pieces;
    }

    /// <summary>
    /// Punctuation that ends a clause, a line break included. ".", "," and ":" only before a space, more punctuation or
    /// the end, so "3.5", "1,000" and "10:30" stay whole (as in <see cref="SegmentedPolish"/>).
    /// </summary>
    internal static bool IsBoundary(string text, int index)
    {
        var character = text[index];
        if (IsNewline(character) || AlwaysEnds.Contains(character)) return true;
        if (!EndsBeforeSpace.Contains(character)) return false;
        if (index + 1 >= text.Length) return true;
        var next = text[index + 1];
        return char.IsWhiteSpace(next) || AlwaysEnds.Contains(next) || EndsBeforeSpace.Contains(next);
    }

    private const string AlwaysEnds = "。，、！？；：．｡､…!?;";
    private const string EndsBeforeSpace = ".,:";

    /// <summary>A line break as macOS counts one (Swift's <c>Character.isNewline</c>).</summary>
    private static bool IsNewline(char c) => c is >= '\n' and <= '\r' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>The line breaks in <paramref name="text"/>, a "\r\n" counting as one, as on macOS.</summary>
    private static int Breaks(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsNewline(text[i])) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            count++;
        }
        return count;
    }

    /// <summary><paramref name="text"/> without the spaces at its start, but with its line breaks.</summary>
    private static string DropLeadingSpace(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]) && !IsNewline(text[start])) start++;
        return text[start..];
    }
}
