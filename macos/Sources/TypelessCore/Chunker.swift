import Foundation

public struct AudioChunk: Sendable, Equatable {
    public let index: Int
    /// Offset of the first sample within the whole recording.
    public let startSample: Int
    public let samples: [Int16]

    public var duration: Double { AudioFormat.seconds(forSampleCount: samples.count) }
    public var startTime: Double { AudioFormat.seconds(forSampleCount: startSample) }

    /// This chunk without the silence before and after the speech (see `VoiceActivity.speechBounds`): less to upload
    /// and transcribe, and nothing for the model to hallucinate on. Itself when there is nothing to trim.
    public func trimmed() -> AudioChunk {
        let (start, end) = VoiceActivity.speechBounds(samples)
        if start == 0, end == samples.count { return self }
        return AudioChunk(index: index, startSample: startSample + start, samples: Array(samples[start..<end]))
    }
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
    /// Where the audio not yet cut into a chunk starts, in samples from the start of the recording.
    public private(set) var pendingStart = 0
    private var nextIndex = 0

    public init(config: Config = Config()) {
        self.config = config
    }

    /// Appends samples and returns any chunks that became ready.
    public func append(_ samples: [Int16]) -> [AudioChunk] {
        pending.append(contentsOf: samples)
        var ready: [AudioChunk] = []
        let maxSamples = AudioFormat.sampleCount(forSeconds: config.maxSeconds)
        while pending.count >= maxSamples {
            let cut = quietestCutPoint()
            ready.append(emit(upTo: cut))
        }
        return ready
    }

    /// Flushes whatever audio is left as the final chunk (nil when nothing is left).
    public func finish() -> AudioChunk? {
        guard !pending.isEmpty else { return nil }
        return emit(upTo: pending.count)
    }

    /// The chunk `finish` would flush now, left in place (nil when nothing is pending).
    public func peek() -> AudioChunk? {
        pending.isEmpty ? nil : AudioChunk(index: nextIndex, startSample: pendingStart, samples: pending)
    }

    private func emit(upTo cut: Int) -> AudioChunk {
        let chunk = AudioChunk(index: nextIndex, startSample: pendingStart, samples: Array(pending[0..<cut]))
        pending.removeFirst(cut)
        pendingStart += cut
        nextIndex += 1
        return chunk
    }

    private func quietestCutPoint() -> Int {
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
