import Foundation

/// Lifts abnormally quiet microphone input to a level the pipeline treats as speech.
///
/// While a call app (WeChat, FaceTime…) runs Apple's voice processing, macOS hands other apps the built-in
/// microphone's raw capsule signal, about 30 dB below normal: speech arrives near −68 dBFS RMS, under the level
/// silent chunks are skipped at (see `AudioLevel.isSilent`), so a dictation came out empty.
///
/// The gain rises only on frames that are clearly speech (well above the noise floor), towards `targetLevel`,
/// and never so far that the noise floor would pass `noiseCeiling`: pauses and silence stay below the silence
/// threshold, so nothing is sent for transcription that wasn't spoken. Normal speech is already above the target
/// and passes through unchanged.
public struct InputGain {
    /// Speech-frame RMS the gain aims for (about −34 dBFS): quiet but clearly speech. Normal speech is louder.
    public static let targetLevel: Float = 0.02
    /// At most +36 dB (during a call the raw microphone's speech can sit near −70 dBFS RMS).
    public static let maximumGain: Float = 63
    /// The noise floor is never lifted above this RMS (about −54 dBFS), half the silence threshold.
    public static let noiseCeiling: Float = 0.002
    /// 20 ms at 16 kHz.
    static let frameSamples = 320

    public private(set) var gain: Float = 1
    /// RMS of the quietest recent frames: falls at once, creeps back up (about 1.3 dB/s).
    private var noise: Float?

    public init() {}

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

        let previous = gain
        // Speech: at least 10 dB over the noise floor.
        if rms > noise * 3.16, rms > 2e-5 {
            let desired = max(1, min(Self.targetLevel / rms, Self.maximumGain, Self.noiseCeiling / noise))
            if desired > gain {
                gain = min(desired, gain * 1.2) // up to +1.6 dB per frame: +30 dB in about 0.4 s of speech
            } else if desired < gain {
                gain = max(desired, gain * 0.9)
            }
        }
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
