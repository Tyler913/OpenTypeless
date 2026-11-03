import AudioToolbox
import Combine
import CoreAudio
import Foundation
import TypelessCore

/// An audio input device, remembered by its UID (stable across reboots and reconnects, unlike the device ID).
struct Microphone: Identifiable, Hashable {
    let id: AudioDeviceID
    let uid: String
    let name: String
    /// A software device (virtual or aggregate), e.g. from a meeting or streaming app, rather than a real mic.
    let isVirtual: Bool

    var label: String { isVirtual ? name + L("（虚拟）", " (virtual)") : name }
}

/// The Mac's audio inputs, from CoreAudio.
enum Microphones {
    static func all() -> [Microphone] {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        let system = AudioObjectID(kAudioObjectSystemObject)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard inputChannels(id) > 0,
                  let uid = string(id, kAudioDevicePropertyDeviceUID),
                  let name = string(id, kAudioObjectPropertyName) else { return nil }
            let transport = uint32(id, kAudioDevicePropertyTransportType) ?? 0
            let isVirtual = transport == UInt32(kAudioDeviceTransportTypeVirtual)
                || transport == UInt32(kAudioDeviceTransportTypeAggregate)
            return Microphone(id: id, uid: uid, name: name, isVirtual: isVirtual)
        }
    }

    /// The system's current default input.
    static var defaultInput: Microphone? {
        guard let id = defaultInputID else { return nil }
        return all().first { $0.id == id }
    }

    /// The device ID of the system's current default input, if there is one.
    static var defaultInputID: AudioDeviceID? {
        guard let id = uint32(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultInputDevice),
              id != AudioObjectID(kAudioObjectUnknown) else { return nil }
        return id
    }

    /// The device with this UID, if it's connected.
    static func deviceID(uid: String) -> AudioDeviceID? {
        all().first { $0.uid == uid }?.id
    }

    /// Calls `handler` on the main queue whenever a device is connected or disconnected, for as long as the app runs.
    static func observeDeviceList(_ handler: @escaping @Sendable () -> Void) {
        var address = address(kAudioHardwarePropertyDevices)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main) { _, _ in handler() }
    }

    /// The device ID of the system's current default output, if there is one.
    static var defaultOutputID: AudioDeviceID? {
        guard let id = uint32(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultOutputDevice),
              id != AudioObjectID(kAudioObjectUnknown) else { return nil }
        return id
    }

    static func name(_ id: AudioDeviceID) -> String {
        string(id, kAudioObjectPropertyName) ?? "device \(id)"
    }

    /// An aggregate device (voice processing can't run on one: it builds its own).
    static func isAggregate(_ id: AudioDeviceID) -> Bool {
        uint32(id, kAudioDevicePropertyTransportType) == UInt32(kAudioDeviceTransportTypeAggregate)
    }

    /// Muted in hardware (or by a mute utility), on its input side. Devices without a mute control never are.
    static func isInputMuted(_ id: AudioDeviceID) -> Bool {
        var address = address(kAudioDevicePropertyMute, kAudioDevicePropertyScopeInput)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr && value != 0
    }

    /// The input volume (0…1), for devices that have one.
    static func inputVolume(_ id: AudioDeviceID) -> Float32? {
        var address = address(kAudioDevicePropertyVolumeScalar, kAudioDevicePropertyScopeInput)
        var value: Float32 = 0
        var size = UInt32(MemoryLayout<Float32>.size)
        return AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    static func nominalSampleRate(_ id: AudioDeviceID) -> Float64 {
        var address = address(kAudioDevicePropertyNominalSampleRate)
        var value: Float64 = 0
        var size = UInt32(MemoryLayout<Float64>.size)
        return AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr ? value : 0
    }

    /// Some process is doing I/O on the device (this one included).
    static func isRunningSomewhere(_ id: AudioDeviceID) -> Bool {
        (uint32(id, kAudioDevicePropertyDeviceIsRunningSomewhere) ?? 0) != 0
    }

    /// The process holding the device exclusively (hog mode), or nil when it's shared.
    static func hogModeOwner(_ id: AudioDeviceID) -> pid_t? {
        var address = address(kAudioDevicePropertyHogMode)
        var value: pid_t = -1
        var size = UInt32(MemoryLayout<pid_t>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, value != -1 else { return nil }
        return value
    }

    private static func address(_ selector: AudioObjectPropertySelector,
                                _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    private static func uint32(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> UInt32? {
        var address = address(selector)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    private static func string(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = address(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    static func inputChannels(_ id: AudioObjectID) -> Int {
        var address = address(kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeInput)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, raw) == noErr else { return 0 }
        let buffers = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return buffers.reduce(0) { $0 + Int($1.mNumberChannels) }
    }
}

/// Listens to a microphone while the user tests it on the General page, and reports how loud it is.
@MainActor
final class MicrophoneTester: ObservableObject {
    /// 0…1, scaled like the recording capsule so normal speech fills most of the bar.
    @Published private(set) var level: Float = 0
    /// A slowly falling peak marker.
    @Published private(set) var peak: Float = 0
    @Published private(set) var isRunning = false
    @Published private(set) var error: String?

    private let recorder = AudioRecorder()

    func start(deviceUID: String?) {
        stop()
        guard Permissions.microphoneGranted else {
            Permissions.requestMicrophone { _ in }
            return
        }
        recorder.deviceUID = deviceUID
        recorder.onLevel = { [weak self] level in
            DispatchQueue.main.async { self?.push(level) }
        }
        do {
            try recorder.start()
            isRunning = true
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }

    func stop() {
        recorder.stop()
        recorder.onLevel = nil
        isRunning = false
        level = 0
        peak = 0
    }

    private func push(_ raw: Float) {
        guard isRunning else { return }
        let scaled = min(1, sqrt(raw) * 3.2)
        level = max(scaled, level * 0.75) // jumps up at once, falls back gently
        peak = max(scaled, peak * 0.97)
    }
}

/// The Mac's microphones and the login item's state, for the General page: read off the main thread, every 1.5 s while
/// the page is open, and published only when something changed. Each read asks coreaudiod about every audio device and
/// the background-task service about the login item, which takes tens of milliseconds (far more with Bluetooth and
/// AirPlay devices about): on the main thread, and again each time the page was redrawn, it held every click up.
@MainActor
final class SystemStatus: ObservableObject {
    static let shared = SystemStatus()

    struct Snapshot: Equatable, Sendable {
        var microphones: [Microphone] = []
        var defaultMicrophone: Microphone?
        var launchAtLogin = false
        var launchNeedsApproval = false

        nonisolated static func read() -> Snapshot {
            let microphones = Microphones.all()
            let defaultID = Microphones.defaultInputID
            return Snapshot(microphones: microphones,
                            defaultMicrophone: defaultID.flatMap { id in microphones.first { $0.id == id } },
                            launchAtLogin: LaunchAtLogin.isEnabled,
                            launchNeedsApproval: LaunchAtLogin.needsApproval)
        }
    }

    @Published private(set) var snapshot = Snapshot()
    /// False until the first read is in, so a chosen microphone isn't called disconnected before the list is known.
    @Published private(set) var isLoaded = false
    private var timer: Timer?
    private var viewers = 0
    private var reading = false
    private var readAgain = false
    /// The login item's state the user just chose, shown until the change has gone through: a read in between would
    /// still find the old one and flip the switch back.
    private var launchChoice: Bool?

    private init() {}

    /// Reads everything again, off the main thread. A read asked for while one is running follows it.
    func refresh() {
        guard !reading else {
            readAgain = true
            return
        }
        reading = true
        Task {
            var fresh = await Task.detached(priority: .userInitiated) { Snapshot.read() }.value
            reading = false
            if let launchChoice { fresh.launchAtLogin = launchChoice }
            if fresh != snapshot { snapshot = fresh }
            if !isLoaded { isLoaded = true }
            if readAgain {
                readAgain = false
                refresh()
            }
        }
    }

    /// Polls while at least one page shows it.
    func setWatching(_ watching: Bool) {
        viewers = max(0, viewers + (watching ? 1 : -1))
        if viewers == 0 {
            timer?.invalidate()
            timer = nil
            return
        }
        refresh()
        guard timer == nil else { return }
        let timer = Timer(timeInterval: 1.5, repeats: true) { _ in
            MainActor.assumeIsolated { SystemStatus.shared.refresh() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// Shows the switch's new position at once, until the next read confirms it.
    func assumeLaunchAtLogin(_ enabled: Bool) {
        launchChoice = enabled
        snapshot.launchAtLogin = enabled
    }

    /// The change asked for by `assumeLaunchAtLogin` has gone through, or failed: what the system says counts again.
    func launchAtLoginChanged() {
        launchChoice = nil
        refresh()
    }

    /// Reads everything now, on this thread: for the UI snapshots, which draw the page at once.
    func readNow() {
        snapshot = Snapshot.read()
        isLoaded = true
    }
}
