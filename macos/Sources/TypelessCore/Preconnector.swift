import Foundation

extension APIClient {
    /// Opens a connection to the provider ahead of the first real request, so that request doesn't wait for DNS, TCP
    /// and TLS. A HEAD request for the base URL, with no key: any answer, even an error status, leaves a warm
    /// connection in the shared session. Never throws.
    public func preconnect() async {
        var request = URLRequest(url: endpoint.baseURL)
        request.httpMethod = "HEAD"
        request.timeoutInterval = 10
        // Only a head start: the real request connects by itself.
        _ = try? await session.data(for: request)
    }
}

/// Warms the connections a dictation is about to use: called when the key goes down (and every so often while
/// recording), so the first speech-to-text and clean-up requests don't pay for setting up a connection. Each host is
/// warmed once however many routes use it, and not again within `minimumInterval` seconds.
public final class Preconnector: @unchecked Sendable {
    private let minimumInterval: Double
    private let clock: @Sendable () -> Double
    private let lock = NSLock()
    private var warmedAt: [String: Double] = [:]

    public init(minimumInterval: Double = 20,
                clock: @escaping @Sendable () -> Double = { ProcessInfo.processInfo.systemUptime }) {
        self.minimumInterval = minimumInterval
        self.clock = clock
    }

    /// Starts a `preconnect` for each host not warmed recently; returns them (for tests).
    @discardableResult
    public func warm(_ clients: [APIClient?]) -> [Task<Void, Never>] {
        let now = clock()
        var started: [Task<Void, Never>] = []
        for case let client? in clients {
            let url = client.endpoint.baseURL
            let host = "\(url.scheme ?? "")://\(url.host ?? "")" + (url.port.map { ":\($0)" } ?? "")
            let due = lock.withLock { () -> Bool in
                if let at = warmedAt[host], now - at < minimumInterval { return false }
                warmedAt[host] = now
                return true
            }
            if due { started.append(Task { await client.preconnect() }) }
        }
        return started
    }
}
