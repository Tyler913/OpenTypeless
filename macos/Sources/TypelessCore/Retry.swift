import Foundation

/// Why a request didn't reach the provider, when the network is to blame rather than the provider (see
/// `OfflineFallback`). The same names on both platforms.
public enum NetworkFailure: String, Sendable, CaseIterable {
    /// The system has no connection at all: no Wi-Fi or cable, airplane mode, or data not allowed.
    case noConnection
    /// The provider's host couldn't be found or reached: a DNS failure, no route to it, or the connection refused.
    case hostUnreachable
    /// The connection dropped part way. A server that turns a request down mid-upload does that too (see
    /// docs/DESIGN.md), so on its own it isn't taken for being offline.
    case connectionLost

    /// What a URLSession error says about the network; nil for one the network isn't to blame for (a timeout, TLS…).
    public init?(_ code: URLError.Code) {
        switch code {
        case .notConnectedToInternet, .dataNotAllowed, .internationalRoamingOff, .callIsActive: self = .noConnection
        case .cannotFindHost, .dnsLookupFailed, .cannotConnectToHost: self = .hostUnreachable
        case .networkConnectionLost: self = .connectionLost
        default: return nil
        }
    }
}

public enum APIError: Error, LocalizedError, Equatable {
    case missingAPIKey(String)
    case http(status: Int, message: String)
    case network(String)
    /// A network error the network itself is to blame for (see `NetworkFailure`); retried like any network error.
    case offline(NetworkFailure, String)
    case timeout(seconds: Double)
    case badResponse(String)
    case cancelled
    /// The speech recognizer on this Mac can't do this at all (a language it has no model for): not worth retrying.
    case unavailable(String)
    /// The speech recognizer on this Mac failed this time; the message is shown as it is.
    case recognition(String)

    public var errorDescription: String? {
        switch self {
        case let .missingAPIKey(provider): return L("还没有填写 \(provider) 的 API Key", "No API key set for \(provider)")
        case let .http(status, message):
            switch status {
            case 401: return L("API Key 无效 (401)：", "Invalid API key (401): ") + message
            case 402: return L("余额不足 (402)：", "Out of credits (402): ") + message
            case 429: return L("请求过于频繁 (429)：", "Rate limited (429): ") + message
            default: return "HTTP \(status): \(message)"
            }
        case let .network(message), let .offline(_, message): return L("网络错误：", "Network error: ") + message
        case let .timeout(seconds): return L("请求超时（\(Int(seconds)) 秒）", "Timed out after \(Int(seconds))s")
        case let .badResponse(message): return L("返回内容无法解析：", "Unexpected response: ") + message
        case .cancelled: return L("已取消", "Cancelled")
        case let .unavailable(message), let .recognition(message): return message
        }
    }

    /// Transient failures worth retrying. Auth, billing and malformed requests are not.
    /// The provider turns every request down this way (no key or a bad one, no credit, no such model or endpoint), not
    /// just this one: a file being transcribed stops at the first such failure rather than sending the rest.
    public var refusesEveryRequest: Bool {
        switch self {
        case .missingAPIKey, .unavailable: return true
        case let .http(status, _): return [401, 402, 403, 404].contains(status)
        default: return false
        }
    }

    public var isRetryable: Bool {
        switch self {
        case .missingAPIKey, .cancelled, .unavailable: return false
        case .network, .offline, .timeout, .recognition: return true
        case .badResponse: return true
        case let .http(status, _):
            return status == 408 || status == 409 || status == 425 || status == 429 || status >= 500
        }
    }

    public var isTimeout: Bool {
        if case .timeout = self { return true }
        return false
    }

    /// Why the network kept the request from the provider, when it did.
    public var networkFailure: NetworkFailure? {
        if case let .offline(failure, _) = self { return failure }
        return nil
    }

    public static func from(_ error: Error) -> APIError {
        if let api = error as? APIError { return api }
        if error is CancellationError { return .cancelled }
        if let url = error as? URLError {
            if url.code == .cancelled { return .cancelled }
            if url.code == .timedOut { return .network(L("连接超时", "connection timed out")) }
            if let failure = NetworkFailure(url.code) { return .offline(failure, url.localizedDescription) }
            return .network(url.localizedDescription)
        }
        return .network(error.localizedDescription)
    }
}

public struct RetryPolicy: Sendable {
    public var maxAttempts: Int
    public var baseDelay: Double
    /// False where a second wait as long as the first would cost more than it could win back: the clean-up, whose
    /// transcript can go in as spoken instead.
    public var retriesTimeouts: Bool
    /// True for an error after which trying again isn't worth it here: the network is known to be down and something
    /// else can take over (see `OfflineFallback`).
    public var stopsEarly: (@Sendable (APIError) -> Bool)?

    public init(maxAttempts: Int = 4, baseDelay: Double = 1.0, retriesTimeouts: Bool = true,
                stopsEarly: (@Sendable (APIError) -> Bool)? = nil) {
        self.maxAttempts = maxAttempts
        self.baseDelay = baseDelay
        self.retriesTimeouts = retriesTimeouts
        self.stopsEarly = stopsEarly
    }

    /// 1s, 2s, 4s … with ±20% jitter so parallel chunks don't retry in lockstep.
    public func delay(afterAttempt attempt: Int) -> Double {
        let base = baseDelay * pow(2, Double(attempt - 1))
        return base * Double.random(in: 0.8...1.2)
    }

    public func run<T>(
        onRetry: ((Int, APIError) -> Void)? = nil,
        _ operation: (Int) async throws -> T
    ) async throws -> T {
        var attempt = 1
        while true {
            do {
                return try await operation(attempt)
            } catch {
                let apiError = APIError.from(error)
                guard apiError.isRetryable, retriesTimeouts || !apiError.isTimeout, attempt < maxAttempts,
                      stopsEarly?(apiError) != true, !Task.isCancelled else { throw apiError }
                onRetry?(attempt, apiError)
                try await Task.sleep(nanoseconds: UInt64(delay(afterAttempt: attempt) * 1_000_000_000))
                attempt += 1
            }
        }
    }
}

/// Runs `operation`, throwing `APIError.timeout` if it doesn't finish within `seconds`.
public func withTimeout<T: Sendable>(
    _ seconds: Double,
    _ operation: @escaping @Sendable () async throws -> T
) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await operation() }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            throw APIError.timeout(seconds: seconds)
        }
        defer { group.cancelAll() }
        guard let result = try await group.next() else { throw APIError.cancelled }
        return result
    }
}
