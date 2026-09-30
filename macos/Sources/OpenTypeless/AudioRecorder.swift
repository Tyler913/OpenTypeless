import AudioToolbox
import AVFoundation
import os
import TypelessCore

/// Captures the chosen microphone (or the system default) and delivers 16 kHz mono Int16 samples.
///
/// A fresh AVAudioEngine is built per recording, and the engine is rebuilt in place if the audio
/// route changes mid-recording (e.g. AirPods connect), so a long dictation keeps going.
///
/// While a call app runs Apple's voice processing, macOS hands other apps the built-in microphone's raw
/// capsules: three channels, about 30 dB quieter than usual. AVAudioConverter turns three channels into one
/// as pure silence, so those are averaged here first, and only then does `InputGain` lift the quiet signal back
/// to a speech level the pipeline doesn't skip as silence. One- and two-channel inputs (every microphone outside a
/// call) get no gain at all: their audio reaches the pipeline exactly as before.
final class AudioRecorder {
    /// Called on the audio thread, in order.
    var onSamples: (([Int16]) -> Void)?
    /// Called on the audio thread with the RMS (0…1) of each delivered block.
    var onLevel: ((Float) -> Void)?
    /// Called on the main thread when capture fails irrecoverably.
    var onFailure: ((Error) -> Void)?
    /// The input to use, by UID; nil (or a device that isn't connected) means the system default.
    var deviceUID: String?
    /// Lift the quiet multi-channel input of a call (off for the diagnostic probe's raw measurement).
    var appliesGain = true

    private var engine: AVAudioEngine?
    private var converter: AVAudioConverter?
    /// Mono at the input's rate, when the input has more channels than the converter can mix down.
    private var mixFormat: AVAudioFormat?
    private var configObserver: NSObjectProtocol?
    /// What the pipeline takes: 16 kHz mono 16-bit, converted to directly from one- and two-channel inputs as always.
    private let int16Format = AVAudioFormat(
        commonFormat: .pcmFormatInt16,
        sampleRate: Double(AudioFormat.sampleRate),
        channels: 1,
        interleaved: true
    )!
    /// The converter's output for a mixed-down input: float, so the gain works before rounding to 16-bit.
    private let floatFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: Double(AudioFormat.sampleRate),
        channels: 1,
        interleaved: false
    )!
    /// Audio thread only; used only while `mixFormat` is set.
    private var gain = InputGain()
    private var reportedGain = false
    /// The gain last reached on a multi-channel input: a later recording in the same call starts there, so its first
    /// words are lifted too.
    private static var learntGain: Float = 20
    private let log = Logger(subsystem: "local.opentypeless.app", category: "audio")
    private(set) var isRecording = false

    /// The gain currently applied (audio thread; for diagnostics).
    var currentGain: Float { gain.gain }
    /// The input's channel count (for diagnostics).
    private(set) var inputChannels: AVAudioChannelCount = 0

    enum RecorderError: LocalizedError {
        case noInput
        case converter

        var errorDescription: String? {
            switch self {
            case .noInput: return L("没有可用的麦克风", "No microphone available")
            case .converter: return L("无法创建音频格式转换器", "Couldn't create the audio converter")
            }
        }
    }

    func start() throws {
        guard !isRecording else { return }
        reportedGain = false
        try startEngine()
        isRecording = true
    }

    func stop() {
        guard isRecording else { return }
        isRecording = false
        teardown()
    }

    private func startEngine() throws {
        let engine = AVAudioEngine()
        let input = engine.inputNode
        if let deviceUID, var deviceID = Microphones.deviceID(uid: deviceUID), let unit = input.audioUnit {
            // Point the engine's input unit at the chosen device before reading its format.
            AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                 &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size))
        }
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else { throw RecorderError.noInput }
        // One or two channels convert as they always have; more are averaged to mono first (see process).
        let mixFormat = inputFormat.channelCount > 2
            ? AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: inputFormat.sampleRate, channels: 1, interleaved: false)
            : nil
        guard let converter = AVAudioConverter(from: mixFormat ?? inputFormat, to: mixFormat == nil ? int16Format : floatFormat) else {
            throw RecorderError.converter
        }
        self.converter = converter
        self.mixFormat = mixFormat
        inputChannels = inputFormat.channelCount
        gain = InputGain(initialGain: mixFormat == nil ? 1 : AudioRecorder.learntGain)
        log.debug("capture started: \(inputFormat.channelCount, privacy: .public) ch at \(inputFormat.sampleRate, privacy: .public) Hz")

        input.installTap(onBus: 0, bufferSize: 2048, format: inputFormat) { [weak self] buffer, _ in
            self?.process(buffer)
        }

        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main
        ) { [weak self] _ in
            self?.handleConfigurationChange()
        }

        engine.prepare()
        try engine.start()
        self.engine = engine
    }

    private func teardown() {
        if mixFormat != nil, gain.gain > 1 { AudioRecorder.learntGain = gain.gain }
        if let configObserver { NotificationCenter.default.removeObserver(configObserver) }
        configObserver = nil
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil
        converter = nil
        mixFormat = nil
    }

    private func handleConfigurationChange() {
        guard isRecording else { return }
        teardown()
        do {
            try startEngine()
        } catch {
            isRecording = false
            onFailure?(error)
        }
    }

    private func process(_ input: AVAudioPCMBuffer) {
        guard let converter else { return }
        let buffer: AVAudioPCMBuffer
        if let mixFormat {
            guard let mono = AudioRecorder.average(input, into: mixFormat) else { return }
            buffer = mono
        } else {
            buffer = input
        }
        let ratio = converter.outputFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: converter.outputFormat, frameCapacity: capacity) else { return }

        var consumed = false
        var error: NSError?
        converter.convert(to: output, error: &error) { _, status in
            if consumed {
                status.pointee = .noDataNow
                return nil
            }
            consumed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, output.frameLength > 0 else { return }
        let count = Int(output.frameLength)
        let samples: [Int16]
        if let channel = output.int16ChannelData {
            samples = Array(UnsafeBufferPointer(start: channel[0], count: count))
        } else if let channel = output.floatChannelData {
            // A mixed-down call microphone: lift it, then round to 16-bit.
            var floats = Array(UnsafeBufferPointer(start: channel[0], count: count))
            if appliesGain {
                gain.process(&floats)
                if !reportedGain, gain.gain >= 4 {
                    reportedGain = true
                    log.debug("quiet input: applying \(Int(20 * log10(self.gain.gain)), privacy: .public) dB of gain")
                }
            }
            samples = floats.map { Int16((max(-1, min(1, $0)) * 32767).rounded()) }
        } else {
            return
        }
        onSamples?(samples)
        onLevel?(AudioLevel.rms(samples))
    }

    /// The average of all of the buffer's float channels, as one channel.
    static func average(_ buffer: AVAudioPCMBuffer, into format: AVAudioFormat) -> AVAudioPCMBuffer? {
        guard let input = buffer.floatChannelData,
              let mono = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: buffer.frameLength),
              let output = mono.floatChannelData?[0] else { return nil }
        let channels = Int(buffer.format.channelCount)
        let frames = Int(buffer.frameLength)
        let interleaved = buffer.format.isInterleaved
        let scale = 1 / Float(channels)
        for frame in 0..<frames {
            var sum: Float = 0
            for channel in 0..<channels {
                sum += interleaved ? input[0][frame * channels + channel] : input[channel][frame]
            }
            output[frame] = sum * scale
        }
        mono.frameLength = buffer.frameLength
        return mono
    }
}
