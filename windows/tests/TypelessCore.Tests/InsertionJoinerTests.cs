using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>Fitting dictated text to the text around the cursor.</summary>
public class InsertionJoinerTests
{
    /// <summary>testdata/insertion-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "insertion-cases.json")))!.AsObject();

    [Fact]
    public void FitsTheSharedCases()
    {
        var cases = Cases();
        var common = cases["commonWords"]!.AsArray().Select(w => (string)w!).ToHashSet();
        var vocabulary = cases["vocabulary"]!.AsArray().Select(w => (string)w!).ToList();
        foreach (var node in cases["cases"]!.AsArray())
        {
            var result = InsertionJoiner.Fit((string)node!["text"]!, (string?)node["before"], (string?)node["after"], vocabulary,
                                             word => common.Contains(word.ToLowerInvariant()));
            var id = (string)node["id"]!;
            Assert.True((string)node["expect"]! == result.Text, $"{id}: got \"{result.Text}\"");
            var changes = node["changes"]!.AsArray().Select(c => (string)c!).ToList();
            Assert.True(changes.SequenceEqual(result.Changes), $"{id}: got [{string.Join(", ", result.Changes)}]");
        }
    }

    [Fact]
    public void ChangesNothingWhenNothingIsKnown()
    {
        var result = InsertionJoiner.Fit("Next sentence.", null, "and more", [], _ => true);
        Assert.Equal("Next sentence.", result.Text);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void DictionaryDecidesLowercasing()
    {
        // Without the dictionary vouching for the word, a capital mid-sentence stays: it may be a name.
        Assert.Equal(" Then we left.", InsertionJoiner.Fit("Then we left.", "and", "", [], _ => false).Text);
        Assert.Equal(" then we left.", InsertionJoiner.Fit("Then we left.", "and", "", [], _ => true).Text);
    }

    [Fact]
    public void FinalStopIsOnlyDroppedAfterAWord()
    {
        Assert.Equal("we left", InsertionJoiner.DroppingFinalStop("we left."));
        Assert.Equal("我们走了", InsertionJoiner.DroppingFinalStop("我们走了。"));
        Assert.Null(InsertionJoiner.DroppingFinalStop("wait..."));
        Assert.Null(InsertionJoiner.DroppingFinalStop("in the U.S."));
        Assert.Null(InsertionJoiner.DroppingFinalStop("signed J."));
        Assert.Null(InsertionJoiner.DroppingFinalStop("(see above)."));
        Assert.Null(InsertionJoiner.DroppingFinalStop("."));
    }
}
