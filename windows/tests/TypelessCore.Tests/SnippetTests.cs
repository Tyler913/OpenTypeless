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
        Assert.True(cases.Count >= 30);
        foreach (var node in cases)
        {
            var snippets = node!["snippets"] is { } own ? Snippets(own) : shared;
            var expected = (string)node["expected"]!;
            var actual = SnippetExpander.Expand((string)node["text"]!, snippets);
            Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"{node["id"]}: expected “{expected}”, got “{actual}”");
        }
    }

    /// <summary>The fixture's saved list: unreadable entries are left out, missing, blank and repeated IDs replaced.</summary>
    [Fact]
    public void StoredListIsReadEntryByEntry()
    {
        var stored = Fixture()["stored"]!.AsObject();
        var (snippets, repaired) = Snippet.ReadList(stored["json"]);
        Assert.Equal(stored["triggers"]!.AsArray().Select(n => (string)n!).ToArray(), snippets.Select(s => s.Trigger).ToArray());
        Assert.Equal((bool)stored["repaired"]!, repaired);
        Assert.Equal("a1", snippets[0].Id);
        Assert.Equal(snippets.Count, snippets.Select(s => s.Id).Distinct().Count());
        Assert.All(snippets, s => Assert.False(string.IsNullOrWhiteSpace(s.Id)));
        var (clean, cleanRepaired) = Snippet.ReadList(stored["clean"]);
        Assert.Equal(new[] { "a1" }, clean.Select(s => s.Id).ToArray());
        Assert.False(cleanRepaired);
        Assert.Empty(Snippet.ReadList(new JsonObject()).Snippets);
    }

    [Fact]
    public void OneClassifierForEveryBlock()
    {
        Assert.True(SnippetExpander.IsHanOrKana(0x20BB7)); // 𠮷
        Assert.True(SnippetExpander.IsHanOrKana(0x30000)); // Extension G
        Assert.True(SnippetExpander.IsHangul(0x1100) && SnippetExpander.IsHangul(0xD7B0));
        Assert.False(SnippetExpander.IsLatinWord(0x20BB7) || SnippetExpander.IsLatinWord(0x1161));
        Assert.True(SnippetExpander.IsLatinWord(0x65) && SnippetExpander.IsLatinWord(0xE9) && SnippetExpander.IsLatinWord(0x33));
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
        // Composed and decomposed are the same trigger.
        Assert.Equal(SnippetExpander.Key("café"), SnippetExpander.Key("café"));
        Assert.Equal(SnippetExpander.Key("한"), SnippetExpander.Key("한"));
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

    /// <summary>How long this takes is a performance budget (PerformanceTests.SnippetsInALongDictation).</summary>
    [Fact]
    public void LongTextExpandsEveryTrigger()
    {
        var snippets = Enumerable.Range(0, 50).Select(i => new Snippet { Trigger = $"trigger number {i}", Expansion = $"expansion {i}" }).ToList();
        var text = string.Concat(Enumerable.Repeat("我们今天讨论了缓存和数据库的性能，then trigger number 7 again. ", 400));
        Assert.Equal(401, SnippetExpander.Expand(text, snippets).Split("expansion 7").Length);
    }
}
