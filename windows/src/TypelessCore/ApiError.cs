using System.Net.Http;
using System.Net.Sockets;

namespace TypelessCore;

public enum ApiErrorKind { MissingApiKey, Http, Network, Timeout, BadResponse, Cancelled }

/// <summary>Every failure the pipeline reports. Mirrors the macOS <c>APIError</c> enum case for case.</summary>
public sealed class ApiException : Exception, IEquatable<ApiException>
{
    public ApiErrorKind Kind { get; }
    /// <summary>Provider name (missing key), response message (HTTP), or description (network / bad response).</summary>
    public string Detail { get; }
    public int Status { get; }
    public double Seconds { get; }

    private ApiException(ApiErrorKind kind, string detail = "", int status = 0, double seconds = 0, Exception? inner = null)
        : base(null, inner)
    {
        Kind = kind;
        Detail = detail;
        Status = status;
        Seconds = seconds;
    }

    public static ApiException MissingApiKey(string provider) => new(ApiErrorKind.MissingApiKey, provider);
    public static ApiException Http(int status, string message) => new(ApiErrorKind.Http, message, status);
    public static ApiException Network(string message, Exception? inner = null) => new(ApiErrorKind.Network, message, inner: inner);
    public static ApiException TimedOut(double seconds) => new(ApiErrorKind.Timeout, seconds: seconds);
    public static ApiException BadResponse(string message) => new(ApiErrorKind.BadResponse, message);
    public static ApiException Cancelled() => new(ApiErrorKind.Cancelled);

    /// <summary>Localised at the moment it's read, so a language switch applies to old errors too.</summary>
    public override string Message => Kind switch
    {
        ApiErrorKind.MissingApiKey => L($"还没有填写 {Detail} 的 API Key", $"No API key set for {Detail}"),
        ApiErrorKind.Http => Status switch
        {
            401 => L("API Key 无效 (401)：", "Invalid API key (401): ") + Detail,
            402 => L("余额不足 (402)：", "Out of credits (402): ") + Detail,
            429 => L("请求过于频繁 (429)：", "Rate limited (429): ") + Detail,
            _ => $"HTTP {Status}: {Detail}",
        },
        ApiErrorKind.Network => L("网络错误：", "Network error: ") + Detail,
        ApiErrorKind.Timeout => L($"请求超时（{(int)Seconds} 秒）", $"Timed out after {(int)Seconds}s"),
        ApiErrorKind.BadResponse => L("返回内容无法解析：", "Unexpected response: ") + Detail,
        _ => L("已取消", "Cancelled"),
    };

    /// <summary>Transient failures worth retrying. Auth, billing and malformed requests are not.</summary>
    public bool IsRetryable => Kind switch
    {
        ApiErrorKind.MissingApiKey or ApiErrorKind.Cancelled => false,
        ApiErrorKind.Network or ApiErrorKind.Timeout or ApiErrorKind.BadResponse => true,
        _ => Status is 408 or 409 or 425 or 429 or >= 500,
    };

    public bool IsCancelled => Kind == ApiErrorKind.Cancelled;

    public static ApiException From(Exception error)
    {
        switch (error)
        {
            case ApiException api:
                return api;
            case OperationCanceledException:
                return Cancelled();
            case AggregateException { InnerExceptions.Count: 1 } aggregate:
                return From(aggregate.InnerException!);
            case HttpRequestException http:
                if (FindSocketError(http) == SocketError.TimedOut) return Network(L("连接超时", "connection timed out"), http);
                return Network(http.InnerException?.Message ?? http.Message, http);
            case IOException io:
                return Network(io.Message, io);
            case TimeoutException timeout:
                return Network(L("连接超时", "connection timed out"), timeout);
            default:
                return Network(error.Message, error);
        }
    }

    private static SocketError? FindSocketError(Exception e)
    {
        for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
        {
            if (inner is SocketException socket) return socket.SocketErrorCode;
        }
        return null;
    }

    public bool Equals(ApiException? other) =>
        other is not null && Kind == other.Kind && Detail == other.Detail && Status == other.Status && Seconds.Equals(other.Seconds);

    public override bool Equals(object? obj) => Equals(obj as ApiException);

    public override int GetHashCode() => HashCode.Combine(Kind, Detail, Status, Seconds);

    public override string ToString() => $"{Kind}({Status}) {Detail}";
}

public sealed record RetryPolicy(int MaxAttempts = 4, double BaseDelay = 1.0)
{
    /// <summary>1s, 2s, 4s … with ±20% jitter so parallel chunks don't retry in lockstep.</summary>
    public double Delay(int afterAttempt)
    {
        var baseDelay = BaseDelay * Math.Pow(2, afterAttempt - 1);
        return baseDelay * (0.8 + Random.Shared.NextDouble() * 0.4);
    }

    public async Task<T> Run<T>(Func<int, CancellationToken, Task<T>> operation,
                                Action<int, ApiException>? onRetry = null,
                                CancellationToken cancellationToken = default)
    {
        var attempt = 1;
        while (true)
        {
            try
            {
                return await operation(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var apiError = ApiException.From(error);
                if (!apiError.IsRetryable || attempt >= MaxAttempts || cancellationToken.IsCancellationRequested)
                {
                    throw apiError;
                }
                onRetry?.Invoke(attempt, apiError);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Delay(attempt)), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw ApiException.Cancelled();
                }
                attempt += 1;
            }
        }
    }
}

public static class Timeouts
{
    /// <summary>
    /// Runs <paramref name="operation"/>, throwing <see cref="ApiErrorKind.Timeout"/> if it doesn't finish within
    /// <paramref name="seconds"/>. Returns as soon as the deadline passes, even if the operation ignores cancellation.
    /// </summary>
    public static async Task<T> WithTimeout<T>(double seconds, Func<CancellationToken, Task<T>> operation,
                                               CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = operation(linked.Token);
        var deadline = Task.Delay(TimeSpan.FromSeconds(seconds), linked.Token);
        var winner = await Task.WhenAny(work, deadline).ConfigureAwait(false);
        if (winner == work)
        {
            linked.Cancel(); // stops the timer
            return await work.ConfigureAwait(false);
        }
        linked.Cancel();
        _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe a late failure
        if (cancellationToken.IsCancellationRequested) throw ApiException.Cancelled();
        throw ApiException.TimedOut(seconds);
    }
}
