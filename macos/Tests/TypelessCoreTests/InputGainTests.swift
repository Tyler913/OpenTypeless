import Foundation
import Testing
@testable import TypelessCore

struct InputGainTests {
    /// Deterministic noise in −1…1.
    private struct Noise {
        var state: UInt64 = 0x9E37_79B9_7F4A_7C15
        mutating func next() -> Float {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return Float(Int64(bitPattern: state >> 11) % 2_000_001 - 1_000_000) / 1_000_000
        }
    }

    /// `noiseLevel` throughout, with bursts of speech-like noise at `speechLevel` (RMS) for the middle seconds.
    /// Successive syllables are scaled by `syllableLevels` in turn (soft word endings, stressed syllables…).
    private func signal(speechLevel: Float, noiseLevel: Float, silence: Double = 1, speech: Double = 4,
                        syllableLevels: [Float] = [1]) -> [Float] {
        var noise = Noise()
        let total = AudioFormat.sampleCount(forSeconds: silence * 2 + speech)
        let speechRange = AudioFormat.sampleCount(forSeconds: silence)..<AudioFormat.sampleCount(forSeconds: silence + speech)
        // Uniform noise in −1…1 has an RMS of 1/√3.
        let unit = Float(3).squareRoot()
        return (0..<total).map { index in
            var value = noise.next() * noiseLevel * unit
            // Syllables, counted from the start of the speech: on for a sixth of a second, off for the next.
            guard speechRange.contains(index) else { return value }
            let syllable = (index - speechRange.lowerBound) / (AudioFormat.sampleRate / 6)
            if syllable % 2 == 0 {
                let level = syllableLevels[(syllable / 2) % syllableLevels.count]
                value += noise.next() * speechLevel * level * unit * Float(2).squareRoot()
            }
            return value
        }
    }

    private func energy(_ samples: [Float]) -> Double {
        samples.reduce(0.0) { $0 + Double($1) * Double($1) }
    }

    private func int16(_ samples: [Float]) -> [Int16] {
        samples.map { Int16((max(-1, min(1, $0)) * 32767).rounded()) }
    }

    private func seconds(_ samples: [Float], _ range: ClosedRange<Double>) -> [Float] {
        Array(samples[AudioFormat.sampleCount(forSeconds: range.lowerBound)..<AudioFormat.sampleCount(forSeconds: range.upperBound)])
    }

    private func processed(_ input: [Float], initialGain: Float = 1) -> (output: [Float], gain: Float) {
        var output = input
        var gain = InputGain(initialGain: initialGain)
        gain.process(&output)
        return (output, gain.gain)
    }

    @Test func liftsSpeechFromARawCallMicrophone() {
        // About what macOS hands other apps during a call: speech near −60 dBFS over a very low noise floor.
        let input = signal(speechLevel: 0.001, noiseLevel: 0.00003)
        #expect(AudioLevel.isSilent(int16(input))) // skipped as silence without the gain
        let (output, gain) = processed(input)
        #expect(gain > 10)
        #expect(!AudioLevel.isSilent(int16(seconds(output, 2...5))))
        // The pauses after the speech stay silent.
        #expect(AudioLevel.isSilent(int16(seconds(output, 5.2...6))))
    }

    @Test func liftsEvenQuieterSpeech() {
        // The quietest a call has measured: speech peaking near −58 dBFS.
        let input = signal(speechLevel: 0.0004, noiseLevel: 0.00002)
        let (output, gain) = processed(input)
        #expect(gain > 30)
        #expect(!AudioLevel.isSilent(int16(seconds(output, 2...5))))
        #expect(AudioLevel.isSilent(int16(seconds(output, 5.2...6))))
    }

    @Test func aLearntGainLiftsTheFirstWords() {
        let input = signal(speechLevel: 0.0004, noiseLevel: 0.00002)
        let (output, _) = processed(input, initialGain: 20)
        #expect(!AudioLevel.isSilent(int16(seconds(output, 1...2))))
    }

    @Test func leavesNormalSpeechAlone() {
        let input = signal(speechLevel: 0.05, noiseLevel: 0.001)
        let (output, gain) = processed(input)
        #expect(gain == 1)
        #expect(output == input)
    }

    @Test func aSoftFirstWordDoesNotBoostNormalSpeech() {
        // A quiet room, and the dictation starts with soft syllables before the stressed ones.
        let input = signal(speechLevel: 0.05, noiseLevel: 0.0001, syllableLevels: [0.1, 0.1, 0.2, 1, 0.6])
        let (output, gain) = processed(input)
        #expect(gain == 1)
        #expect(energy(output) / energy(input) < 1.01)
    }

    @Test func neverLiftsSilenceIntoSpeech() {
        let input = signal(speechLevel: 0, noiseLevel: 0.0003)
        #expect(AudioLevel.isSilent(int16(processed(input).output)))
        // Not even when starting from a gain learnt earlier.
        #expect(AudioLevel.isSilent(int16(processed(input, initialGain: 20).output)))
    }

    @Test func keepsANoisyFloorUnderTheSilenceThreshold() {
        let input = signal(speechLevel: 0.001, noiseLevel: 0.0002)
        let (output, gain) = processed(input)
        #expect(gain > 1)
        #expect(AudioLevel.isSilent(int16(seconds(output, 5.2...6))))
    }

    @Test func neverClips() {
        var noise = Noise()
        // Quiet speech that suddenly turns loud once the gain is up.
        var samples = signal(speechLevel: 0.001, noiseLevel: 0.00003)
        samples += (0..<AudioFormat.sampleCount(forSeconds: 1)).map { _ in noise.next() * 0.9 }
        var gain = InputGain()
        gain.process(&samples)
        #expect(samples.allSatisfy { abs($0) <= 1 })
    }
}
