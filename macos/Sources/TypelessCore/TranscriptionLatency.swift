import Foundation

/// How long a speech-to-text chunk usually takes, and so when it is late enough to ask the backup route too.
///
/// Fitted on 128 timed dictations (microsoft/mai-transcribe-2 through OpenRouter, 1–27 s chunks): the wait barely
/// grows with the length of the audio, about 0.39 s plus 0.02 s per second of audio (a Theil–Sen line through the
/// chunks left after dropping those more than three MADs above it). Most answers land on that line; a slower mode
/// around 1.6–2.3 s and the odd 4.7–10.6 s answer don't. Only the last chunk is usually waited on, and speech-to-text
/// is cheap next to the wait, so the backup is asked as soon as a chunk is `margin` (0.2 s) past the line: about
/// 40% of last chunks in that data. A route that is slower or faster across the board shifts the line by the median
/// of its recent residuals, which the odd very late answer doesn't move.
public final class TranscriptionLatency: @unchecked Sendable {
    public static let baseSeconds = 0.39
    public static let secondsPerAudioSecond = 0.02
    public static let margin = 0.2

    private let lock = NSLock()
    private var residuals: [Double] = []
    private let window: Int
    public let minimumDelay: Double
    public let maximumDelay: Double

    public init(window: Int = 20, minimumDelay: Double = 0.5, maximumDelay: Double = 25) {
        self.window = window
        self.minimumDelay = minimumDelay
        self.maximumDelay = maximumDelay
    }

    static func baseline(forAudioSeconds seconds: Double) -> Double {
        baseSeconds + secondsPerAudioSecond * max(seconds, 0)
    }

    /// A chunk of `audioSeconds` came back after `latency` seconds.
    public func record(latency: Double, audioSeconds: Double) {
        guard latency.isFinite, latency >= 0 else { return }
        lock.withLock {
            residuals.append(latency - Self.baseline(forAudioSeconds: audioSeconds))
            if residuals.count > window { residuals.removeFirst(residuals.count - window) }
        }
    }

    public var samples: Int { lock.withLock { residuals.count } }

    /// How far this route runs from the fitted line: the median recent residual, 0 until three chunks are timed.
    public var shift: Double {
        let recent = lock.withLock { residuals }
        return recent.count >= 3 ? ConnectionCheck.median(recent) : 0
    }

    /// The wait expected for a chunk of this length.
    public func expected(forAudioSeconds seconds: Double) -> Double {
        Self.baseline(forAudioSeconds: seconds) + shift
    }

    /// How long to wait for a chunk before asking the backup too: the expected wait plus `margin`.
    public func hedgeDelay(forAudioSeconds seconds: Double) -> Double {
        min(maximumDelay, max(minimumDelay, expected(forAudioSeconds: seconds) + Self.margin))
    }
}
