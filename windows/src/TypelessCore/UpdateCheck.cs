using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypelessCore;

/// <summary>A release version like <c>1.0.2</c>, compared number by number (so 1.0.10 is newer than 1.0.9).</summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public IReadOnlyList<int> Components { get; }

    private AppVersion(int[] components) => Components = components;

    /// <summary>Accepts <c>1.0.2</c>, <c>v1.0.2</c> and tags like <c>1.0.2-windows</c>.</summary>
    public static AppVersion? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out numbers[i])) return null;
        }
        return new AppVersion(numbers);
    }

    public static AppVersion FromSystemVersion(Version version) =>
        new([version.Major, version.Minor, Math.Max(version.Build, 0)]);

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < Math.Max(Components.Count, other.Components.Count); i++)
        {
            var x = i < Components.Count ? Components[i] : 0;
            var y = i < other.Components.Count ? other.Components[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode()
    {
        var count = Components.Count;
        while (count > 1 && Components[count - 1] == 0) count--;
        var hash = new HashCode();
        for (var i = 0; i < count; i++) hash.Add(Components[i]);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join('.', Components);

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator ==(AppVersion? a, AppVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(AppVersion? a, AppVersion? b) => !(a == b);
}

/// <summary>The subset of GitHub's release JSON the updater needs.</summary>
public sealed class GitHubRelease
{
    public sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        /// <summary><c>sha256:&lt;hex&gt;</c>, published by GitHub for every uploaded file.</summary>
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }

    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<Asset> Assets { get; set; } = [];
}

/// <summary>A newer release this copy of the app can install.</summary>
/// <param name="PageUrl">The release page, for reading the notes or downloading by hand.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the zip, or null if GitHub didn't publish one (then it can't be installed automatically).</param>
public sealed record AvailableUpdate(
    AppVersion Version, string Title, string Notes, Uri PageUrl, string AssetName, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Finds newer versions among the GitHub releases of this repository.
/// Releases are matched by their zip's file name, <c>OpenTypeless-&lt;version&gt;-&lt;platform&gt;.zip</c>, not by tag, so it
/// works whether a release carries one app (<c>1.0.2</c>, <c>1.0.2-windows</c>) or both. The release workflow already refuses
/// to publish a zip whose version doesn't match its tag.
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "Tyler913/OpenTypeless";
    public static readonly Uri ReleasesUrl = new($"https://api.github.com/repos/{Repository}/releases?per_page=30");
    public static readonly Uri ReleasesPageUrl = new($"https://github.com/{Repository}/releases");

    /// <summary>The file-name suffix of this platform's zip: <c>windows-x64</c> or <c>windows-arm64</c>.</summary>
    public static string WindowsPlatform(bool arm64) => arm64 ? "windows-arm64" : "windows-x64";

    public static List<GitHubRelease> Decode(string json) =>
        JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? [];

    /// <summary>The version in <c>OpenTypeless-&lt;version&gt;-&lt;platform&gt;.zip</c>, or null if the file is for another platform.</summary>
    public static AppVersion? AssetVersion(string name, string platform)
    {
        const string prefix = "OpenTypeless-";
        var suffix = $"-{platform}.zip";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
            || name.Length <= prefix.Length + suffix.Length) return null;
        return AppVersion.Parse(name[prefix.Length..^suffix.Length]);
    }

    /// <summary>The newest published, non-prerelease release that has a zip for <paramref name="platform"/> and is newer than <paramref name="current"/>.</summary>
    public static AvailableUpdate? Newest(IEnumerable<GitHubRelease> releases, string platform, AppVersion current)
    {
        AvailableUpdate? best = null;
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease) continue;
            foreach (var asset in release.Assets)
            {
                if (AssetVersion(asset.Name, platform) is not { } version || !(version > current)) continue;
                if (best != null && !(version > best.Version)) continue;
                if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var download)
                    || !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page)) continue;
                var title = (release.Name ?? "").Trim();
                best = new AvailableUpdate(
                    version,
                    title.Length == 0 ? $"OpenTypeless {version}" : title,
                    (release.Body ?? "").Trim(),
                    page,
                    asset.Name,
                    download,
                    asset.Size,
                    Sha256FromDigest(asset.Digest));
            }
        }
        return best;
    }

    /// <summary><c>sha256:ABC…</c> → <c>abc…</c>; null for other algorithms or malformed values.</summary>
    public static string? Sha256FromDigest(string? digest)
    {
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest["sha256:".Length..].ToLowerInvariant();
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? hex : null;
    }

    /// <summary>Asks GitHub for the release list. No sign-in needed; unauthenticated requests are limited to 60 an hour.</summary>
    public static async Task<List<GitHubRelease>> FetchReleases(HttpClient http, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "OpenTypeless");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw ApiException.TimedOut(30);
        }
        catch (HttpRequestException error)
        {
            throw ApiException.Network(error.Message, error);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw ApiException.Http((int)response.StatusCode, L("GitHub 请求过于频繁，稍后再试", "GitHub rate limit reached, try again later"));
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw ApiException.Http((int)response.StatusCode, body.Length > 200 ? body[..200] : body);
            }
            try
            {
                return Decode(body);
            }
            catch (JsonException error)
            {
                throw ApiException.BadResponse(error.Message);
            }
        }
    }
}
