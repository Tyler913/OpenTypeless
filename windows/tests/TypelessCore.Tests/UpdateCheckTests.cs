namespace TypelessCore.Tests;

public class UpdateCheckTests
{
    /// <summary>Shaped like GitHub's <c>GET /repos/{owner}/{repo}/releases</c>, newest first.</summary>
    private const string ReleasesJson = """
    [
      {"tag_name": "1.1.0-beta", "name": "Beta", "body": "", "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.1.0-beta",
       "draft": false, "prerelease": true,
       "assets": [{"name": "OpenTypeless-1.1.0-windows-x64.zip", "size": 10, "digest": null,
                   "browser_download_url": "https://example.com/beta-win.zip"}]},
      {"tag_name": "1.0.3-windows", "name": "", "body": null, "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.3-windows",
       "draft": false, "prerelease": false,
       "assets": [{"name": "OpenTypeless-1.0.3-windows-x64.zip", "size": 65000000,
                   "digest": "sha256:D8768D63FCD7E2F2106F7B6787CA6096285D9161F44F0B00BA96D5DEF45B5735",
                   "browser_download_url": "https://example.com/1.0.3-win-x64.zip"},
                  {"name": "OpenTypeless-1.0.3-windows-arm64.zip", "size": 64000000,
                   "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
                   "browser_download_url": "https://example.com/1.0.3-win-arm64.zip"}]},
      {"tag_name": "1.0.4", "name": "OpenTypeless 1.0.4", "body": "  Faster clean-up.\n", "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.4",
       "draft": false, "prerelease": false,
       "assets": [{"name": "OpenTypeless-1.0.4-macOS-arm64.zip", "size": 1810073,
                   "digest": "sha256:78f69c8dae4fdc7f1cde3ce8a31b04e293416a6972914a96d60978399590c22b",
                   "browser_download_url": "https://example.com/1.0.4-mac.zip"}]},
      {"tag_name": "1.0.0", "name": "OpenTypeless V1.0.0", "body": "First release", "html_url": "https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.0",
       "draft": false, "prerelease": false,
       "assets": [{"name": "OpenTypeless-1.0.0-macOS-arm64.zip", "size": 1810073, "digest": "sha256:78f69c8dae4fdc7f1cde3ce8a31b04e293416a6972914a96d60978399590c22b",
                   "browser_download_url": "https://example.com/1.0.0-mac.zip"},
                  {"name": "OpenTypeless-1.0.0-windows-x64.zip", "size": 65148938, "digest": "sha256:d8768d63fcd7e2f2106f7b6787ca6096285d9161f44f0b00ba96d5def45b5735",
                   "browser_download_url": "https://example.com/1.0.0-win-x64.zip"}]}
    ]
    """;

    private static List<GitHubRelease> Releases() => UpdateCheck.Decode(ReleasesJson);
    private static AppVersion V(string text) => AppVersion.Parse(text)!;

    [Fact]
    public void VersionParsingAndOrder()
    {
        Assert.Equal([1, 0, 2], AppVersion.Parse("1.0.2")!.Components);
        Assert.Equal([2, 1], AppVersion.Parse("v2.1")!.Components);
        Assert.Equal([1, 0, 2], AppVersion.Parse("1.0.2-windows")!.Components);
        Assert.Null(AppVersion.Parse(""));
        Assert.Null(AppVersion.Parse("1..2"));
        Assert.Null(AppVersion.Parse("1.x"));
        Assert.Null(AppVersion.Parse("+1.0"));
        Assert.True(V("1.0.10") > V("1.0.9"));
        Assert.True(V("1.1") > V("1.0.99"));
        Assert.Equal(V("1.0"), V("1.0.0"));
        Assert.Single(new HashSet<AppVersion> { V("1.0"), V("1.0.0") });
        Assert.Equal("1.2.3", V("01.2.3").ToString());
        Assert.Equal(V("1.0.1"), AppVersion.FromSystemVersion(new Version(1, 0, 1, 0)));
        Assert.Equal(V("1.0"), AppVersion.FromSystemVersion(new Version(1, 0)));
    }

    [Fact]
    public void AssetVersionMatchesOnlyThisPlatform()
    {
        Assert.Equal(V("1.0.2"), UpdateCheck.AssetVersion("OpenTypeless-1.0.2-windows-x64.zip", "windows-x64"));
        Assert.Null(UpdateCheck.AssetVersion("OpenTypeless-1.0.2-windows-arm64.zip", "windows-x64"));
        Assert.Null(UpdateCheck.AssetVersion("OpenTypeless-1.0.2-macOS-arm64.zip", "windows-arm64"));
        Assert.Null(UpdateCheck.AssetVersion("OpenTypeless--windows-x64.zip", "windows-x64"));
        Assert.Null(UpdateCheck.AssetVersion("Other-1.0.2-windows-x64.zip", "windows-x64"));
        // The installer sits next to the zip in a release; updates always come from the zip.
        Assert.Null(UpdateCheck.AssetVersion("OpenTypeless-1.0.2-windows-x64-setup.exe", "windows-x64"));
        Assert.Equal("windows-arm64", UpdateCheck.WindowsPlatform(arm64: true));
    }

    [Fact]
    public void PicksNewestStableReleaseForThisArchitecture()
    {
        var x64 = UpdateCheck.Newest(Releases(), "windows-x64", V("1.0.1"));
        Assert.NotNull(x64);
        Assert.Equal(V("1.0.3"), x64.Version);   // 1.1.0 is a prerelease, 1.0.4 is macOS-only
        Assert.Equal("OpenTypeless 1.0.3", x64.Title);   // empty release name falls back
        Assert.Equal("", x64.Notes);
        Assert.Equal("OpenTypeless-1.0.3-windows-x64.zip", x64.AssetName);
        Assert.Equal("https://example.com/1.0.3-win-x64.zip", x64.DownloadUrl.AbsoluteUri);
        Assert.Equal(65000000, x64.Size);
        Assert.Equal("d8768d63fcd7e2f2106f7b6787ca6096285d9161f44f0b00ba96d5def45b5735", x64.Sha256);

        var arm = UpdateCheck.Newest(Releases(), "windows-arm64", V("1.0.1"));
        Assert.Equal("https://example.com/1.0.3-win-arm64.zip", arm!.DownloadUrl.AbsoluteUri);

        var mac = UpdateCheck.Newest(Releases(), "macOS-arm64", V("1.0.1"));
        Assert.Equal(V("1.0.4"), mac!.Version);
        Assert.Equal("Faster clean-up.", mac.Notes);
        Assert.Equal("https://github.com/Tyler913/OpenTypeless/releases/tag/1.0.4", mac.PageUrl.AbsoluteUri);
    }

    [Fact]
    public void NothingWhenUpToDateOrAhead()
    {
        Assert.Null(UpdateCheck.Newest(Releases(), "windows-x64", V("1.0.3")));
        Assert.Null(UpdateCheck.Newest(Releases(), "windows-x64", V("2.0")));
        Assert.Null(UpdateCheck.Newest([], "windows-x64", V("1.0")));
    }

    [Fact]
    public void DigestParsing()
    {
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 32)), UpdateCheck.Sha256FromDigest("sha256:" + string.Concat(Enumerable.Repeat("Ab", 32))));
        Assert.Null(UpdateCheck.Sha256FromDigest(null));
        Assert.Null(UpdateCheck.Sha256FromDigest("sha512:abcd"));
        Assert.Null(UpdateCheck.Sha256FromDigest("sha256:abcd"));
        Assert.Null(UpdateCheck.Sha256FromDigest("sha256:" + string.Concat(Enumerable.Repeat("zz", 32))));
    }
}
