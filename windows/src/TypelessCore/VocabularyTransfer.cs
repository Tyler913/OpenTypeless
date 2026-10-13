using System.Text;

namespace TypelessCore;

/// <summary>
/// The vocabulary in and out of a file. Exported as CSV (each term with what recognition heard instead, when it was
/// learned from a correction) or as plain text, a term per line; imported from either, or from a list another app
/// exported, and merged into the vocabulary without duplicates.
/// </summary>
public static class VocabularyTransfer
{
    /// <summary>A vocabulary term, and the misheard forms it was learned from (none for a term the user typed in).</summary>
    public sealed record Term(string Text, IReadOnlyList<string> HeardAs);

    /// <summary>A header with any of these as its first column is a header, not a term ("term" is what the export writes).</summary>
    internal static readonly HashSet<string> Headers = ["term", "terms", "vocabulary", "词汇"];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>"term,heardAs" and a line per term, misheard forms separated by "; ", CRLF line ends.</summary>
    public static string ToCsv(IEnumerable<Term> terms)
    {
        var builder = new StringBuilder("term,heardAs\r\n");
        foreach (var term in terms)
        {
            builder.Append(Csv.Field(term.Text)).Append(',').Append(Csv.Field(string.Join("; ", term.HeardAs))).Append("\r\n");
        }
        return builder.ToString();
    }

    /// <summary>A term per line.</summary>
    public static string ToText(IEnumerable<Term> terms) => string.Concat(terms.Select(term => term.Text + "\n"));

    /// <summary>The file's bytes: UTF-8, the CSV with a byte-order mark so Excel reads it as UTF-8.</summary>
    public static byte[] Data(IEnumerable<Term> terms, bool csv)
    {
        if (csv) return [.. HistoryExport.ByteOrderMark, .. Encoding.UTF8.GetBytes(ToCsv(terms))];
        return Encoding.UTF8.GetBytes(ToText(terms));
    }

    /// <summary>
    /// An imported file's text: UTF-8 with or without a byte-order mark, or UTF-16 with one (Excel's "Unicode text").
    /// Null for anything else, such as a CSV Excel saved in the system's legacy encoding: guessing that would import
    /// garbled terms.
    /// </summary>
    public static string? Decode(byte[] data)
    {
        try
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return StrictUtf8.GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return StrictUtf16LittleEndian.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return StrictUtf16BigEndian.GetString(data, 2, data.Length - 2);
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// The terms in a CSV: the first column of each row (the others, like the export's misheard forms, aren't terms),
    /// after a header row.
    /// </summary>
    public static List<string> TermsFromCsv(string text)
    {
        var rows = Csv.Rows(text);
        if (rows.Count > 0 && rows[0].Count > 0 && Headers.Contains(rows[0][0].Trim().ToLowerInvariant())) rows.RemoveAt(0);
        return rows.SelectMany(row => Split(row.Count > 0 ? row[0] : "")).ToList();
    }

    /// <summary>The terms in a text file: a term per line, or separated by commas the way the vocabulary field takes them.</summary>
    public static List<string> TermsFromText(string text) => Split(text);

    /// <summary>
    /// What importing adds: the imported terms not already in the vocabulary, compared without case, each once and
    /// spelled as it first appears, in the file's order.
    /// </summary>
    public static List<string> NewTerms(IEnumerable<string> imported, IEnumerable<string> existing)
    {
        var seen = existing.Select(term => term.ToLowerInvariant()).ToHashSet();
        return imported.Where(term => seen.Add(term.ToLowerInvariant())).ToList();
    }

    /// <summary>
    /// Splits where the vocabulary field does (commas, full-width commas, line breaks), since a term that held one would
    /// be cut there once it's in the field anyway.
    /// </summary>
    private static List<string> Split(string text) =>
        text.Split([',', '，', '\n', '\r']).Select(term => term.Trim()).Where(term => term.Length > 0).ToList();
}
