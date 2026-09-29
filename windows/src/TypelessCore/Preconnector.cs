using System.Net.Http;

namespace TypelessCore;

public sealed partial class ApiClient
{
    /// <summary>
    /// Opens a connection to the provider ahead of the first real request, so that request doesn't wait for DNS, TCP and
    /// TLS. A HEAD request for the base URL, with no key: any answer, even an error status, leaves a warm connection in the
    /// shared pool. Never throws.
    /// </summary>
    public async Task Preconnect(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Endpoint.BaseUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Only a head start: the real request connects by itself.
        }
    }
}

/// <summary>
/// Warms the connections a dictation is about to use: called when the key goes down (and every so often while
/// recording), so the first speech-to-text and clean-up requests don't pay for setting up a connection. Each host is
/// warmed once however many routes use it, and not again within <c>minimumInterval</c> seconds.
/// </summary>
public sealed class Preconnector
{
    private readonly double _minimumInterval;
    private readonly Func<double> _clock;
    private readonly Dictionary<string, double> _warmedAt = new();
    private readonly object _lock = new();

    public Preconnector(double minimumInterval = 20, Func<double>? clock = null)
    {
        _minimumInterval = minimumInterval;
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
    }

    /// <summary>Starts a <see cref="ApiClient.Preconnect"/> for each host not warmed recently; returns them (for tests).</summary>
    public List<Task> Warm(IEnumerable<ApiClient?> clients)
    {
        var started = new List<Task>();
        var now = _clock();
        foreach (var client in clients)
        {
            if (client == null) continue;
            var host = client.Endpoint.BaseUrl.GetLeftPart(UriPartial.Authority);
            lock (_lock)
            {
                if (_warmedAt.TryGetValue(host, out var at) && now - at < _minimumInterval) continue;
                _warmedAt[host] = now;
            }
            started.Add(client.Preconnect());
        }
        return started;
    }
}
