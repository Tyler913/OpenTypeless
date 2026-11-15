import Foundation

/// What the recorder does when its audio engine says its configuration changed mid-recording.
///
/// Pointing the engine at a chosen input makes it post a change of its own just after it starts, while it goes on
/// running on that input in the same format. Rebuilding the engine for it started it again, which posted another: the
/// microphone went on and off without end (the menu bar's indicator flickering) and nothing was recorded. So a change
/// is acted on only when something did change, and a run of rebuilds that keeps going is cut short either way.
public struct CaptureRestartPolicy {
    public enum Decision: Equatable {
        /// Nothing changed that matters: the engine keeps running as it is.
        case keep
        /// The input went away, or its format changed: build the engine again.
        case rebuild
        /// Rebuilt too often in a row and stopped again each time: the recording fails.
        case giveUp
    }

    /// At most this many rebuilds…
    public static let maximumRebuilds = 3
    /// …within this many seconds.
    public static let window: TimeInterval = 5

    private var rebuilds: [TimeInterval] = []

    public init() {}

    /// A new recording: earlier rebuilds don't count against it.
    public mutating func reset() {
        rebuilds = []
    }

    /// - Parameters:
    ///   - isRunning: the engine is still running.
    ///   - onExpectedInput: it's still on the input it was opened on (the chosen one, or the system default).
    ///   - sameFormat: that input's sample rate and channel count are what the tap was installed for.
    ///   - now: a monotonic time in seconds.
    public mutating func decide(isRunning: Bool, onExpectedInput: Bool, sameFormat: Bool, now: TimeInterval) -> Decision {
        if isRunning, onExpectedInput, sameFormat { return .keep }
        rebuilds.removeAll { now - $0 >= Self.window }
        if rebuilds.count >= Self.maximumRebuilds { return isRunning ? .keep : .giveUp }
        rebuilds.append(now)
        return .rebuild
    }
}
