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
            var input = (string)item!["input"]!;
            var terms = (string)item["kind"]! == "csv" ? VocabularyTransfer.TermsFromCsv(input) : VocabularyTransfer.TermsFromText(input);
            Assert.Equal(item["terms"]!.AsArray().Select(t => (string)t!).ToList(), terms);
        }
        foreach (var item in vocabulary["merges"]!.AsArray())
        {
            var added = VocabularyTransfer.NewTerms(item!["imported"]!.AsArray().Select(t => (string)t!),
                                                    item["existing"]!.AsArray().Select(t => (string)t!));
            Assert.Equal(item["added"]!.AsArray().Select(t => (string)t!).ToList(), added);
        }
        foreach (var item in vocabulary["decode"]!.AsArray())
        {
            Assert.Equal((string?)item!["text"], VocabularyTransfer.Decode(Convert.FromBase64String((string)item["base64"]!)));
        }
    }

    [Fact]
    public void ExportedVocabularyImportsBack()
    {
        List<VocabularyTransfer.Term> terms = [new("Typeless", ["TypeList"]), new("李明", [])];
        var csv = VocabularyTransfer.Decode(VocabularyTransfer.Data(terms, csv: true))!;
        Assert.Equal(["Typeless", "李明"], VocabularyTransfer.TermsFromCsv(csv));
        var text = VocabularyTransfer.Decode(VocabularyTransfer.Data(terms, csv: false))!;
        Assert.Equal(["Typeless", "李明"], VocabularyTransfer.TermsFromText(text));
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
