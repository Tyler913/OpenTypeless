namespace TypelessCore;

/// <summary>
/// The model on this PC standing in for the speech-to-text provider while the network is down, so a dictation still ends
/// in text. Only with the model already downloaded (nothing can be downloaded then), and never for the provider's own
/// refusals (a bad key, a rate limit, a server error), which keep the retries and failures they always had. The cases are
/// in <c>testdata/offline-fallback-cases.json</c>, read by both test suites.
/// </summary>
public static class OfflineFallback
{
    /// <summary>The setting, on by default; the same key as on macOS.</summary>
    public const string SettingsKey = "onDeviceWhenOffline";

    /// <summary>
    /// Whether a chunk goes to the model on this PC without trying the provider: once the dictation has switched to it
    /// (<paramref name="switched"/>), and while the system says there's no network at all, so no request waits out its
    /// timeout. <paramref name="ready"/>: the model can do it without the network.
    /// </summary>
    public static bool SkipsProvider(bool switched, bool networkDown, bool ready) => ready && (switched || networkDown);

    /// <summary>
    /// Whether the model on this PC takes over a chunk the provider failed with <paramref name="error"/>, switching the rest
    /// of the dictation to it: only for a failure the network is to blame for, never a timeout or the provider's own
    /// answer. While the attempt has retries left, only when trying again can't help: the system has no connection, or
    /// says the network is down. A DNS hiccup or a dropped connection gets its usual retries first: a server that turns a
    /// request down mid-upload drops the connection too.
    /// </summary>
    public static bool TakesOver(ApiException error, bool retriesLeft, bool networkDown, bool ready)
    {
        if (!ready || error.Offline is not { } failure) return false;
        return !retriesLeft || failure == NetworkFailure.NoConnection || networkDown;
    }

    /// <summary>
    /// Whether <paramref name="host"/> is this PC ("localhost", 127.0.0.1, ::1): a speech-to-text server there works with
    /// no network, so the system saying the network is down tells nothing about it.
    /// </summary>
    public static bool IsLocalHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.EndsWith('.')) host = host[..^1];
        if (host is "localhost" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        // 127.0.0.0/8, written as four decimal numbers.
        var parts = host.Split('.');
        return parts.Length == 4 && parts[0] == "127"
            && parts.All(part => part.Length is >= 1 and <= 3 && part.All(c => c is >= '0' and <= '9') && int.Parse(part) <= 255);
    }

    /// <summary>
    /// What a retry reuses of a dictation's transcripts (by chunk index), and which of those were recognised on this PC
    /// for want of a network. With the provider (<paramref name="usesProvider"/>) those are sent to it again, so the retry
    /// gets the provider's text; on-device, everything is reused as it is.
    /// </summary>
    public static (Dictionary<int, string> Texts, List<int>? OnDevice) Reused(IReadOnlyDictionary<int, string> texts,
                                                                              IReadOnlyCollection<int>? onDevice, bool usesProvider)
    {
        var recognised = (onDevice ?? Array.Empty<int>()).Where(texts.ContainsKey).ToHashSet();
        if (recognised.Count == 0) return (new Dictionary<int, string>(texts), null);
        if (!usesProvider) return (new Dictionary<int, string>(texts), recognised.Order().ToList());
        return (texts.Where(t => !recognised.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value), null);
    }
}

/// <summary>The model on this PC as a provider's stand-in in a <see cref="TranscriptionPipeline"/> (see <see cref="OfflineFallback"/>).</summary>
/// <param name="Transcriber">The model (<see cref="OnDeviceTranscriber"/>).</param>
/// <param name="Options">What it's asked with (<see cref="OnDeviceTranscriber.Options"/>: the dictation's language).</param>
/// <param name="IsReady">Whether it can recognise the dictation's language without the network: the model is downloaded.</param>
/// <param name="NetworkDown">Whether the system says there's no network now.</param>
public sealed record OfflineFallbackRoute(ISpeechTranscriber Transcriber, TranscriptionOptions Options, Func<bool> IsReady,
                                          Func<bool> NetworkDown);
