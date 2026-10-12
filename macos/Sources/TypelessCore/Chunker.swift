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
/// Once `pauseCutSeconds` of audio are pending, it cuts as soon as the speaker pauses (`pauseSeconds` quiet after
/// speech, as `VoiceActivity` tells them apart), in the middle of the pause's first `pauseSeconds`: that chunk is sent
/// while they go on, so less is left to send when they let go of the key. Without such a pause, once the pending audio
/// reaches `maxSeconds` it cuts at the centre of the quietest `windowSeconds` window found between `minSeconds` and
/// `maxSeconds`, so words are not sliced in half: the quietest spot always wins.
///
/// Where it cuts depends only on the audio, not on how it arrives: a retry hands the saved recording over in one go
/// and reuses the transcripts of the chunks that succeeded by their index, so it must cut exactly where the live
/// recording, delivered 40 ms at a time, was cut.
public final class Chunker {
    public struct Config: Sendable {
        public var minSeconds: Double
        public var maxSeconds: Double
        public var windowSeconds: Double
        /// Pending audio from which a pause is enough to cut at. Short enough that a dictation which pauses now and
        /// then leaves at most this much (plus what follows the last pause) to send at release, since the wait for a
        /// chunk grows with its length (about 0.05 s per second of audio in 789 timed dictations); long enough that
        /// each request still has a few sentences of context and the chunk count stays low.
        public var pauseCutSeconds: Double
        /// How long the speaker has to be quiet for that: the pause the speculative tail waits for, so a pause long
        /// past `pauseCutSeconds` is cut at rather than transcribed ahead of time and then sent again.
        public var pauseSeconds: Double

        public init(minSeconds: Double = 18, maxSeconds: Double = 28, windowSeconds: Double = 0.4,
                    pauseCutSeconds: Double = 8, pauseSeconds: Double = TranscriptionPipeline.pauseSeconds) {
            self.minSeconds = minSeconds
            self.maxSeconds = maxSeconds
            self.windowSeconds = windowSeconds
            self.pauseCutSeconds = pauseCutSeconds
            self.pauseSeconds = pauseSeconds
        }
    }

    /// Which way recordings are cut into chunks. A saved dictation's per-chunk transcripts are reused by a retry only
    /// when it was cut the same way (1, or none saved, was 18–28 s at the quietest spot only; 2 also cuts at pauses).
    public static let layout = 2

    private let config: Config
    private var pending: [Int16] = []
    /// The RMS of each whole 30 ms frame of the chunk being built, counted from its first sample.
    private var levels: [Float] = []
    /// How much of the front of `pending` this append has already handed out as chunks (always 0 between calls).
    private var emitted = 0
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
        while true {
            // A pause is looked for up to `maxSeconds` before falling back to the quietest spot, so a chunk is cut at
            // the same place whether its audio came in one go or 40 ms at a time.
            if let cut = pauseCutPoint(within: min(pending.count - emitted, maxSamples)) {
                ready.append(emit(upTo: cut))
            } else if pending.count - emitted >= maxSamples {
                ready.append(emit(upTo: quietestCutPoint()))
            } else {
                break
            }
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

    /// The chunk `finish` would flush now, left in place (nil when nothing is pending).
    public func peek() -> AudioChunk? {
        pending.isEmpty ? nil : AudioChunk(index: nextIndex, startSample: pendingStart, samples: pending)
    }

    private func emit(upTo cut: Int) -> AudioChunk {
        let chunk = AudioChunk(index: nextIndex, startSample: pendingStart, samples: Array(pending[emitted..<(emitted + cut)]))
        emitted += cut
        pendingStart += cut
        nextIndex += 1
        levels.removeAll(keepingCapacity: true)
        return chunk
    }

    /// Removes what `emit` handed out, once per call rather than once per chunk: removing each chunk from the front
    /// moved all the audio after it, so re-transcribing a long recording (appended in one go) was quadratic.
    private func dropEmitted() {
        pending.removeFirst(emitted)
        emitted = 0
    }

    /// Measures the frames of the chunk being built that end within its first `available` samples, and returns where
    /// to cut at the first pause found once `pauseCutSeconds` are pending, if any. Frame by frame, each decided on the
    /// frames up to it, so the answer doesn't depend on how the audio was split into appends.
    private func pauseCutPoint(within available: Int) -> Int? {
        let frame = VoiceActivity.frameSamples
        let earliest = AudioFormat.sampleCount(forSeconds: config.pauseCutSeconds)
        // Chunks too short to reach it (as in the performance test) are never cut at pauses: nothing to measure.
        guard earliest < AudioFormat.sampleCount(forSeconds: config.maxSeconds) else { return nil }
        let pauseFrames = max(1, AudioFormat.sampleCount(forSeconds: config.pauseSeconds) / frame)
        while (levels.count + 1) * frame <= available {
            let start = emitted + levels.count * frame
            levels.append(AudioLevel.rms(pending[start..<(start + frame)]))
            if levels.count * frame >= earliest, levels.count > pauseFrames, pauseJustBegan(frames: pauseFrames) {
                let pauseStart = (levels.count - pauseFrames) * frame
                return pauseStart + pauseFrames * frame / 2
            }
        }
        return nil
    }

    /// Whether the last `count` frames are quiet and the one before them speech: the speaker has just been quiet for
    /// `pauseSeconds`. A pause that began earlier (before `pauseCutSeconds`) isn't cut at when it gets there: the
    /// speculative tail already covers it, and cutting would send that audio a second time.
    private func pauseJustBegan(frames count: Int) -> Bool {
        let speech = levels[levels.count - count - 1]
        let quiet = levels[(levels.count - count)...]
        // Most frames are clearly speech or clearly quiet whatever the room's noise (the threshold is always within
        // these bounds), which spares working the threshold out over every frame.
        guard speech > VoiceActivity.minimumThreshold, let loudest = quiet.max(), loudest <= VoiceActivity.maximumThreshold,
              speech > loudest else { return false }
        if speech > VoiceActivity.maximumThreshold, loudest <= VoiceActivity.minimumThreshold { return true }
        let threshold = VoiceActivity.threshold(levels)
        return speech > threshold && loudest <= threshold
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
