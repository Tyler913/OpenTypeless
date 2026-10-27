using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>Spoken "new line" / "new paragraph": a command said on its own becomes line breaks; the same words within a sentence stay.</summary>
public class SpokenLayoutTests
{
    /// <summary>testdata/spoken-layout-cases.json, shared with the macOS tests so both apps lay a dictation out the same.</summary>
    private static JsonNode CaseFile([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "spoken-layout-cases.json")))!;

    [Fact]
    public void SharedCases()
    {
        var cases = CaseFile()["cases"]!.AsArray();
        Assert.True(cases.Count > 30);
        foreach (var node in cases)
        {
            var input = (string)node!["input"]!;
            var expected = (string?)node["output"] ?? input;
            var actual = SpokenLayout.Apply(input);
            Assert.True(expected == actual, $"{node["id"]}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void SharedEdges()
    {
        foreach (var node in CaseFile()["edges"]!.AsArray())
        {
            var (leading, body, trailing) = SpokenLayout.Edges((string)node!["input"]!);
            var id = (string)node["id"]!;
            Assert.True((string)node["leading"]! == leading, id);
            Assert.True((string)node["body"]! == body, id);
            Assert.True((string)node["trailing"]! == trailing, id);
        }
    }

    /// <summary>Put back together, the edges are the laid-out text again, trimmed only where there were breaks.</summary>
    [Fact]
    public void EdgesPutBackTogether()
    {
        foreach (var text in new[] { "\n\nSecond point.\n", "Hello.", "A.\nB.", "\n" })
        {
            var (leading, body, trailing) = SpokenLayout.Edges(text);
            Assert.Equal(text, leading + body + trailing);
        }
    }

    /// <summary>Every command of the list is recognised said on its own, and none is recognised inside a longer clause.</summary>
    [Fact]
    public void EveryCommandOnItsOwnAndNotWithinASentence()
    {
        foreach (var (key, breaks) in SpokenLayout.Commands)
        {
            Assert.Equal($"A.{breaks}B.", SpokenLayout.Apply($"A. {key}. B."));
            Assert.Equal($"A {key} B.", SpokenLayout.Apply($"A {key} B."));
        }
    }

    /// <summary>Every language the app speaks has both commands.</summary>
    [Fact]
    public void BothCommandsInEveryLanguage()
    {
        Assert.Equal(["\n", "\n\n"], SpokenLayout.Commands.Values.Distinct().Order());
        Assert.Equal(33, SpokenLayout.Commands.Count); // as on macOS
    }

    /// <summary>A Windows line ending counts as one break, as on macOS.</summary>
    [Fact]
    public void CrLfIsOneBreak() => Assert.Equal(("\n", "Hi.", ""), SpokenLayout.Edges("\r\nHi."));
}
