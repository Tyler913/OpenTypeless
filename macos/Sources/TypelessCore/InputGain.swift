import Foundation

/// Lifts abnormally quiet microphone input to a level the pipeline treats as speech.
///
/// While a call app (WeChat, FaceTime…) runs Apple's voice processing, macOS hands other apps the built-in
/// microphone's raw capsules, about 30 dB below normal: speech arrives near −70 dBFS RMS, under the level silent
/// chunks are skipped at (see `AudioLevel.isSilent`), so a dictation came out empty.
///
/// The gain follows the loudest recent speech (a peak that decays by 6 dB over 5 s), not each syllable, so it
/// stays nearly constant: the chunker's quietest-window search, pause detection and trimming see the same shape
/// as in the raw audio, only louder. It rises only once enough speech has been heard, and never so far that the
/// noise floor would pass `noiseCeiling`: pauses and silence stay below the silence threshold, so nothing is sent
/// for transcription that wasn't spoken. Speech that already reaches the target passes through unchanged.
public struct InputGain {
    /// RMS the loudest speech frames are lifted to (about −26 dBFS): ordinary speech into a Mac's microphone.
    public static let targetLevel: Float = 0.05
    /// At most +36 dB (during a call the raw microphone's speech can sit near −70 dBFS RMS).
    public static let maximumGain: Float = 63
    /// The noise floor is never lifted above this RMS (about −54 dBFS), half the silence threshold.
    public static let noiseCeiling: Float = 0.002
    /// 20 ms at 16 kHz.
    static let frameSamples = 320
    /// Speech heard before the gain may rise (0.8 s): a soft first word says nothing about the level yet.
    static let evidenceFrames = 40
    /// Per 20 ms frame: the loud level falls 6 dB over 5 s.
    static let loudDecay: Float = 0.997_24

    public private(set) var gain: Float
    /// RMS of the quietest recent frames: falls at once, creeps back up (about 1.3 dB/s).
    private var noise: Float?
    /// RMS of the loudest recent speech frames.
    private var loud: Float = 0
    private var speechFrames = 0

    /// `initialGain` starts from a level already learnt (e.g. earlier in the same call); the noise cap still applies.
    public init(initialGain: Float = 1) {
        gain = max(1, min(initialGain, Self.maximumGain))
    }

    /// Applies the gain in place (16 kHz mono, −1…1).
    public mutating func process(_ samples: inout [Float]) {
        var start = 0
        while start < samples.count {
            let end = min(start + Self.frameSamples, samples.count)
            processFrame(&samples, start..<end)
            start = end
        }
    }

    private mutating func processFrame(_ samples: inout [Float], _ range: Range<Int>) {
        var sum: Float = 0
        for index in range { sum += samples[index] * samples[index] }
        let rms = (sum / Float(range.count)).squareRoot()

        let noise: Float
        if let current = self.noise, rms >= current {
            noise = current * 1.003
        } else {
            noise = max(rms, 1e-6)
        }
        self.noise = noise

        // Speech: at least 10 dB over the noise floor.
        if rms > noise * 3.16, rms > 2e-5 {
            loud = max(rms, loud * Self.loudDecay)
            speechFrames += 1
        } else {
            loud *= Self.loudDecay
        }

        let previous = gain
        if speechFrames >= Self.evidenceFrames, loud > 0 {
            let desired = max(1, min(Self.targetLevel / loud, Self.maximumGain))
            if desired > gain {
                gain = min(desired, gain * 1.1) // +0.8 dB per frame: +36 dB in under a second
            } else if desired < gain {
                gain = max(desired, gain * 0.98)
            }
        }
        gain = max(1, min(gain, Self.noiseCeiling / noise))
        guard previous != 1 || gain != 1 else { return }

        // Ramp across the frame so gain changes don't click, and never clip.
        let step = (gain - previous) / Float(range.count)
        var peak: Float = 0
        for (offset, index) in range.enumerated() {
            samples[index] *= previous + step * Float(offset + 1)
            peak = max(peak, abs(samples[index]))
        }
        if peak > 0.98 {
            let scale = 0.98 / peak
            for index in range { samples[index] *= scale }
            gain = max(1, gain * scale)
        }
    }
}
