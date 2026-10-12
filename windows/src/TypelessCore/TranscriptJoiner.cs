using System.Text;

namespace TypelessCore;

public static class TranscriptJoiner
{
    /// <summary>
    /// Joins per-chunk transcripts. Chinese and Japanese text is concatenated directly; elsewhere (Latin letters,
    /// digits, Korean, which puts spaces between words) a space goes in, so "hello" + "world" doesn't become
    /// "helloworld".
    /// </summary>
    public static string Join(IEnumerable<string> parts)
    {
        var result = new StringBuilder();
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            if (result.Length > 0
                && Rune.DecodeLastFromUtf16(result.ToString(), out var last, out _) == System.Buffers.OperationStatus.Done
                && Rune.DecodeFromUtf16(part, out var first, out _) == System.Buffers.OperationStatus.Done
                && NeedsSpace(last, first))
            {
                result.Append(' ');
            }
            result.Append(part);
        }
        return result.ToString();
    }

    private static bool NeedsSpace(Rune a, Rune b)
    {
        if (JoinsWithoutSpace(a) || JoinsWithoutSpace(b)) return false;
        if (Rune.IsWhiteSpace(a)) return false;
        // After sentence punctuation like "." or "," a space reads naturally in Latin text.
        if (Rune.IsPunctuation(b)) return false;
        return true;
    }

    /// <summary>
    /// Script written without spaces between words: Han, kana, CJK punctuation and full-width forms. Not Hangul, unlike
    /// <see cref="IsCJK"/>: Korean separates its words with spaces, so two Korean chunks need one between them.
    /// </summary>
    internal static bool JoinsWithoutSpace(Rune s) => IsCJK(s) && s.Value is not (>= 0xAC00 and <= 0xD7AF);

    internal static bool IsCJK(Rune s) => s.Value switch
    {
        >= 0x3000 and <= 0x303F => true, // CJK punctuation
        >= 0x3040 and <= 0x30FF => true, // Kana
        >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF => true, // Han
        >= 0xAC00 and <= 0xD7AF => true, // Hangul
        >= 0xFF00 and <= 0xFFEF => true, // Full-width forms
        _ => false,
    };
}
