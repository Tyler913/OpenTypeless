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
/// signal, about 30 dB quieter than usual, which the pipeline would skip as silence. `InputGain` lifts it
/// back to a speech level (and leaves normal input alone).
final class AudioRecorder {
    /// Called on the audio thread, in order.
    var onSamples: (([Int16]) -> Void)?
    /// Called on the audio thread with the RMS (0…1) of each delivered block.
    var onLevel: ((Float) -> Void)?
    /// Called on the main thread when capture fails irrecoverably.
    var onFailure: ((Error) -> Void)?
    /// The input to use, by UID; nil (or a device that isn't connected) means the system default.
    var deviceUID: String?
    /// Lift abnormally quiet input (off for the diagnostic probe's raw measurement).
    var appliesGain = true

    private var engine: AVAudioEngine?
    private var converter: AVAudioConverter?
    private var configObserver: NSObjectProtocol?
    /// The converter's output: 16 kHz mono float, so the gain works before rounding to 16-bit.
    private let floatFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: Double(AudioFormat.sampleRate),
        channels: 1,
        interleaved: false
    )!
    /// Audio thread only.
    private var gain = InputGain()
    private var reportedGain = false
    private let log = Logger(subsystem: "local.opentypeless.app", category: "audio")
    private(set) var isRecording = false

    /// The gain currently applied (audio thread; for diagnostics).
    var currentGain: Float { gain.gain }

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
        gain = InputGain()
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
        guard let converter = AVAudioConverter(from: inputFormat, to: floatFormat) else {
            throw RecorderError.converter
        }
        self.converter = converter
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
        if let configObserver { NotificationCenter.default.removeObserver(configObserver) }
        configObserver = nil
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil
        converter = nil
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

    private func process(_ buffer: AVAudioPCMBuffer) {
        guard let converter else { return }
        let ratio = floatFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: floatFormat, frameCapacity: capacity) else { return }

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
        guard error == nil, output.frameLength > 0, let channel = output.floatChannelData else { return }
        var floats = Array(UnsafeBufferPointer(start: channel[0], count: Int(output.frameLength)))
        if appliesGain {
            gain.process(&floats)
            if !reportedGain, gain.gain >= 4 {
                reportedGain = true
                log.debug("quiet input: applying \(Int(20 * log10(self.gain.gain)), privacy: .public) dB of gain")
            }
        }
        let samples = floats.map { Int16((max(-1, min(1, $0)) * 32767).rounded()) }
        onSamples?(samples)
        onLevel?(AudioLevel.rms(samples))
    }
}
