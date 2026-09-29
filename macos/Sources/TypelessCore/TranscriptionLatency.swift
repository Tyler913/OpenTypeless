import Foundation

/// How long a speech-to-text route usually takes, relative to the length of the audio, and so when a chunk is late
/// enough to ask the backup route too. Waits scale with audio length (a 28 s chunk takes longer than a 3 s one), so
/// the median of recent wait ÷ audio-seconds ratios is kept rather than a fixed number of seconds.
public final class TranscriptionLatency: @unchecked Sendable {
    private let lock = NSLock()
    private var ratios: [Double] = []
    private let window: Int
    public let minimumDelay: Double
    public let maximumDelay: Double

    /// Until three chunks have been timed: 0.3 s per second of audio plus 1.5 s, a conservative guess.
    static let defaultRatio = 0.3
    static let defaultOverhead = 1.5

    public init(window: Int = 20, minimumDelay: Double = 3, maximumDelay: Double = 25) {
        self.window = window
        self.minimumDelay = minimumDelay
        self.maximumDelay = maximumDelay
    }

    /// A chunk of `audioSeconds` came back after `latency` seconds.
    public func record(latency: Double, audioSeconds: Double) {
        guard latency.isFinite, latency >= 0 else { return }
        lock.withLock {
            ratios.append(latency / max(audioSeconds, 1))
            if ratios.count > window { ratios.removeFirst(ratios.count - window) }
        }
    }

    public var samples: Int { lock.withLock { ratios.count } }

    /// The wait expected for a chunk of this length.
    public func expected(forAudioSeconds seconds: Double) -> Double {
        let recent = lock.withLock { ratios }
        guard recent.count >= 3 else { return Self.defaultRatio * seconds + Self.defaultOverhead }
        return ConnectionCheck.median(recent) * max(seconds, 1)
    }

    /// How long to wait for a chunk before asking the backup too: twice the expected wait plus a second.
    public func hedgeDelay(forAudioSeconds seconds: Double) -> Double {
        min(maximumDelay, max(minimumDelay, expected(forAudioSeconds: seconds) * 2 + 1))
    }
}
