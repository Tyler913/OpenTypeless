import AVFoundation
import TypelessCore

/// Captures the default microphone and delivers 16 kHz mono Int16 samples.
///
/// A fresh AVAudioEngine is built per recording, and the engine is rebuilt in place if the audio
/// route changes mid-recording (e.g. AirPods connect), so a long dictation keeps going.
final class AudioRecorder {
    /// Called on the audio thread, in order.
    var onSamples: (([Int16]) -> Void)?
    /// Called on the audio thread with the RMS (0…1) of each delivered block.
    var onLevel: ((Float) -> Void)?
    /// Called on the main thread when capture fails irrecoverably.
    var onFailure: ((Error) -> Void)?

    private var engine: AVAudioEngine?
    private var converter: AVAudioConverter?
    private var configObserver: NSObjectProtocol?
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatInt16,
        sampleRate: Double(AudioFormat.sampleRate),
        channels: 1,
        interleaved: true
    )!
    private(set) var isRecording = false

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
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else { throw RecorderError.noInput }
        guard let converter = AVAudioConverter(from: inputFormat, to: targetFormat) else {
            throw RecorderError.converter
        }
        self.converter = converter

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
        let ratio = targetFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return }

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
        guard error == nil, output.frameLength > 0, let channel = output.int16ChannelData else { return }
        let samples = Array(UnsafeBufferPointer(start: channel[0], count: Int(output.frameLength)))
        onSamples?(samples)
        onLevel?(AudioLevel.rms(samples))
    }
}
