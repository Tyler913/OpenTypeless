import Foundation

/// Where microphone samples go. While the microphone is kept warm between dictations, the last moments are held
/// in a short pre-roll; when a dictation starts, that pre-roll is handed over first and everything after it
/// follows in order, so the first syllable is there even when speaking starts right as the key goes down.
///
/// Thread-safe: `deliver` runs on the audio thread, `begin` / `end` on the main thread.
public final class AudioSink: @unchecked Sendable {
    public typealias Handler = ([Int16]) -> Void

    private let lock = NSLock()
    private let capacity: Int
    private var preRoll: [Int16] = []
    private var handler: Handler?

    /// `preRollSeconds` of audio are kept while no dictation is running.
    public init(preRollSeconds: Double = 0.4) {
        capacity = AudioFormat.sampleCount(forSeconds: preRollSeconds)
    }

    public var isRecording: Bool { lock.withLock { handler != nil } }

    /// Samples from the microphone, in order.
    public func deliver(_ samples: [Int16]) {
        lock.lock()
        defer { lock.unlock() }
        if let handler {
            handler(samples)
            return
        }
        preRoll.append(contentsOf: samples)
        if preRoll.count > capacity { preRoll.removeFirst(preRoll.count - capacity) }
    }

    /// Starts sending samples to `handler`, beginning with the pre-roll. Returns how many pre-roll samples it got.
    @discardableResult
    public func begin(_ handler: @escaping Handler) -> Int {
        lock.lock()
        defer { lock.unlock() }
        let held = preRoll
        preRoll.removeAll(keepingCapacity: true)
        if !held.isEmpty { handler(held) }
        self.handler = handler
        return held.count
    }

    /// Stops sending samples on; the pre-roll starts filling again.
    public func end() {
        lock.withLock {
            handler = nil
            preRoll.removeAll(keepingCapacity: true)
        }
    }
}
