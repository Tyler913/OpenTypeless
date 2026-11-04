using System.Text.Json;

namespace TypelessCore;

/// <summary>
/// The API keys as Windows Credential Manager stores them: JSON objects of provider → key, each at most
/// <see cref="MaxBytes"/> long, the most a credential holds. One entry holds a handful of keys; with more providers set up
/// they are spread over as many entries as needed (the app's first entry, then numbered ones), so adding a provider never
/// fails because the others already filled the entry.
/// </summary>
public static class CredentialBlobs
{
    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 × 512 bytes.</summary>
    public const int MaxBytes = 2560;

    /// <summary>
    /// The keys split into entries, in the order the app lists the providers (so the first entry, which an older version
    /// reads alone, holds the original providers' keys); null when a single key is too long for an entry by itself.
    /// </summary>
    public static List<byte[]>? Split(IReadOnlyDictionary<string, string> keys, int maxBytes = MaxBytes)
    {
        var order = ProviderIdExtensions.All.Select(p => p.RawValue()).ToList();
        int Rank(string name) { var i = order.IndexOf(name); return i < 0 ? int.MaxValue : i; }
        var blobs = new List<byte[]>();
        var current = new Dictionary<string, string>();
        foreach (var (name, key) in keys.OrderBy(k => Rank(k.Key)).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            current[name] = key;
            if (JsonSerializer.SerializeToUtf8Bytes(current).Length <= maxBytes) continue;
            current.Remove(name);
            if (current.Count > 0) blobs.Add(JsonSerializer.SerializeToUtf8Bytes(current));
            current = new Dictionary<string, string> { [name] = key };
            if (JsonSerializer.SerializeToUtf8Bytes(current).Length > maxBytes) return null;
        }
        if (current.Count > 0) blobs.Add(JsonSerializer.SerializeToUtf8Bytes(current));
        return blobs;
    }

    /// <summary>All the keys of the entries read, in order; an entry that isn't a JSON object of strings is skipped.</summary>
    public static Dictionary<string, string> Merge(IEnumerable<byte[]> blobs)
    {
        var keys = new Dictionary<string, string>();
        foreach (var blob in blobs)
        {
            Dictionary<string, string>? part;
            try
            {
                part = JsonSerializer.Deserialize<Dictionary<string, string>>(blob);
            }
            catch (JsonException)
            {
                continue;
            }
            foreach (var (name, key) in part ?? []) keys.TryAdd(name, key);
        }
        return keys;
    }
}
