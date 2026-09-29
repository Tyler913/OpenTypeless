import Foundation

/// Tells speech from pauses on 30 ms frames: to trim the silence off both ends of the audio sent for transcription,
/// and to notice while recording that the speaker has stopped (see `PauseTracker`).
///
/// A frame is speech when its RMS is above `threshold`: twice the level of the quietest tenth of the frames (the
/// room's noise), but never below 0.004, the level silent chunks are skipped under, and never above 0.008 (about
/// −42 dBFS, well under quiet speech). So a noisy room still has pauses, and a softly spoken word is never taken for one.
public enum VoiceActivity {
    public static let frameSamples = 480 // 30 ms
    public static let minimumThreshold: Float = 0.004
    public static let maximumThreshold: Float = 0.008
    /// Audio kept around the speech when trimming, so soft onsets and word endings survive.
    public static let paddingSeconds = 0.3

    /// The RMS of each 30 ms frame; a shorter last frame counts too.
    public static func frameLevels(_ samples: [Int16]) -> [Float] {
        stride(from: 0, to: samples.count, by: frameSamples).map { start in
            AudioLevel.rms(samples[start..<min(start + frameSamples, samples.count)])
        }
    }

    /// The level above which a frame is speech, given the levels of the frames around it.
    public static func threshold(_ levels: [Float]) -> Float {
        guard !levels.isEmpty else { return minimumThreshold }
        let sorted = levels.sorted()
        return min(max(2 * sorted[sorted.count / 10], minimumThreshold), maximumThreshold)
    }

    /// Where the speech in `samples` starts and ends, widened by `padding` seconds on each side: all of it when no
    /// frame is speech.
    public static func speechBounds(_ samples: [Int16], padding: Double = paddingSeconds) -> (start: Int, end: Int) {
        let levels = frameLevels(samples)
        let threshold = threshold(levels)
        guard let first = levels.firstIndex(where: { $0 > threshold }),
              let last = levels.lastIndex(where: { $0 > threshold }) else { return (0, samples.count) }
        let pad = AudioFormat.sampleCount(forSeconds: padding)
        return (max(0, first * frameSamples - pad), min(samples.count, (last + 1) * frameSamples + pad))
    }
}

/// Follows the live audio 30 ms at a time, to tell when the speaker last spoke and whether they spoke again after a
/// given point. Positions are sample offsets from the start of the recording. Not thread-safe.
public final class PauseTracker {
    private var levels: [Float] = []
    /// The frame `levels[0]` belongs to: frames before it (the chunks already cut) are forgotten.
    private var firstFrame = 0
    private var partial: [Int16] = []

    public init() {
        partial.reserveCapacity(VoiceActivity.frameSamples)
    }

    /// Samples appended so far.
    public var sampleCount: Int { (firstFrame + levels.count) * VoiceActivity.frameSamples + partial.count }

    public func append(_ samples: [Int16]) {
        var rest = samples[...]
        while !rest.isEmpty {
            let take = min(rest.count, VoiceActivity.frameSamples - partial.count)
            partial.append(contentsOf: rest.prefix(take))
            rest = rest.dropFirst(take)
            guard partial.count == VoiceActivity.frameSamples else { break }
            levels.append(AudioLevel.rms(partial))
            partial.removeAll(keepingCapacity: true)
        }
    }

    /// Forgets the frames that end at or before `sample`, so only the audio not yet cut into a chunk counts.
    public func forget(before sample: Int) {
        let drop = min(levels.count, sample / VoiceActivity.frameSamples - firstFrame)
        guard drop > 0 else { return }
        levels.removeFirst(drop)
        firstFrame += drop
    }

    /// Where the last frame of speech ends, or nil while there has been none.
    public func lastSpeechEnd() -> Int? {
        let threshold = VoiceActivity.threshold(levels)
        guard let last = levels.lastIndex(where: { $0 > threshold }) else { return nil }
        return (firstFrame + last + 1) * VoiceActivity.frameSamples
    }

    /// Whether any audio after `sample` is speech, the frame straddling it included. With `includePartial` the last,
    /// not yet complete frame counts too (at the end of the recording).
    public func speechAfter(_ sample: Int, includePartial: Bool = false) -> Bool {
        let threshold = VoiceActivity.threshold(levels)
        let from = max(0, sample / VoiceActivity.frameSamples - firstFrame)
        if from < levels.count, levels[from...].contains(where: { $0 > threshold }) { return true }
        return includePartial && !partial.isEmpty && sampleCount > sample && AudioLevel.rms(partial) > threshold
    }
}
