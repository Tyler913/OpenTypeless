using System.Text;

namespace TypelessCore;

/// <summary>
/// Comma-separated values as RFC 4180 has them: a field holding a comma, a quote or a line break is quoted, with its
/// quotes doubled.
/// </summary>
public static class Csv
{
    private static readonly char[] Special = [',', '"', '\r', '\n'];

    /// <summary>One field, quoted only when it has to be.</summary>
    public static string Field(string text) =>
        text.IndexOfAny(Special) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// A field of a file a spreadsheet will open. One starting with = + - @, a tab or a carriage return gets an
    /// apostrophe in front, so Excel shows it as the text it is rather than running it as a formula (OWASP's advice
    /// against CSV injection); <see cref="Unprotected"/> takes the apostrophe off again when such a file is imported.
    /// Text that already starts with apostrophes before one of those gets one more, so it comes back as it was.
    /// </summary>
    public static string SpreadsheetField(string text) => Field(NeedsApostrophe(text) ? "'" + text : text);

    /// <summary>A field as it was before <see cref="SpreadsheetField"/> protected it.</summary>
    public static string Unprotected(string field) =>
        field.Length > 0 && field[0] == '\'' && NeedsApostrophe(field) ? field[1..] : field;

    private static bool NeedsApostrophe(string text)
    {
        var rest = text.TrimStart('\'');
        return rest.Length > 0 && rest[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
    }

    /// <summary>
    /// The rows of a file. Lenient about what spreadsheets write: CRLF, LF or CR line ends, a stray quote inside a field
    /// kept as it is, an unclosed quote running to the end, and a semicolon or tab as the separator when the first line
    /// uses more of those than commas (Excel in German, French or Russian saves "CSV" with semicolons), unless a
    /// <paramref name="separator"/> is given.
    /// </summary>
    public static List<List<string>> Rows(string text, char? separator = null)
    {
        var used = separator ?? Separator(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index++];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (index < text.Length && text[index] == '"')
                {
                    field.Append(c);
                    index++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == used)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && index < text.Length && text[index] == '\n') index++;
                row.Add(field.ToString());
                rows.Add(row);
                row = [];
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The separator the first line uses most, outside quotes: comma, semicolon or tab (comma when it has none).</summary>
    private static char Separator(string text)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c is '\r' or '\n') break;
            if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if (c == '\t') tabs++;
        }
        if (semicolons > commas && semicolons >= tabs) return ';';
        if (tabs > commas && tabs > semicolons) return '\t';
        return ',';
    }
}
