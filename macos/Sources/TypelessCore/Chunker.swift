import Foundation

public struct AudioChunk: Sendable, Equatable {
    public let index: Int
    /// Offset of the first sample within the whole recording.
    public let startSample: Int
    public let samples: [Int16]

    public var duration: Double { AudioFormat.seconds(forSampleCount: samples.count) }
    public var startTime: Double { AudioFormat.seconds(forSampleCount: startSample) }
}

/// Splits a live PCM stream into chunks that are each short enough for a single STT request.
///
/// Once the pending audio reaches `maxSeconds`, it cuts at the centre of the quietest
/// `windowSeconds` window found between `minSeconds` and `maxSeconds`, i.e. at a pause,
/// so words are not sliced in half. No thresholds to tune: the quietest spot always wins.
public final class Chunker {
    public struct Config: Sendable {
        public var minSeconds: Double
        public var maxSeconds: Double
        public var windowSeconds: Double

        public init(minSeconds: Double = 18, maxSeconds: Double = 28, windowSeconds: Double = 0.4) {
            self.minSeconds = minSeconds
            self.maxSeconds = maxSeconds
            self.windowSeconds = windowSeconds
        }
    }

    private let config: Config
    private var pending: [Int16] = []
    /// How much of the front of `pending` this append has already handed out as chunks.
    private var emitted = 0
    private var pendingStart = 0
    private var nextIndex = 0

    public init(config: Config = Config()) {
        self.config = config
    }

    /// Appends samples and returns any chunks that became ready.
    public func append(_ samples: [Int16]) -> [AudioChunk] {
        pending.append(contentsOf: samples)
        var ready: [AudioChunk] = []
        let maxSamples = AudioFormat.sampleCount(forSeconds: config.maxSeconds)
        while pending.count - emitted >= maxSamples {
            let cut = quietestCutPoint()
            ready.append(emit(upTo: cut))
        }
        dropEmitted()
        return ready
    }

    /// Flushes whatever audio is left as the final chunk (nil when nothing is left).
    public func finish() -> AudioChunk? {
        guard !pending.isEmpty else { return nil }
        defer { dropEmitted() }
        return emit(upTo: pending.count)
    }

    private func emit(upTo cut: Int) -> AudioChunk {
        let chunk = AudioChunk(index: nextIndex, startSample: pendingStart, samples: Array(pending[emitted..<(emitted + cut)]))
        emitted += cut
        pendingStart += cut
        nextIndex += 1
        return chunk
    }

    /// Removes what `emit` handed out, once per call rather than once per chunk: removing each chunk from the front
    /// moved all the audio after it, so re-transcribing a long recording (appended in one go) was quadratic.
    private func dropEmitted() {
        pending.removeFirst(emitted)
        emitted = 0
    }

    private func quietestCutPoint() -> Int {
        pending.withUnsafeBufferPointer { all in quietestCutPoint(in: UnsafeBufferPointer(rebasing: all[emitted...])) }
    }

    private func quietestCutPoint(in pending: UnsafeBufferPointer<Int16>) -> Int {
        let lower = AudioFormat.sampleCount(forSeconds: config.minSeconds)
        let upper = min(pending.count, AudioFormat.sampleCount(forSeconds: config.maxSeconds))
        let window = max(1, AudioFormat.sampleCount(forSeconds: config.windowSeconds))
        let hop = max(1, AudioFormat.sampleCount(forSeconds: 0.02))
        guard upper - lower > window else { return upper }

        // Sliding sum of squares over the window, sampled every `hop` samples.
        var bestStart = lower
        var bestEnergy = Double.greatestFiniteMagnitude
        var start = lower
        var energy = 0.0
        for i in start..<(start + window) { energy += Double(pending[i]) * Double(pending[i]) }
        while start + window <= upper {
            if energy < bestEnergy {
                bestEnergy = energy
                bestStart = start
            }
            let next = start + hop
            guard next + window <= upper else { break }
            for i in start..<next { energy -= Double(pending[i]) * Double(pending[i]) }
            for i in (start + window)..<(next + window) { energy += Double(pending[i]) * Double(pending[i]) }
            start = next
        }
        return bestStart + window / 2
    }
}

public enum AudioLevel {
    /// RMS of the samples normalised to 0...1.
    public static func rms(_ samples: ArraySlice<Int16>) -> Float {
        guard !samples.isEmpty else { return 0 }
        var sum: Float = 0
        for s in samples {
            let v = Float(s) / 32768
            sum += v * v
        }
        return (sum / Float(samples.count)).squareRoot()
    }

    public static func rms(_ samples: [Int16]) -> Float { rms(samples[...]) }

    /// True when no 30 ms frame rises above `threshold` RMS. Such chunks are skipped because
    /// STT models tend to hallucinate text ("Thanks for watching!") on pure silence.
    public static func isSilent(_ samples: [Int16], threshold: Float = 0.004) -> Bool {
        let frame = AudioFormat.sampleCount(forSeconds: 0.03)
        var i = 0
        while i < samples.count {
            let end = min(i + frame, samples.count)
            if rms(samples[i..<end]) > threshold { return false }
            i = end
        }
        return true
    }
}
