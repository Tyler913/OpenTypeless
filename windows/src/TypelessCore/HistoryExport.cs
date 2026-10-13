using System.Globalization;
using System.Text;

namespace TypelessCore;

/// <summary>
/// History as a file the user keeps: CSV for a spreadsheet, JSON for a script, Markdown to read. Every number and date
/// is written by hand rather than by a formatter's defaults, so both apps write exactly the same bytes for the same
/// dictations whatever the system's language or region (testdata/export-cases.json holds the expected files).
/// </summary>
public static class HistoryExport
{
    public enum Format { Csv, Json, Markdown }

    public static string FileExtension(this Format format) => format switch
    {
        Format.Csv => "csv",
        Format.Json => "json",
        _ => "md",
    };

    /// <summary>One dictation as it's exported.</summary>
    /// <param name="UtcOffsetMinutes">
    /// The local time's offset from UTC on that date, in minutes: a dictation from before a daylight-saving change keeps
    /// the offset it was made in.
    /// </param>
    /// <param name="Status">As stored in session.json: "done", "polishFailed", "failed", "cancelled", "recording" or "processing".</param>
    /// <param name="Cost">USD; null when nothing could be priced.</param>
    public sealed record Entry(DateTimeOffset Date, int UtcOffsetMinutes, double Duration, string Status, string FinalText,
                               string RawText, double? Cost);

    /// <summary>The CSV's columns and the JSON's keys, in order.</summary>
    public static readonly string[] Fields = ["date", "durationSeconds", "status", "finalText", "rawText", "costUSD", "words"];

    /// <summary>Lets Excel open a CSV as UTF-8: without it, Chinese comes out garbled.</summary>
    public static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

    /// <summary>The file's bytes: UTF-8, the CSV with a byte-order mark.</summary>
    public static byte[] Data(IReadOnlyList<Entry> entries, Format format)
    {
        if (format == Format.Csv) return [.. ByteOrderMark, .. Encoding.UTF8.GetBytes(ToCsv(entries))];
        return Encoding.UTF8.GetBytes(format == Format.Json ? ToJson(entries) : ToMarkdown(entries));
    }

    /// <summary>
    /// RFC 4180: a header, then a line per dictation, CRLF line ends, fields quoted when they hold a comma, a quote or a
    /// line break.
    /// </summary>
    public static string ToCsv(IReadOnlyList<Entry> entries)
    {
        var builder = new StringBuilder(string.Join(",", Fields)).Append("\r\n");
        foreach (var entry in entries)
        {
            string[] row =
            [
                Timestamp(entry.Date, entry.UtcOffsetMinutes), Seconds(entry.Duration), entry.Status, entry.FinalText,
                entry.RawText, entry.Cost is { } cost ? Usd(cost) : "", Words(entry),
            ];
            builder.Append(string.Join(",", row.Select(Csv.Field))).Append("\r\n");
        }
        return builder.ToString();
    }

    /// <summary>An array of objects with the same fields as the CSV, in the same order, two-space indented.</summary>
    public static string ToJson(IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) return "[]\n";
        var objects = entries.Select(entry =>
        {
            string[] values =
            [
                JsonString(Timestamp(entry.Date, entry.UtcOffsetMinutes)), Seconds(entry.Duration), JsonString(entry.Status),
                JsonString(entry.FinalText), JsonString(entry.RawText), entry.Cost is { } cost ? Usd(cost) : "null", Words(entry),
            ];
            return "  {\n" + string.Join(",\n", Fields.Zip(values, (key, value) => "    \"" + key + "\": " + value)) + "\n  }";
        });
        return "[\n" + string.Join(",\n", objects) + "\n]\n";
    }

    /// <summary>
    /// A heading for each day and each dictation's time under it, then its text, a paragraph per line. Dictations
    /// without text are left out: there's nothing to read.
    /// </summary>
    public static string ToMarkdown(IReadOnlyList<Entry> entries)
    {
        var blocks = new List<string>();
        string? currentDay = null;
        foreach (var entry in entries)
        {
            var paragraphs = entry.FinalText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(line => line.TrimEnd(' ', '\t'))
                .Where(line => line.Length > 0)
                .ToList();
            if (paragraphs.Count == 0) continue;
            var stamp = Timestamp(entry.Date, entry.UtcOffsetMinutes);
            // "2026-10-12T14:32:05+08:00": the local day, then the hour and minute.
            var day = stamp[..10];
            if (day != currentDay)
            {
                currentDay = day;
                blocks.Add("# " + day);
            }
            blocks.Add("## " + stamp.Substring(11, 5));
            blocks.AddRange(paragraphs);
        }
        return blocks.Count == 0 ? "" : string.Join("\n\n", blocks) + "\n";
    }

    // MARK: Values

    /// <summary>ISO 8601 in the local time it was made, with its offset: "2026-10-12T14:32:05+08:00" (whole seconds, cut down).</summary>
    internal static string Timestamp(DateTimeOffset date, int utcOffsetMinutes)
    {
        // DateTimeOffset takes offsets up to 14 hours, which every time zone is within.
        var minutes = Math.Clamp(utcOffsetMinutes, -14 * 60, 14 * 60);
        var local = DateTimeOffset.FromUnixTimeSeconds(date.ToUnixTimeSeconds()).ToOffset(TimeSpan.FromMinutes(minutes));
        var offset = Math.Abs(minutes);
        return local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
               + (minutes < 0 ? "-" : "+")
               + (offset / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (offset % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Seconds to a tenth, halves rounded up: "42.3".</summary>
    internal static string Seconds(double duration)
    {
        var tenths = (long)Math.Round((double.IsFinite(duration) ? Math.Max(0, duration) : 0) * 10, MidpointRounding.AwayFromZero);
        return (tenths / 10).ToString(CultureInfo.InvariantCulture) + "." + (tenths % 10).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>USD to a millionth, halves rounded up: "0.000508", "1.200000".</summary>
    internal static string Usd(double cost)
    {
        var millionths = (long)Math.Round((double.IsFinite(cost) ? Math.Max(0, cost) : 0) * 1_000_000, MidpointRounding.AwayFromZero);
        return (millionths / 1_000_000).ToString(CultureInfo.InvariantCulture) + "." + (millionths % 1_000_000).ToString("000000", CultureInfo.InvariantCulture);
    }

    /// <summary>A JSON string literal; only what JSON requires is escaped, so Chinese stays readable.</summary>
    internal static string JsonString(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case < ' ': builder.Append("\\u00").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)); break;
                default: builder.Append(c); break;
            }
        }
        return builder.Append('"').ToString();
    }

    private static string Words(Entry entry) => WordCount.Count(entry.FinalText).ToString(CultureInfo.InvariantCulture);
}
