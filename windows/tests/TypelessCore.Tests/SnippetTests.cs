using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

public class SnippetTests
{
    /// <summary>testdata/snippet-cases.json, shared with the macOS tests so both apps insert the same text.</summary>
    private static JsonObject Fixture([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "snippet-cases.json")))!
            .AsObject();

    private static List<Snippet> Snippets(JsonNode list) =>
        list.AsArray().Select(n => new Snippet { Trigger = (string)n!["trigger"]!, Expansion = (string)n["expansion"]! }).ToList();

    [Fact]
    public void SharedCases()
    {
        var file = Fixture();
        var shared = Snippets(file["snippets"]!);
        var cases = file["cases"]!.AsArray();
        Assert.True(cases.Count >= 20);
        foreach (var node in cases)
        {
            var snippets = node!["snippets"] is { } own ? Snippets(own) : shared;
            var expected = (string)node["expected"]!;
            var actual = SnippetExpander.Expand((string)node["text"]!, snippets);
            Assert.True(expected == actual, $"{node["id"]}: expected “{expected}”, got “{actual}”");
        }
    }

    [Fact]
    public void NothingToExpand()
    {
        List<Snippet> snippets = [new() { Trigger = "my email", Expansion = "tyler@example.com" }];
        Assert.Equal("", SnippetExpander.Expand("", snippets));
        Assert.Equal("my email", SnippetExpander.Expand("my email", []));
        // Nothing to find in a trigger of spaces and punctuation.
        Assert.Equal("。", SnippetExpander.Expand("。", [new Snippet { Trigger = " 。", Expansion = "x" }]));
    }

    /// <summary>Expansion is a pure function of the text and the snippets, so a retry inserts what the first run did.</summary>
    [Fact]
    public void SameTextSameResult()
    {
        List<Snippet> snippets = [new() { Trigger = "我的邮箱", Expansion = "tyler@example.com" }, new() { Trigger = "my email", Expansion = "x" }];
        var once = SnippetExpander.Expand("发到我的邮箱，还有 my email。", snippets);
        Assert.Equal("发到 tyler@example.com，还有 x。", once);
        Assert.Equal(once, SnippetExpander.Expand("发到我的邮箱，还有 my email。", snippets));
    }

    [Fact]
    public void KeysTellTriggersApart()
    {
        Assert.Equal(SnippetExpander.Key("my email"), SnippetExpander.Key("My Email."));
        Assert.Equal(SnippetExpander.Key("pr template"), SnippetExpander.Key("ＰＲ　Template"));
        Assert.Equal(SnippetExpander.Key("我的GitHub"), SnippetExpander.Key("我的 GitHub"));
        Assert.NotEqual(SnippetExpander.Key("C"), SnippetExpander.Key("C#"));
        Assert.Equal("", SnippetExpander.Key("  ？ "));
    }

    [Fact]
    public void TriggersGoAheadOfTheVocabulary()
    {
        List<Snippet> snippets =
        [
            new() { Trigger = " PR template ", Expansion = "…" },
            new() { Trigger = "Claude", Expansion = "…" },
            new() { Trigger = "  ", Expansion = "…" },
        ];
        Assert.Equal(new[] { "PR template", "Claude", "GitHub" }, SnippetExpander.Vocabulary(snippets, ["claude", "GitHub", "PR Template"]));
        // Without snippets the vocabulary is exactly what it was.
        Assert.Equal(new[] { "Claude", "claude", " x " }, SnippetExpander.Vocabulary([], ["Claude", "claude", " x "]));
    }

    [Fact]
    public void LongTextStaysFast()
    {
        var snippets = Enumerable.Range(0, 50).Select(i => new Snippet { Trigger = $"trigger number {i}", Expansion = $"expansion {i}" }).ToList();
        var text = string.Concat(Enumerable.Repeat("我们今天讨论了缓存和数据库的性能，then trigger number 7 again. ", 400));
        var watch = Stopwatch.StartNew();
        var expanded = SnippetExpander.Expand(text, snippets);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(401, expanded.Split("expansion 7").Length);
    }
}
