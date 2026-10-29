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

    /// <summary>What a dictation uses: off, or without a command, the transcript exactly as it came (its own line breaks included).</summary>
    [Fact]
    public void SharedLayouts()
    {
        foreach (var node in CaseFile()["layouts"]!.AsArray())
        {
            var expected = new SpokenLayout.LaidOut((string)node!["leading"]!, (string)node["body"]!, (string)node["trailing"]!,
                                                    (bool)node["dictatedBreaks"]!);
            var actual = SpokenLayout.Layout((string)node["input"]!, (bool)node["enabled"]!);
            Assert.True(expected == actual, $"{node["id"]}: got {actual}");
        }
        var laidOut = SpokenLayout.Layout("New line. Hello. New paragraph.", enabled: true);
        Assert.Equal("\nHello.\n\n", laidOut.Text);
        Assert.Equal("\nHello!\n\n", laidOut.Around("Hello!"));
    }

    /// <summary>The known false positives (a command word said as its own clause in a list, or as an answer) stay pinned.</summary>
    [Fact]
    public void LimitationsArePinned() =>
        Assert.True(CaseFile()["cases"]!.AsArray().Count(node => node!["limitation"] != null) >= 7);

    /// <summary>
    /// The clean-up's prompt is the one ordinary dictations always had unless the transcript's breaks were dictated, and
    /// then only for a transcript (or part) that has any.
    /// </summary>
    [Fact]
    public void OrdinaryDictationsKeepTheirPrompt()
    {
        var plain = Prompts.PolishSystemPrompt([], "");
        Assert.Equal(Prompts.BasePolishPrompt, plain);
        Assert.DoesNotContain("Line breaks", plain);
        Assert.Equal(Prompts.BasePolishPrompt + "\n\n" + Prompts.LineBreaksSection, Prompts.PolishSystemPrompt([], "", dictatedLineBreaks: true));
        var dictated = new PolishOptions("m", DictatedLineBreaks: true);
        Assert.Contains("## Line breaks", ApiClient.PolishMessages("Hi,\nthere.", dictated)[0]);
        Assert.DoesNotContain("## Line breaks", ApiClient.PolishMessages("Hi there.", dictated)[0]);
        // Speech recognition's own breaks: nothing said about them, as before.
        Assert.DoesNotContain("## Line breaks", ApiClient.PolishMessages("Hi,\nthere.", new PolishOptions("m"))[0]);
    }

    /// <summary>The section's example is a clean-up, and keeps its lines.</summary>
    [Fact]
    public void TheLineBreaksExampleIsACleanUp()
    {
        var examples = PolishGuardTests.Examples(Prompts.LineBreaksSection);
        Assert.Single(examples);
        foreach (var (input, output) in examples)
        {
            Assert.True(input.Contains("\n\n") && output.Contains("\n\n"));
            Assert.Null(Prompts.Fallback(input, output, truncated: false));
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
