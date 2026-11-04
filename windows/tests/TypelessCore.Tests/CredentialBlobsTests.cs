using System.Text;
using System.Text.Json;

namespace TypelessCore.Tests;

public class CredentialBlobsTests
{
    /// <summary>A key as long as real ones get (OpenAI project keys run past 160 characters).</summary>
    private static string Key(string provider) => "sk-" + provider + "-" + new string('x', 180);

    [Fact]
    public void AFewKeysStayInOneEntry()
    {
        var keys = new Dictionary<string, string> { ["openrouter"] = "sk-or-v1-abc", ["groq"] = "gsk_def" };
        var blobs = CredentialBlobs.Split(keys)!;
        // The very same bytes the app has always written, so older versions read it as before.
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["openrouter"] = "sk-or-v1-abc", ["groq"] = "gsk_def" }),
                     Assert.Single(blobs));
        Assert.Equal(keys, CredentialBlobs.Merge(blobs));
    }

    [Fact]
    public void EveryProviderSetUpSpreadsOverEntriesThatFit()
    {
        var keys = ProviderIdExtensions.All.ToDictionary(p => p.RawValue(), p => Key(p.RawValue()));
        var blobs = CredentialBlobs.Split(keys)!;
        Assert.True(blobs.Count > 1);
        Assert.All(blobs, b => Assert.InRange(b.Length, 1, CredentialBlobs.MaxBytes));
        Assert.Equal(keys, CredentialBlobs.Merge(blobs));
        // The original providers come first, where a version that reads one entry looks.
        var first = JsonSerializer.Deserialize<Dictionary<string, string>>(blobs[0])!;
        Assert.Contains("openrouter", first.Keys);
        Assert.Contains("openai", first.Keys);
    }

    [Fact]
    public void AKeyTooLongForAnEntryIsRefused()
    {
        Assert.Null(CredentialBlobs.Split(new Dictionary<string, string> { ["openai"] = new string('x', CredentialBlobs.MaxBytes) }));
        Assert.Empty(CredentialBlobs.Split(new Dictionary<string, string>())!);
    }

    [Fact]
    public void ADamagedEntryDoesNotLoseTheOthers()
    {
        var keys = CredentialBlobs.Merge([Encoding.UTF8.GetBytes("""{"openai":"sk-a"}"""), Encoding.UTF8.GetBytes("not json"), [],
                                          Encoding.UTF8.GetBytes("""{"gemini":"AIza-b","openai":"later"}""")]);
        Assert.Equal(new Dictionary<string, string> { ["openai"] = "sk-a", ["gemini"] = "AIza-b" }, keys);
    }
}
