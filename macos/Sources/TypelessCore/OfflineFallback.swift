import Foundation

/// The recognizer on this computer standing in for the speech-to-text provider while the network is down, so a
/// dictation still ends in text. Only with the recognizer's model for the language already there (nothing can be
/// downloaded then), and never for the provider's own refusals (a bad key, a rate limit, a server error), which keep the
/// retries and failures they always had. The cases are in `testdata/offline-fallback-cases.json`, read by both test
/// suites.
public enum OfflineFallback {
    /// The setting, on by default; the same key on both platforms.
    public static let defaultsKey = "onDeviceWhenOffline"

    /// Whether a chunk goes to the recognizer without trying the provider: once the dictation has switched to it
    /// (`switched`), and while the system says there's no network at all, so no request waits out its timeout. `ready`:
    /// the recognizer can do it without the network.
    public static func skipsProvider(switched: Bool, networkDown: Bool, ready: Bool) -> Bool {
        ready && (switched || networkDown)
    }

    /// Whether the recognizer takes over a chunk the provider failed with `error`, switching the rest of the dictation
    /// to it: only for a failure the network is to blame for, never a timeout or the provider's own answer. While the
    /// attempt has retries left (`retriesLeft`), only when trying again can't help: the system has no connection, or
    /// says the network is down. A DNS hiccup or a dropped connection gets its usual retries first: a server that turns
    /// a request down mid-upload drops the connection too.
    public static func takesOver(after error: APIError, retriesLeft: Bool, networkDown: Bool, ready: Bool) -> Bool {
        guard ready, let failure = error.networkFailure else { return false }
        return !retriesLeft || failure == .noConnection || networkDown
    }

    /// Whether `host` is this computer ("localhost", 127.0.0.1, ::1): a speech-to-text server there works with no
    /// network, so the system saying the network is down tells nothing about it.
    public static func isLocalHost(_ host: String) -> Bool {
        var host = host.trimmingCharacters(in: .whitespaces).lowercased()
        if host.hasPrefix("["), host.hasSuffix("]") { host = String(host.dropFirst().dropLast()) }
        if host.hasSuffix(".") { host.removeLast() }
        if host == "localhost" || host.hasSuffix(".localhost") || host == "::1" { return true }
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        // 127.0.0.0/8, written as four decimal numbers.
        return parts.count == 4 && parts[0] == "127" && parts.allSatisfy { part in
            (1...3).contains(part.count) && part.allSatisfy { ("0"..."9").contains($0) } && Int(part)! <= 255
        }
    }

    /// What a retry reuses of a dictation's transcripts (`texts`, by chunk index), and which of those were recognised on
    /// this computer for want of a network (`onDevice`). With the provider (`usesProvider`) those are sent to it again,
    /// so the retry gets the provider's text; on-device, everything is reused as it is.
    public static func reused(_ texts: [Int: String], onDevice: [Int]?,
                              usesProvider: Bool) -> (texts: [Int: String], onDevice: [Int]?) {
        let recognised = Set(onDevice ?? []).intersection(texts.keys)
        guard !recognised.isEmpty else { return (texts, nil) }
        guard usesProvider else { return (texts, recognised.sorted()) }
        return (texts.filter { !recognised.contains($0.key) }, nil)
    }
}

/// The recognizer on this computer as a provider's stand-in in a `TranscriptionPipeline` (see `OfflineFallback`).
public struct OfflineFallbackRoute: Sendable {
    public let route: OnDeviceRoute
    /// Whether it can recognise the dictation's language without the network: the model for it is already there. Asked
    /// once, as the pipeline starts.
    public let isReady: @Sendable () async -> Bool
    /// Whether the system says there's no network now.
    public let networkDown: @Sendable () -> Bool

    public init(route: OnDeviceRoute, isReady: @escaping @Sendable () async -> Bool,
                networkDown: @escaping @Sendable () -> Bool) {
        self.route = route
        self.isReady = isReady
        self.networkDown = networkDown
    }
}
