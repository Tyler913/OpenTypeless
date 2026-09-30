import AudioToolbox
import AVFoundation
import CoreAudio
import TypelessCore

/// Captures the chosen microphone (or the system default) and delivers 16 kHz mono Int16 samples.
///
/// Capture runs through an input-only HAL output unit (AUHAL) opened on one explicit device, the way
/// browsers and call apps record. AVAudioEngine isn't used for this: its input node shares one I/O unit
/// with the output side and binds to the system default input before a device can be chosen, and while
/// another app holds the microphone (a call, a meeting) it can start "successfully" and deliver nothing.
/// A plain HAL client shares the device with every other app.
///
/// The unit is rebuilt in place when the device goes away, changes format (e.g. a call switches AirPods
/// to a lower sample rate) or, when following the system default, the default input changes, so a long
/// dictation keeps going.
final class AudioRecorder {
    /// Called on the audio thread, in order.
    var onSamples: (([Int16]) -> Void)?
    /// Called on the audio thread with the RMS (0…1) of each delivered block.
    var onLevel: ((Float) -> Void)?
    /// Called on the main thread when capture fails irrecoverably.
    var onFailure: ((Error) -> Void)?
    /// The input to use, by UID; nil (or a device that isn't connected) means the system default.
    var deviceUID: String?

    private var unit: AudioUnit?
    private var converter: AVAudioConverter?
    /// What the unit renders into: mono Float32 at the device's sample rate.
    private var captureBuffer: AVAudioPCMBuffer?
    private var listeners: [(object: AudioObjectID, address: AudioObjectPropertyAddress, block: AudioObjectPropertyListenerBlock)] = []
    private var restartPending = false
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
        try startUnit()
        isRecording = true
    }

    func stop() {
        guard isRecording else { return }
        isRecording = false
        teardown()
    }

    // MARK: - Capture unit

    private func startUnit() throws {
        let chosen = deviceUID.flatMap(Microphones.deviceID(uid:))
        guard let device = chosen ?? Microphones.defaultInputID else { throw RecorderError.noInput }

        var description = AudioComponentDescription(componentType: kAudioUnitType_Output,
                                                    componentSubType: kAudioUnitSubType_HALOutput,
                                                    componentManufacturer: kAudioUnitManufacturer_Apple,
                                                    componentFlags: 0, componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &description) else { throw RecorderError.noInput }
        var created: AudioUnit?
        try check(AudioComponentInstanceNew(component, &created))
        guard let unit = created else { throw RecorderError.noInput }
        self.unit = unit

        do {
            // Input only (element 1 is the input side, element 0 the output side): no output device is touched.
            var on: UInt32 = 1, off: UInt32 = 0
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1,
                                           &on, UInt32(MemoryLayout<UInt32>.size)))
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0,
                                           &off, UInt32(MemoryLayout<UInt32>.size)))
            var deviceID = device
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                           &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size)))

            // The device side of the input element; the unit can't resample input, so the app side keeps its rate.
            var hardware = AudioStreamBasicDescription()
            var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
            try check(AudioUnitGetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 1, &hardware, &size))
            guard hardware.mSampleRate > 0, hardware.mChannelsPerFrame > 0 else { throw RecorderError.noInput }

            // One channel: the unit maps the device's first channel to it, as the converter did with AVAudioEngine.
            guard let captureFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: hardware.mSampleRate,
                                                    channels: 1, interleaved: false) else { throw RecorderError.converter }
            try check(AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1,
                                           captureFormat.streamDescription, UInt32(MemoryLayout<AudioStreamBasicDescription>.size)))
            guard let converter = AVAudioConverter(from: captureFormat, to: targetFormat) else { throw RecorderError.converter }
            self.converter = converter

            var maxFrames: UInt32 = 0
            size = UInt32(MemoryLayout<UInt32>.size)
            _ = AudioUnitGetProperty(unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &maxFrames, &size)
            captureBuffer = AVAudioPCMBuffer(pcmFormat: captureFormat, frameCapacity: max(maxFrames, 8192))

            var callback = AURenderCallbackStruct(inputProc: AudioRecorder.inputCallback,
                                                  inputProcRefCon: Unmanaged.passUnretained(self).toOpaque())
            try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0,
                                           &callback, UInt32(MemoryLayout<AURenderCallbackStruct>.size)))
            try check(AudioUnitInitialize(unit))
            try check(AudioOutputUnitStart(unit))
        } catch {
            teardown()
            throw error
        }
        observe(device: device, followsDefault: chosen == nil)
    }

    private func teardown() {
        for listener in listeners {
            var address = listener.address
            AudioObjectRemovePropertyListenerBlock(listener.object, &address, .main, listener.block)
        }
        listeners = []
        if let unit {
            // Stopping waits for a running input callback to return, so nothing touches the buffers after this.
            AudioOutputUnitStop(unit)
            AudioUnitUninitialize(unit)
            AudioComponentInstanceDispose(unit)
        }
        unit = nil
        converter = nil
        captureBuffer = nil
    }

    private func check(_ status: OSStatus) throws {
        if status != noErr { throw RecorderError.device(status) }
    }

    private static let inputCallback: AURenderCallback = { refCon, flags, timeStamp, bus, frames, _ in
        Unmanaged<AudioRecorder>.fromOpaque(refCon).takeUnretainedValue().render(flags, timeStamp, bus, frames)
    }

    /// Audio thread: pulls the new input from the unit and passes it on.
    private func render(_ flags: UnsafeMutablePointer<AudioUnitRenderActionFlags>, _ timeStamp: UnsafePointer<AudioTimeStamp>,
                        _ bus: UInt32, _ frames: UInt32) -> OSStatus {
        guard let unit, let buffer = captureBuffer, frames <= buffer.frameCapacity else { return noErr }
        let buffers = UnsafeMutableAudioBufferListPointer(buffer.mutableAudioBufferList)
        for index in 0..<buffers.count {
            buffers[index].mDataByteSize = frames * UInt32(MemoryLayout<Float32>.size)
        }
        let status = AudioUnitRender(unit, flags, timeStamp, bus, frames, buffer.mutableAudioBufferList)
        guard status == noErr else { return status }
        buffer.frameLength = frames
        process(buffer)
        return noErr
    }

    // MARK: - Device changes

    /// Rebuilds the unit when its device disappears or changes format, or (following the system default) when the
    /// default input changes.
    private func observe(device: AudioDeviceID, followsDefault: Bool) {
        listen(device, kAudioDevicePropertyDeviceIsAlive)
        listen(device, kAudioDevicePropertyNominalSampleRate)
        listen(device, kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeInput)
        if followsDefault {
            listen(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultInputDevice)
            // The chosen microphone may be plugged back in. (Call apps also add and remove private devices, so the
            // list changing alone isn't a reason to restart.)
            if let uid = deviceUID {
                listen(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDevices) {
                    Microphones.deviceID(uid: uid) != nil
                }
            }
        }
    }

    private func listen(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector,
                        _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal,
                        when shouldRestart: @escaping () -> Bool = { true }) {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            if shouldRestart() { self?.scheduleRestart() }
        }
        if AudioObjectAddPropertyListenerBlock(object, &address, .main, block) == noErr {
            listeners.append((object: object, address: address, block: block))
        }
    }

    /// Changes arrive in bursts (a call reconfigures the device several times), so restart once they settle.
    private func scheduleRestart(attempt: Int = 0) {
        guard isRecording, !restartPending || attempt > 0 else { return }
        restartPending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + (attempt == 0 ? 0.15 : 0.5)) { [weak self] in
            guard let self else { return }
            guard isRecording else {
                restartPending = false
                return
            }
            teardown()
            do {
                try startUnit()
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
