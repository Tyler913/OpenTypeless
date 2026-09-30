import AudioToolbox
import AVFoundation
import CoreAudio
import os
import TypelessCore

/// Captures the chosen microphone (or the system default) and delivers 16 kHz mono Int16 samples.
///
/// Capture runs through an input-only HAL output unit (AUHAL) opened on one explicit device, the way
/// browsers record. AVAudioEngine isn't used for this: its input node shares one I/O unit with the output
/// side and binds to the system default input before a device can be chosen.
///
/// While a call app (WeChat, FaceTime…) runs Apple's voice processing on the microphone, macOS can hand
/// plain clients like that unit nothing but digital silence, while voice-processing clients still hear the
/// mic. So when the unit delivers exact zeros (or no audio at all) the recorder switches to a voice
/// processing unit of its own, and prefers it for a while afterwards.
///
/// The unit is rebuilt in place when the device goes away or changes format, or, when following the system
/// default, the default input changes, so a long dictation keeps going.
final class AudioRecorder {
    enum Mode: String {
        /// Plain input: the microphone as it is.
        case hal
        /// Apple's voice processing (echo cancellation, noise suppression): still hears the mic during calls.
        case voiceProcessing
    }

    /// Called on the audio thread, in order.
    var onSamples: (([Int16]) -> Void)?
    /// Called on the audio thread with the RMS (0…1) of each delivered block.
    var onLevel: ((Float) -> Void)?
    /// Called on the main thread when capture fails irrecoverably.
    var onFailure: ((Error) -> Void)?
    /// The input to use, by UID; nil (or a device that isn't connected) means the system default.
    var deviceUID: String?
    /// Switch to voice processing when plain capture turns out silent (off for the diagnostic probe).
    var allowsFallback = true

    private(set) var isRecording = false
    private(set) var mode: Mode = .hal
    /// Frames received since the unit started (audio thread).
    private(set) var renderedFrames: UInt64 = 0

    private var unit: AudioUnit?
    private var device: AudioDeviceID = 0
    private var converter: AVAudioConverter?
    /// What the unit renders into: mono at the device's sample rate.
    private var captureBuffer: AVAudioPCMBuffer?
    private var listeners: [(object: AudioObjectID, address: AudioObjectPropertyAddress, block: AudioObjectPropertyListenerBlock)] = []
    private var restartPending = false
    /// The device as it was once capture started, to tell real changes from notification noise.
    private var startedState: DeviceState?
    /// Bumped on every (re)start, so checks scheduled for an older unit are ignored.
    private var generation = 0
    private var voiceProcessingFailed = false

    // Audio thread: consecutive frames of exact zeros, and whether that has been reported.
    private var silentFrames: UInt32 = 0
    private var silenceLimit: UInt32 = 0
    private var silenceReported = false

    /// After a fallback, start with voice processing for a while: the call is probably still going.
    private static var preferVoiceProcessingUntil = Date.distantPast
    private static let preferVoiceProcessingFor: TimeInterval = 5 * 60
    /// A real microphone never delivers this long a run of exact zeros; a silenced client does.
    private static let silenceSeconds = 0.5

    private let log = Logger(subsystem: "local.opentypeless.app", category: "audio")
    private let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatInt16,
        sampleRate: Double(AudioFormat.sampleRate),
        channels: 1,
        interleaved: true
    )!

    enum RecorderError: LocalizedError {
        case noInput
        case converter
        case device(OSStatus)

        var errorDescription: String? {
            switch self {
            case .noInput: return L("没有可用的麦克风", "No microphone available")
            case .converter: return L("无法创建音频格式转换器", "Couldn't create the audio converter")
            case .device(let status): return L("无法打开麦克风（错误 \(status)）", "Couldn't open the microphone (error \(status))")
            }
        }
    }

    func start() throws {
        guard !isRecording else { return }
        voiceProcessingFailed = false
        if allowsFallback, Date() < AudioRecorder.preferVoiceProcessingUntil {
            do {
                try start(mode: .voiceProcessing)
                return
            } catch {
                note("voice processing didn't start (\(error.localizedDescription)); using plain capture")
                voiceProcessingFailed = true
            }
        }
        try start(mode: .hal)
    }

    /// Starts in the given mode (the diagnostic probe uses this directly).
    func start(mode: Mode) throws {
        guard !isRecording else { return }
        try startUnit(mode: mode)
        isRecording = true
    }

    func stop() {
        guard isRecording else { return }
        isRecording = false
        teardown()
    }

    // MARK: - Capture unit

    private func startUnit(mode: Mode) throws {
        let chosen = deviceUID.flatMap(Microphones.deviceID(uid:))
        guard let device = chosen ?? Microphones.defaultInputID else { throw RecorderError.noInput }
        var output: AudioDeviceID = 0
        if mode == .voiceProcessing {
            // Voice processing builds an aggregate of the mic and the speakers itself, so it needs an output and
            // can't run on an aggregate device.
            guard !Microphones.isAggregate(device), let defaultOutput = Microphones.defaultOutputID else { throw RecorderError.noInput }
            output = defaultOutput
        }

        var description = AudioComponentDescription(
            componentType: kAudioUnitType_Output,
            componentSubType: mode == .hal ? kAudioUnitSubType_HALOutput : kAudioUnitSubType_VoiceProcessingIO,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0, componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &description) else { throw RecorderError.noInput }
        var created: AudioUnit?
        try check(AudioComponentInstanceNew(component, &created))
        guard let unit = created else { throw RecorderError.noInput }
        self.unit = unit
        self.device = device
        self.mode = mode
        generation += 1
        renderedFrames = 0
        silentFrames = 0
        silenceReported = false

        do {
            // Element 1 is the input side of an I/O unit, element 0 the output side.
            let deviceSize = UInt32(MemoryLayout<AudioDeviceID>.size)
            if mode == .hal {
                // Input only: no output device is touched.
                var on: UInt32 = 1, off: UInt32 = 0
                try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1,
                                               &on, UInt32(MemoryLayout<UInt32>.size)))
                try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0,
                                               &off, UInt32(MemoryLayout<UInt32>.size)))
                var deviceID = device
                try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                               &deviceID, deviceSize))
            } else {
                // Both sides always run (EnableIO isn't writable here): the mic in, and silence out to the speakers,
                // whose sound is what the echo canceller removes.
                var input = device, speakers = output
                try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 1,
                                               &input, deviceSize))
                try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                               &speakers, deviceSize))
                var silence = AURenderCallbackStruct(inputProc: AudioRecorder.silentOutput, inputProcRefCon: nil)
                try check(AudioUnitSetProperty(unit, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0,
                                               &silence, UInt32(MemoryLayout<AURenderCallbackStruct>.size)))
                // Voice processing turns other audio down by default; the call shouldn't go quiet while dictating.
                var ducking = AUVoiceIOOtherAudioDuckingConfiguration(mEnableAdvancedDucking: false, mDuckingLevel: .min)
                _ = AudioUnitSetProperty(unit, kAUVoiceIOProperty_OtherAudioDuckingConfiguration, kAudioUnitScope_Global, 0,
                                         &ducking, UInt32(MemoryLayout<AUVoiceIOOtherAudioDuckingConfiguration>.size))
            }

            // The device side of the input element; the unit can't resample input, so the app side keeps its rate.
            var hardware = AudioStreamBasicDescription()
            var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
            try check(AudioUnitGetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 1, &hardware, &size))
            guard hardware.mSampleRate > 0, hardware.mChannelsPerFrame > 0 else { throw RecorderError.noInput }

            // One channel: the unit maps the device's first channel to it. Voice processing may refuse float, so it
            // falls back to 16-bit.
            var captureFormat: AVAudioFormat?
            let candidates: [AVAudioCommonFormat] = mode == .hal ? [.pcmFormatFloat32] : [.pcmFormatFloat32, .pcmFormatInt16]
            for common in candidates {
                guard let format = AVAudioFormat(commonFormat: common, sampleRate: hardware.mSampleRate,
                                                 channels: 1, interleaved: false) else { continue }
                if setClientFormat(format, on: unit, mode: mode) == noErr {
                    captureFormat = format
                    break
                }
            }
            guard let captureFormat else { throw RecorderError.converter }
            guard let converter = AVAudioConverter(from: captureFormat, to: targetFormat) else { throw RecorderError.converter }
            self.converter = converter
            silenceLimit = UInt32(hardware.mSampleRate * AudioRecorder.silenceSeconds)

            var maxFrames: UInt32 = 0
            size = UInt32(MemoryLayout<UInt32>.size)
            _ = AudioUnitGetProperty(unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &maxFrames, &size)
            captureBuffer = AVAudioPCMBuffer(pcmFormat: captureFormat, frameCapacity: max(maxFrames, 8192))

            var callback = AURenderCallbackStruct(inputProc: AudioRecorder.inputCallback,
                                                  inputProcRefCon: Unmanaged.passUnretained(self).toOpaque())
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global,
                                           mode == .hal ? 0 : 1, &callback, UInt32(MemoryLayout<AURenderCallbackStruct>.size)))
            try check(AudioUnitInitialize(unit))
            try check(AudioOutputUnitStart(unit))
        } catch {
            teardown()
            throw error
        }
        startedState = DeviceState(device: device)
        observe(device: device, followsDefault: chosen == nil)
        note("capture started: \(Microphones.name(device)), \(mode.rawValue)")
        if mode == .hal { watchForMissingInput() }
    }

    private func setClientFormat(_ format: AVAudioFormat, on unit: AudioUnit, mode: Mode) -> OSStatus {
        let size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        let status = AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1,
                                          format.streamDescription, size)
        guard status == noErr, mode == .voiceProcessing else { return status }
        // The (silent) output side takes the same format.
        return AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0,
                                    format.streamDescription, size)
    }

    private func teardown() {
        for listener in listeners {
            var address = listener.address
            AudioObjectRemovePropertyListenerBlock(listener.object, &address, .main, listener.block)
        }
        listeners = []
        if let unit {
            // Stopping waits for a running callback to return, so nothing touches the buffers after this.
            AudioOutputUnitStop(unit)
            AudioUnitUninitialize(unit)
            AudioComponentInstanceDispose(unit)
        }
        unit = nil
        converter = nil
        captureBuffer = nil
        startedState = nil
    }

    private func check(_ status: OSStatus) throws {
        if status != noErr { throw RecorderError.device(status) }
    }

    private func note(_ text: String) {
        log.debug("\(text, privacy: .public)")
    }

    private static let inputCallback: AURenderCallback = { refCon, flags, timeStamp, bus, frames, _ in
        Unmanaged<AudioRecorder>.fromOpaque(refCon).takeUnretainedValue().render(flags, timeStamp, bus, frames)
    }

    /// What voice processing plays to the speakers: nothing.
    private static let silentOutput: AURenderCallback = { _, flags, _, _, _, ioData in
        flags.pointee.insert(.unitRenderAction_OutputIsSilence)
        if let ioData {
            for buffer in UnsafeMutableAudioBufferListPointer(ioData) {
                if let data = buffer.mData { memset(data, 0, Int(buffer.mDataByteSize)) }
            }
        }
        return noErr
    }

    /// Audio thread: pulls the new input from the unit and passes it on.
    private func render(_ flags: UnsafeMutablePointer<AudioUnitRenderActionFlags>, _ timeStamp: UnsafePointer<AudioTimeStamp>,
                        _ bus: UInt32, _ frames: UInt32) -> OSStatus {
        guard let unit, let buffer = captureBuffer, frames <= buffer.frameCapacity else { return noErr }
        let bytesPerFrame = buffer.format.streamDescription.pointee.mBytesPerFrame
        let buffers = UnsafeMutableAudioBufferListPointer(buffer.mutableAudioBufferList)
        for index in 0..<buffers.count {
            buffers[index].mDataByteSize = frames * bytesPerFrame
        }
        let status = AudioUnitRender(unit, flags, timeStamp, bus, frames, buffer.mutableAudioBufferList)
        guard status == noErr else { return status }
        buffer.frameLength = frames
        renderedFrames &+= UInt64(frames)
        if mode == .hal, allowsFallback, !silenceReported { trackSilence(buffer) }
        process(buffer)
        return noErr
    }

    // MARK: - Falling back to voice processing

    /// Audio thread: counts runs of exact zeros, and reports one that lasts too long.
    private func trackSilence(_ buffer: AVAudioPCMBuffer) {
        if AudioRecorder.isDigitalSilence(buffer) {
            silentFrames &+= buffer.frameLength
        } else {
            silentFrames = 0
        }
        guard silentFrames >= silenceLimit else { return }
        silenceReported = true
        let generation = generation
        DispatchQueue.main.async { [weak self] in
            self?.captureIsSilent(generation: generation, reason: "only digital silence")
        }
    }

    static func isDigitalSilence(_ buffer: AVAudioPCMBuffer) -> Bool {
        let count = Int(buffer.frameLength)
        if let samples = buffer.floatChannelData?[0] {
            for index in 0..<count where samples[index] != 0 { return false }
            return true
        }
        if let samples = buffer.int16ChannelData?[0] {
            for index in 0..<count where samples[index] != 0 { return false }
            return true
        }
        return false
    }

    /// Plain capture that never calls back at all is as silent as one delivering zeros.
    private func watchForMissingInput() {
        let generation = generation
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            guard let self, self.generation == generation, self.renderedFrames == 0 else { return }
            self.captureIsSilent(generation: generation, reason: "no input at all")
        }
    }

    private func captureIsSilent(generation: Int, reason: String) {
        guard isRecording, self.generation == generation, mode == .hal, allowsFallback, !voiceProcessingFailed else { return }
        // A muted microphone is meant to be silent: voice processing would ignore the mute, so leave it be.
        if Microphones.isInputMuted(device) {
            note("capture silent (\(reason)), but the microphone is muted")
            return
        }
        note("capture silent (\(reason)): another app is probably using voice processing; switching to it too")
        teardown()
        do {
            try startUnit(mode: .voiceProcessing)
            AudioRecorder.preferVoiceProcessingUntil = Date().addingTimeInterval(AudioRecorder.preferVoiceProcessingFor)
        } catch {
            note("voice processing didn't start: \(error.localizedDescription)")
            voiceProcessingFailed = true
            do {
                try startUnit(mode: .hal)
            } catch {
                isRecording = false
                onFailure?(error)
            }
        }
    }

    // MARK: - Device changes

    /// What a restart would change: the device to use, and its state.
    private struct DeviceState: Equatable {
        var device: AudioDeviceID
        var alive: Bool
        var sampleRate: Float64
        var inputChannels: Int

        init(device: AudioDeviceID) {
            self.device = device
            alive = Microphones.isAlive(device)
            sampleRate = Microphones.nominalSampleRate(device)
            inputChannels = Microphones.inputChannels(device)
        }
    }

    /// Rebuilds the unit when its device disappears or changes format, or (following the system default) when the
    /// default input changes.
    private func observe(device: AudioDeviceID, followsDefault: Bool) {
        listen(device, kAudioDevicePropertyDeviceIsAlive)
        if mode == .hal {
            // Voice processing adapts to format changes itself (and makes some of its own).
            listen(device, kAudioDevicePropertyNominalSampleRate)
            listen(device, kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeInput)
        }
        if followsDefault {
            listen(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultInputDevice)
            // The chosen microphone may be plugged back in (call apps also add and remove private devices).
            if deviceUID != nil { listen(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDevices) }
        }
    }

    private func listen(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector,
                        _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.scheduleRestart() }
        if AudioObjectAddPropertyListenerBlock(object, &address, .main, block) == noErr {
            listeners.append((object: object, address: address, block: block))
        }
    }

    /// Whether the device to use, or its state, differs from when capture started.
    private var deviceChanged: Bool {
        let current = deviceUID.flatMap(Microphones.deviceID(uid:)) ?? Microphones.defaultInputID
        guard let current, let startedState else { return true }
        if mode == .voiceProcessing {
            // Voice processing reconfigures the device itself; only a different or vanished device matters.
            return current != startedState.device || !Microphones.isAlive(current)
        }
        return DeviceState(device: current) != startedState
    }

    /// Changes arrive in bursts (a call reconfigures the device several times), so restart once they settle,
    /// and only if something that matters changed.
    private func scheduleRestart(attempt: Int = 0) {
        guard isRecording, !restartPending || attempt > 0 else { return }
        restartPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + (attempt == 0 ? 0.15 : 0.5)) { [weak self] in
            guard let self else { return }
            guard isRecording, attempt > 0 || deviceChanged else {
                restartPending = false
                return
            }
            note("device changed; restarting capture")
            teardown()
            do {
                try startUnit(mode: mode)
                restartPending = false
            } catch {
                // A device mid-switch (e.g. Bluetooth changing modes) can briefly report no input: try once more.
                if attempt == 0 {
                    scheduleRestart(attempt: 1)
                } else {
                    restartPending = false
                    isRecording = false
                    onFailure?(error)
                }
            }
        }
    }

    // MARK: - Conversion

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
