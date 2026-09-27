import Foundation

public enum APIError: Error, LocalizedError, Equatable {
    case missingAPIKey(String)
    case http(status: Int, message: String)
    case network(String)
    case timeout(seconds: Double)
    case badResponse(String)
    case cancelled

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
        case let .network(message): return L("网络错误：", "Network error: ") + message
        case let .timeout(seconds): return L("请求超时（\(Int(seconds)) 秒）", "Timed out after \(Int(seconds))s")
        case let .badResponse(message): return L("返回内容无法解析：", "Unexpected response: ") + message
        case .cancelled: return L("已取消", "Cancelled")
        }
    }

    /// Transient failures worth retrying. Auth, billing and malformed requests are not.
    public var isRetryable: Bool {
        switch self {
        case .missingAPIKey, .cancelled: return false
        case .network, .timeout: return true
        case .badResponse: return true
        case let .http(status, _):
            return status == 408 || status == 409 || status == 425 || status == 429 || status >= 500
        }
    }

    public static func from(_ error: Error) -> APIError {
        if let api = error as? APIError { return api }
        if error is CancellationError { return .cancelled }
        if let url = error as? URLError {
            if url.code == .cancelled { return .cancelled }
            if url.code == .timedOut { return .network(L("连接超时", "connection timed out")) }
            return .network(url.localizedDescription)
        }
        return .network(error.localizedDescription)
    }
}

public struct RetryPolicy: Sendable {
    public var maxAttempts: Int
    public var baseDelay: Double

    public init(maxAttempts: Int = 4, baseDelay: Double = 1.0) {
        self.maxAttempts = maxAttempts
        self.baseDelay = baseDelay
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
                guard apiError.isRetryable, attempt < maxAttempts, !Task.isCancelled else { throw apiError }
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
