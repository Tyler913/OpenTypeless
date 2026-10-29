using System.Text;

namespace TypelessCore;

/// <summary>
/// The vocabulary in and out of a file. Exported as CSV (each term with what recognition heard instead, when it was
/// learned from a correction, for reference) or as plain text, a term per line; imported from either, or from a list
/// another app exported, and merged into the vocabulary without duplicates. Only terms are imported, never misheard
/// forms: those make clean-up replace words in every dictation, so they come only from the user's own corrections.
/// </summary>
public static class VocabularyTransfer
{
    /// <summary>A vocabulary term, and the misheard forms it was learned from (none for a term the user typed in).</summary>
    public sealed record Term(string Text, IReadOnlyList<string> HeardAs);

    /// <summary>A header with any of these as its first column is a header, not a term ("term" is what the export writes).</summary>
    internal static readonly HashSet<string> Headers = ["term", "terms", "word", "words", "phrase", "phrases", "vocabulary", "词", "词汇", "术语"];

    /// <summary>The files the import takes: a CSV or tab-separated table (its first column), or text.</summary>
    public static readonly string[] ImportExtensions = ["csv", "tsv", "txt"];

    /// <summary>
    /// Every term goes into every clean-up request, so an import leaves out any longer than this, in UTF-16 code units
    /// (which both apps count the same way): that's a sentence or a pasted paragraph, not a name.
    /// </summary>
    public const int MaxTermLength = 64;

    /// <summary>Nor does an import take the vocabulary past this many terms (a few thousand tokens in every request).</summary>
    public const int MaxTerms = 1000;

    /// <summary>What importing a file does to the vocabulary.</summary>
    /// <param name="Added">The new terms, each once and spelled as it first appears, in the file's order.</param>
    /// <param name="Known">Terms already in the vocabulary.</param>
    /// <param name="TooLong">Terms left out for being longer than <see cref="MaxTermLength"/>.</param>
    /// <param name="OverLimit">New terms left out because the vocabulary would hold more than <see cref="MaxTerms"/>.</param>
    public sealed record ImportPlan(List<string> Added, int Known, int TooLong, int OverLimit);

    private static readonly char[] LineBreaks = ['\r', '\n'];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>"term,heardAs" and a line per term, misheard forms separated by "; ", CRLF line ends.</summary>
    public static string ToCsv(IEnumerable<Term> terms)
    {
        var builder = new StringBuilder("term,heardAs\r\n");
        foreach (var term in terms)
        {
            builder.Append(Csv.SpreadsheetField(term.Text)).Append(',').Append(Csv.SpreadsheetField(string.Join("; ", term.HeardAs))).Append("\r\n");
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

    /// <summary>A file's terms, read the way its extension (without the dot) says: a table's first column, or text.</summary>
    public static List<string> TermsFromFile(string text, string extension) =>
        extension.ToLowerInvariant() is "csv" or "tsv" ? TermsFromCsv(text) : TermsFromText(text);

    /// <summary>
    /// An imported file's text: UTF-8 with or without a byte-order mark, or UTF-16 with one (Excel's "Unicode text").
    /// Null for anything else, such as a CSV Excel saved in the system's legacy encoding: guessing that would import
    /// garbled terms.
    /// </summary>
    public static string? Decode(byte[] data)
    {
        // An odd byte at the end of UTF-16 is half a character: the strict decoder refuses it, as macOS does.
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
    public static List<string> TermsFromCsv(string text) => TermsFromTable(Csv.Rows(text));

    /// <summary>
    /// The terms in a text file: a term per line, or separated by commas the way the vocabulary field takes them. A file
    /// whose first line holds a tab is a table (Excel saves "Unicode Text" as a .txt with tabs between the columns), so
    /// its first column is read.
    /// </summary>
    public static List<string> TermsFromText(string text)
    {
        var lineEnd = text.IndexOfAny(LineBreaks);
        var firstLine = lineEnd < 0 ? text : text[..lineEnd];
        return firstLine.Contains('\t') ? TermsFromTable(Csv.Rows(text, '\t')) : Split(text);
    }

    /// <summary>
    /// What importing <paramref name="imported"/> into a vocabulary holding <paramref name="existing"/> does. Terms are
    /// compared without case (<c>ToLowerInvariant()</c> here, <c>lowercased()</c> on macOS: they differ only for a few
    /// letters such as Turkish İ, the same as everywhere else the vocabulary is compared).
    /// </summary>
    public static ImportPlan Plan(IEnumerable<string> imported, IReadOnlyCollection<string> existing, int limit = MaxTerms)
    {
        var known = existing.Select(term => term.ToLowerInvariant()).ToHashSet();
        var seen = new HashSet<string>();
        var added = new List<string>();
        int knownCount = 0, tooLong = 0, overLimit = 0;
        foreach (var term in imported)
        {
            var key = term.ToLowerInvariant();
            if (!seen.Add(key)) continue;
            if (known.Contains(key)) knownCount++;
            else if (term.Length > MaxTermLength) tooLong++;
            else if (existing.Count + added.Count >= limit) overLimit++;
            else added.Add(term);
        }
        return new ImportPlan(added, knownCount, tooLong, overLimit);
    }

    /// <summary>The vocabulary field's text with <paramref name="terms"/> added after what's there, separated by commas.</summary>
    public static string Merged(string vocabulary, IReadOnlyCollection<string> terms)
    {
        if (terms.Count == 0) return vocabulary;
        var current = vocabulary.Trim();
        var joined = string.Join(", ", terms);
        return current.Length == 0 ? joined : current + ", " + joined;
    }

    private static List<string> TermsFromTable(List<List<string>> rows)
    {
        if (rows.Count > 0 && rows[0].Count > 0 && Headers.Contains(rows[0][0].Trim().ToLowerInvariant())) rows.RemoveAt(0);
        return rows.SelectMany(row => Split(Csv.Unprotected(row.Count > 0 ? row[0] : ""))).ToList();
    }

    /// <summary>
    /// Splits where the vocabulary field does (commas, full-width commas, line breaks), since a term that held one would
    /// be cut there once it's in the field anyway.
    /// </summary>
    private static List<string> Split(string text) =>
        text.Split([',', '，', '\n', '\r']).Select(term => term.Trim()).Where(term => term.Length > 0).ToList();
}
