using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>History exports and vocabulary files: both apps write the same bytes and read imports the same way.</summary>
public class ExportTests
{
    /// <summary>testdata/export-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "export-cases.json")))!.AsObject();

    private static DateTimeOffset Date(JsonNode? unix) => DateTimeOffset.UnixEpoch.AddSeconds((double)unix!);

    private static List<HistoryExport.Entry> Entries(JsonArray items) =>
        items.Select(item => new HistoryExport.Entry(Date(item!["unix"]), (int)item["offsetMinutes"]!, (double)item["duration"]!,
                                                     (string)item["status"]!, (string)item["finalText"]!, (string)item["rawText"]!,
                                                     (double?)item["cost"])).ToList();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static List<string> Strings(JsonNode? array) => array!.AsArray().Select(t => (string)t!).ToList();

    /// <summary>The fixture names IANA zones; Windows without ICU knows only its own names.</summary>
    private static TimeZoneInfo Zone(JsonNode item)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById((string)item["zone"]!);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById((string)item["windowsZone"]!);
        }
    }

    private static byte[] WithByteOrderMark(string text) => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(text)).ToArray();

    [Fact]
    public void HistoryFormatsMatchSharedCases()
    {
        var history = Cases()["history"]!;
        var entries = Entries(history["entries"]!.AsArray());
        var csv = (string)history["csv"]!;
        var json = (string)history["json"]!;
        var markdown = (string)history["markdown"]!;
        Assert.Equal(csv, HistoryExport.ToCsv(entries));
        Assert.Equal(json, HistoryExport.ToJson(entries));
        Assert.Equal(markdown, HistoryExport.ToMarkdown(entries));
        // The CSV file starts with a byte-order mark, so Excel reads it as UTF-8; the others don't.
        Assert.Equal(WithByteOrderMark(csv), HistoryExport.Data(entries, HistoryExport.Format.Csv));
        Assert.Equal(Utf8(json), HistoryExport.Data(entries, HistoryExport.Format.Json));
        Assert.Equal(Utf8(markdown), HistoryExport.Data(entries, HistoryExport.Format.Markdown));
    }

    [Fact]
    public void ValuesMatchSharedCases()
    {
        var values = Cases()["values"]!;
        foreach (var item in values["timestamps"]!.AsArray())
        {
            Assert.Equal((string)item!["expected"]!, HistoryExport.Timestamp(Date(item["unix"]), (int)item["offsetMinutes"]!));
        }
        foreach (var item in values["seconds"]!.AsArray())
        {
            Assert.Equal((string)item!["expected"]!, HistoryExport.Seconds((double)item["value"]!));
        }
        foreach (var item in values["usd"]!.AsArray())
        {
            Assert.Equal((string)item!["expected"]!, HistoryExport.Usd((double)item["value"]!));
        }
        foreach (var item in values["offsets"]!.AsArray())
        {
            Assert.Equal((int)item!["expected"]!, HistoryExport.UtcOffsetMinutes(Date(item["unix"]), Zone(item)));
        }
        foreach (var item in values["markdownLines"]!.AsArray())
        {
            Assert.Equal((string)item!["expected"]!, HistoryExport.MarkdownLine((string)item["line"]!));
        }
        foreach (var item in values["spreadsheetFields"]!.AsArray())
        {
            var text = (string)item!["text"]!;
            Assert.Equal((string)item["expected"]!, Csv.SpreadsheetField(text));
            // And back: what an import reads is what was written.
            Assert.Equal(text, Csv.Unprotected(Csv.Rows(Csv.SpreadsheetField(text) + "\r\n", ',')[0][0]));
        }
    }

    /// <summary>A date at either end of what Windows can store is written within bounds rather than throwing.</summary>
    [Fact]
    public void ExtremeValuesDontThrow()
    {
        Assert.Equal("9999-12-31T23:59:59+08:00", HistoryExport.Timestamp(DateTimeOffset.MaxValue, 480));
        Assert.Equal("0001-01-01T00:00:00-05:00", HistoryExport.Timestamp(DateTimeOffset.MinValue, -300));
        Assert.Equal("1970-01-01T14:00:00+14:00", HistoryExport.Timestamp(DateTimeOffset.UnixEpoch, 100_000));
        Assert.Equal("1000000000000.0", HistoryExport.Seconds(double.MaxValue));
        Assert.Equal("0.000000", HistoryExport.Usd(double.NegativeInfinity));
    }

    [Fact]
    public void EmptyExports()
    {
        Assert.Equal("date,durationSeconds,status,finalText,rawText,costUSD,words\r\n", HistoryExport.ToCsv([]));
        Assert.Equal("[]\n", HistoryExport.ToJson([]));
        Assert.Equal("", HistoryExport.ToMarkdown([]));
        Assert.Equal("0.0", HistoryExport.Seconds(double.NaN));
        Assert.Equal("0.000000", HistoryExport.Usd(double.PositiveInfinity));
    }

    [Fact]
    public void ThousandsOfDictationsExportQuickly()
    {
        var entry = new HistoryExport.Entry(DateTimeOffset.FromUnixTimeSeconds(1_791_786_725), 480, 42, "done",
                                            string.Concat(Enumerable.Repeat("明天下午三点开会，记得带上报告。", 20)),
                                            string.Concat(Enumerable.Repeat("嗯明天下午三点开会 记得带上那个报告", 20)), 0.0005);
        var entries = Enumerable.Repeat(entry, 5000).ToList();
        var watch = Stopwatch.StartNew();
        foreach (var format in Enum.GetValues<HistoryExport.Format>()) HistoryExport.Data(entries, format);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void VocabularyFilesMatchSharedCases()
    {
        var vocabulary = Cases()["vocabulary"]!;
        var terms = vocabulary["terms"]!.AsArray()
            .Select(item => new VocabularyTransfer.Term((string)item!["term"]!, item["heardAs"]!.AsArray().Select(h => (string)h!).ToList()))
            .ToList();
        var csv = (string)vocabulary["csv"]!;
        var text = (string)vocabulary["text"]!;
        Assert.Equal(csv, VocabularyTransfer.ToCsv(terms));
        Assert.Equal(text, VocabularyTransfer.ToText(terms));
        Assert.Equal(WithByteOrderMark(csv), VocabularyTransfer.Data(terms, csv: true));
        Assert.Equal(Utf8(text), VocabularyTransfer.Data(terms, csv: false));
    }

    [Fact]
    public void VocabularyImportsMatchSharedCases()
    {
        var vocabulary = Cases()["vocabulary"]!;
        foreach (var item in vocabulary["imports"]!.AsArray())
        {
            // A file's bytes (Excel's UTF-16 "Unicode Text", say), or its text.
            var input = item!["base64"] is { } base64 ? VocabularyTransfer.Decode(Convert.FromBase64String((string)base64!))! : (string)item["input"]!;
            Assert.Equal(Strings(item["terms"]), VocabularyTransfer.TermsFromFile(input, (string)item["kind"]!));
        }
        Assert.Equal((int)vocabulary["maxTermLength"]!, VocabularyTransfer.MaxTermLength);
        Assert.Equal((int)vocabulary["maxTerms"]!, VocabularyTransfer.MaxTerms);
        foreach (var item in vocabulary["plans"]!.AsArray())
        {
            var limit = item!["limit"] is { } given ? (int)given : VocabularyTransfer.MaxTerms;
            var plan = VocabularyTransfer.Plan(Strings(item["imported"]), Strings(item["existing"]), limit);
            Assert.Equal(Strings(item["added"]), plan.Added);
            Assert.Equal((int)item["known"]!, plan.Known);
            Assert.Equal((int)item["tooLong"]!, plan.TooLong);
            Assert.Equal((int)item["overLimit"]!, plan.OverLimit);
        }
        foreach (var item in vocabulary["merged"]!.AsArray())
        {
            Assert.Equal((string)item!["expected"]!, VocabularyTransfer.Merged((string)item["vocabulary"]!, Strings(item["adding"])));
        }
        foreach (var item in vocabulary["decode"]!.AsArray())
        {
            Assert.Equal((string?)item!["text"], VocabularyTransfer.Decode(Convert.FromBase64String((string)item["base64"]!)));
        }
    }

    [Fact]
    public void ExportedVocabularyImportsBack()
    {
        List<VocabularyTransfer.Term> terms = [new("Typeless", ["TypeList"]), new("李明", []), new("-ish", ["=ish"])];
        var csv = VocabularyTransfer.Decode(VocabularyTransfer.Data(terms, csv: true))!;
        Assert.Equal(["Typeless", "李明", "-ish"], VocabularyTransfer.TermsFromFile(csv, "csv"));
        var text = VocabularyTransfer.Decode(VocabularyTransfer.Data(terms, csv: false))!;
        Assert.Equal(["Typeless", "李明", "-ish"], VocabularyTransfer.TermsFromFile(text, "txt"));
    }

    /// <summary>However big the file, an import never takes the vocabulary past its limit, so clean-up requests stay small.</summary>
    [Fact]
    public void ImportStopsAtTheLimit()
    {
        var existing = Enumerable.Range(1, 990).Select(n => $"term{n}").ToList();
        var imported = Enumerable.Range(1, 5000).Select(n => $"new{n}").ToList();
        var plan = VocabularyTransfer.Plan(imported, existing);
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"new{n}").ToList(), plan.Added);
        Assert.Equal(4990, plan.OverLimit);
        Assert.Equal(1, VocabularyTransfer.Plan(["one more"], Enumerable.Range(1, 1200).Select(n => $"t{n}").ToList()).OverLimit);
    }

    /// <summary>A save that fails part way leaves neither half a file nor its temporary file behind.</summary>
    [Fact]
    public void FailedSavesLeaveNoTemporaryFile()
    {
        var folder = Directory.CreateTempSubdirectory("opentypeless-export-").FullName;
        try
        {
            // A folder where the file should go: the final move fails after the temporary file was written.
            var taken = Path.Combine(folder, "taken.csv");
            Directory.CreateDirectory(taken);
            Assert.ThrowsAny<Exception>(() => SafeFile.WriteAllBytes(taken, [1, 2, 3]));
            var source = Path.Combine(folder, "audio.wav");
            File.WriteAllBytes(source, [4, 5, 6]);
            Assert.ThrowsAny<Exception>(() => SafeFile.Copy(source, taken));
            Assert.ThrowsAny<Exception>(() => SafeFile.Copy(Path.Combine(folder, "missing.wav"), Path.Combine(folder, "copy.wav")));
            Assert.Equal(["audio.wav", "taken.csv"], Directory.EnumerateFileSystemEntries(folder).Select(path => Path.GetFileName(path)).Order().ToList());

            // And one that works replaces what was there, again with nothing left over.
            var saved = Path.Combine(folder, "saved.csv");
            File.WriteAllBytes(saved, [9, 9, 9, 9]);
            SafeFile.WriteAllBytes(saved, [1, 2]);
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(saved));
            var copy = Path.Combine(folder, "copy.wav");
            SafeFile.Copy(source, copy);
            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(copy));
            Assert.Equal(["audio.wav", "copy.wav", "saved.csv", "taken.csv"],
                         Directory.EnumerateFileSystemEntries(folder).Select(path => Path.GetFileName(path)).Order().ToList());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CsvRowsAreParsedLikeRfc4180()
    {
        Assert.Equal([["a", "b,c", "d\"e"], ["f", "g\nh"]], Csv.Rows("a,\"b,c\",\"d\"\"e\"\r\nf,\"g\nh\"\n"));
        Assert.Equal([["a", "b", "c"], ["1", "2", "3"]], Csv.Rows("a;b;c\n1;2;3"));
        Assert.Equal([["a", "b"], ["1", "2"], ["3"]], Csv.Rows("a\tb\n1\t2\r3"));
        Assert.Equal([["open, never closed\nstill open"]], Csv.Rows("\"open, never closed\nstill open"));
        Assert.Empty(Csv.Rows(""));
        Assert.Equal("plain", Csv.Field("plain"));
        Assert.Equal("\"a,b\"", Csv.Field("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Csv.Field("say \"hi\""));
        Assert.Equal("\"two\nlines\"", Csv.Field("two\nlines"));
    }
}
