import AVFoundation
import CoreAudio
import CoreMedia
import Foundation

/// `OpenTypeless --mic-probe [seconds]`: records a few seconds through the app's recorder and through the raw capture
/// paths in turn, and reports what each heard, to diagnose a microphone that's silent in some situations (e.g. during a
/// call). Speech normally peaks around −20 dBFS; far below that, the pipeline would skip it as silence.
enum MicProbe {
    @MainActor
    static func run(seconds: Double) async -> Int32 {
        let uid = AppSettings.shared.microphoneUID
        print("OpenTypeless microphone probe — macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
        if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            _ = await AVCaptureDevice.requestAccess(for: .audio)
        }
        print("Microphone permission: \(permission)")
        describeDevices(chosenUID: uid.isEmpty ? nil : uid)
        print("\nKeep talking while each method records for \(format(seconds)) s…\n")

        var results: [(String, Stats)] = []
        let recorded = Stats()
        let recorder = AudioRecorder()
        recorder.deviceUID = uid.isEmpty ? nil : uid
        recorder.onSamples = { samples in recorded.add(samples) }
        do {
            try recorder.start()
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            recorded.note = "\(recorder.inputChannels) ch in, gain ×\(format(Double(recorder.currentGain)))"
            recorder.stop()
        } catch {
            recorded.error = error.localizedDescription
        }
        results.append(("OpenTypeless", recorded))
        report("OpenTypeless", recorded)

        let engine = Stats()
        await recordWithEngine(seconds: seconds, uid: uid, into: engine)
        results.append(("AVAudioEngine", engine))
        report("AVAudioEngine", engine)

        let capture = Stats()
        await recordWithCaptureSession(seconds: seconds, uid: uid, into: capture)
        results.append(("AVCaptureSession", capture))
        report("AVCaptureSession", capture)

        print("\nSummary: " + results.map { "\($0.0): \($0.1.verdict)" }.joined(separator: "; "))
        return 0
    }

    // MARK: - Devices

    private static var permission: String {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return "granted"
        case .denied: return "denied"
        case .restricted: return "restricted"
        case .notDetermined: return "not asked yet"
        @unknown default: return "unknown"
        }
    }

    private static func describeDevices(chosenUID: String?) {
        if let chosenUID {
            if let id = Microphones.deviceID(uid: chosenUID) {
                describe("Chosen input", id)
            } else {
                print("Chosen input: \(chosenUID) (not connected; the default is used)")
            }
        }
        if let id = Microphones.defaultInputID { describe("Default input", id) } else { print("Default input: none") }
        if let id = Microphones.defaultOutputID { print("Default output: \(Microphones.name(id))") }
        let others = Microphones.all().filter { $0.id != Microphones.defaultInputID }.map(\.label)
        if !others.isEmpty { print("Other inputs: \(others.joined(separator: ", "))") }
    }

    private static func describe(_ label: String, _ id: AudioDeviceID) {
        var details = [
            "\(format(Microphones.nominalSampleRate(id) / 1000)) kHz",
            "\(Microphones.inputChannels(id)) ch",
            Microphones.isInputMuted(id) ? "MUTED" : "not muted",
        ]
        if let volume = Microphones.inputVolume(id) { details.append("volume \(Int(volume * 100))%") }
        details.append(Microphones.isRunningSomewhere(id) ? "in use by some app" : "idle")
        if let owner = Microphones.hogModeOwner(id) { details.append("held exclusively by pid \(owner)") }
        if Microphones.isAggregate(id) { details.append("aggregate") }
        print("\(label): \(Microphones.name(id)) — " + details.joined(separator: ", "))
    }

    // MARK: - Other capture paths

    @MainActor
    private static func recordWithEngine(seconds: Double, uid: String, into stats: Stats) async {
        let engine = AVAudioEngine()
        let input = engine.inputNode
        if !uid.isEmpty, var deviceID = Microphones.deviceID(uid: uid), let unit = input.audioUnit {
            AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                 &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size))
        }
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            stats.error = "no input format"
            return
        }
        let channels = (0..<Int(format.channelCount)).map { _ in Stats() }
        input.installTap(onBus: 0, bufferSize: 2048, format: format) { buffer, _ in
            guard let data = buffer.floatChannelData else { return }
            stats.add(UnsafeBufferPointer(start: data[0], count: Int(buffer.frameLength)))
            for (index, channel) in channels.enumerated() {
                channel.add(UnsafeBufferPointer(start: data[index], count: Int(buffer.frameLength)))
            }
        }
        defer {
            if channels.count > 1 {
                stats.note = "per channel: " + channels.enumerated().map { "\($0.offset + 1): \($0.element.peakText)" }.joined(separator: ", ")
            }
        }
        do {
            engine.prepare()
            try engine.start()
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        } catch {
            stats.error = error.localizedDescription
        }
        input.removeTap(onBus: 0)
        engine.stop()
    }

    @MainActor
    private static func recordWithCaptureSession(seconds: Double, uid: String, into stats: Stats) async {
        let device = uid.isEmpty ? AVCaptureDevice.default(for: .audio) : (AVCaptureDevice(uniqueID: uid) ?? AVCaptureDevice.default(for: .audio))
        guard let device else {
            stats.error = "no capture device"
            return
        }
        let session = AVCaptureSession()
        let output = AVCaptureAudioDataOutput()
        let delegate = CaptureDelegate(stats: stats)
        let queue = DispatchQueue(label: "local.opentypeless.mic-probe")
        do {
            let input = try AVCaptureDeviceInput(device: device)
            guard session.canAddInput(input), session.canAddOutput(output) else {
                stats.error = "can't configure the capture session"
                return
            }
            session.addInput(input)
            session.addOutput(output)
            output.setSampleBufferDelegate(delegate, queue: queue)
            session.startRunning()
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            session.stopRunning()
        } catch {
            stats.error = error.localizedDescription
        }
    }

    private final class CaptureDelegate: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
        let stats: Stats
        init(stats: Stats) { self.stats = stats }

        func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
            guard let description = CMSampleBufferGetFormatDescription(sampleBuffer),
                  let format = CMAudioFormatDescriptionGetStreamBasicDescription(description)?.pointee else { return }
            var needed = 0
            CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
                sampleBuffer, bufferListSizeNeededOut: &needed, bufferListOut: nil, bufferListSize: 0,
                blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: nil)
            guard needed > 0 else { return }
            let raw = UnsafeMutableRawPointer.allocate(byteCount: needed, alignment: MemoryLayout<AudioBufferList>.alignment)
            defer { raw.deallocate() }
            let list = raw.bindMemory(to: AudioBufferList.self, capacity: 1)
            var block: CMBlockBuffer?
            guard CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
                sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: list, bufferListSize: needed,
                blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: &block) == noErr else { return }
            let isFloat = format.mFormatFlags & kAudioFormatFlagIsFloat != 0
            for buffer in UnsafeMutableAudioBufferListPointer(list) {
                guard let data = buffer.mData else { continue }
                if isFloat, format.mBitsPerChannel == 32 {
                    let count = Int(buffer.mDataByteSize) / MemoryLayout<Float>.size
                    stats.add(UnsafeBufferPointer(start: data.assumingMemoryBound(to: Float.self), count: count))
                } else if !isFloat, format.mBitsPerChannel == 16 {
                    let count = Int(buffer.mDataByteSize) / MemoryLayout<Int16>.size
                    stats.add(Array(UnsafeBufferPointer(start: data.assumingMemoryBound(to: Int16.self), count: count)))
                }
            }
        }
    }

    // MARK: - Measuring

    /// What one capture method heard. Filled from audio threads.
    final class Stats: @unchecked Sendable {
        private let lock = NSLock()
        private var callbacks = 0
        private var silentCallbacks = 0
        private var samples = 0
        private var peak: Float = 0
        private var sumOfSquares: Double = 0
        var error: String?
        /// Extra detail for the report line.
        var note: String?

        func add(_ values: [Int16]) {
            record(values.count) { index in Float(values[index]) / 32768 }
        }

        func add(_ values: UnsafeBufferPointer<Float>) {
            record(values.count) { index in values[index] }
        }

        private func record(_ count: Int, _ sample: (Int) -> Float) {
            var blockPeak: Float = 0
            var squares: Double = 0
            for index in 0..<count {
                let value = abs(sample(index))
                blockPeak = max(blockPeak, value)
                squares += Double(value * value)
            }
            lock.lock()
            callbacks += 1
            if blockPeak == 0 { silentCallbacks += 1 }
            samples += count
            peak = max(peak, blockPeak)
            sumOfSquares += squares
            lock.unlock()
        }

        var verdict: String {
            lock.lock()
            defer { lock.unlock() }
            if let error { return "failed (\(error))" }
            if callbacks == 0 { return "no audio at all" }
            if peak == 0 { return "digital silence" }
            // Speech normally peaks around −20 dBFS; below −40 it's barely there.
            if peak < 0.01 { return "very quiet (peak \(decibels(peak)) dBFS)" }
            return "audio (peak \(decibels(peak)) dBFS)"
        }

        var line: String {
            lock.lock()
            defer { lock.unlock() }
            let rms = samples > 0 ? Float(sqrt(sumOfSquares / Double(samples))) : 0
            let silent = callbacks > 0 ? 100 * silentCallbacks / callbacks : 0
            return "\(callbacks) blocks, \(silent)% exact zeros, peak \(decibels(peak)) dBFS, RMS \(decibels(rms)) dBFS"
                + (note.map { "; \($0)" } ?? "")
        }

        var peakText: String {
            lock.lock()
            defer { lock.unlock() }
            return "peak \(decibels(peak)) dBFS"
        }

        private func decibels(_ value: Float) -> String {
            value > 0 ? String(format: "%.1f", 20 * log10(value)) : "-inf"
        }
    }

    private static func report(_ name: String, _ stats: Stats) {
        let padded = name.padding(toLength: 18, withPad: " ", startingAt: 0)
        print("\(padded) \(stats.verdict) — \(stats.line)")
    }

    private static func format(_ value: Double) -> String {
        String(format: value == value.rounded() ? "%.0f" : "%.1f", value)
    }
}
