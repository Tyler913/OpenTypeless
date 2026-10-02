import CoreAudio
import Foundation

/// Silences the speakers while recording, so music or a video doesn't play on over the dictation, and puts them
/// back afterwards. Uses the default output's mute switch, or its volume where it has none (some USB and HDMI
/// outputs). Only what this changed is undone: an output that was already muted stays muted.
///
/// Pausing instead isn't possible reliably: macOS only lets Apple's own apps see or control what's playing.
@MainActor
final class OutputMute {
    /// What was changed, to undo: the device by UID (stable across reconnects), and the volume it had when there
    /// was no mute switch. Kept in UserDefaults until undone, so a crash mid-recording doesn't leave the Mac silent.
    private struct Change: Codable {
        let deviceUID: String
        let volume: Float32?
    }

    private static let defaultsKey = "outputMutedWhileRecording"
    private var pending: Task<Void, Never>?

    /// Mutes the default output after `delay` (so the start sound isn't cut off), unless `restore` comes first.
    func mute(after delay: Duration) {
        pending?.cancel()
        pending = Task {
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            Self.muteNow()
        }
    }

    func restore() {
        pending?.cancel()
        pending = nil
        Self.undo()
    }

    /// Undoes a mute a crash or force quit left in place. Called at launch.
    static func restoreLeftover() { undo() }

    private static var saved: Change? {
        get { UserDefaults.standard.data(forKey: defaultsKey).flatMap { try? JSONDecoder().decode(Change.self, from: $0) } }
        set { UserDefaults.standard.set(newValue.flatMap { try? JSONEncoder().encode($0) }, forKey: defaultsKey) }
    }

    private static func muteNow() {
        guard saved == nil, let device = Microphones.defaultOutputID, let uid = uid(of: device) else { return }
        if isSettable(device, kAudioDevicePropertyMute) {
            guard let muted = uint32(device, kAudioDevicePropertyMute), muted == 0 else { return }
            // Saved first: if the app dies in between, unmuting an output that never got muted is harmless.
            saved = Change(deviceUID: uid, volume: nil)
            setUInt32(device, kAudioDevicePropertyMute, 1)
        } else if isSettable(device, kAudioDevicePropertyVolumeScalar),
                  let volume = float32(device, kAudioDevicePropertyVolumeScalar), volume > 0 {
            saved = Change(deviceUID: uid, volume: volume)
            setFloat32(device, kAudioDevicePropertyVolumeScalar, 0)
        }
    }

    private static func undo() {
        guard let change = saved else { return }
        saved = nil
        // The output may have been unplugged meanwhile; then there's nothing left to undo.
        guard let device = device(uid: change.deviceUID) else { return }
        if let volume = change.volume {
            setFloat32(device, kAudioDevicePropertyVolumeScalar, volume)
        } else {
            setUInt32(device, kAudioDevicePropertyMute, 0)
        }
    }

    // MARK: CoreAudio

    private static func address(_ selector: AudioObjectPropertySelector,
                                _ scope: AudioObjectPropertyScope = kAudioDevicePropertyScopeOutput) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    private static func isSettable(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> Bool {
        var address = address(selector)
        var settable: DarwinBoolean = false
        return AudioObjectHasProperty(id, &address)
            && AudioObjectIsPropertySettable(id, &address, &settable) == noErr && settable.boolValue
    }

    private static func uint32(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> UInt32? {
        var address = address(selector)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    private static func float32(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> Float32? {
        var address = address(selector)
        var value: Float32 = 0
        var size = UInt32(MemoryLayout<Float32>.size)
        return AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    private static func setUInt32(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ value: UInt32) {
        var address = address(selector)
        var value = value
        AudioObjectSetPropertyData(id, &address, 0, nil, UInt32(MemoryLayout<UInt32>.size), &value)
    }

    private static func setFloat32(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ value: Float32) {
        var address = address(selector)
        var value = value
        AudioObjectSetPropertyData(id, &address, 0, nil, UInt32(MemoryLayout<Float32>.size), &value)
    }

    private static func uid(of id: AudioObjectID) -> String? {
        var address = address(kAudioDevicePropertyDeviceUID, kAudioObjectPropertyScopeGlobal)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    private static func device(uid: String) -> AudioDeviceID? {
        var address = address(kAudioHardwarePropertyTranslateUIDToDevice, kAudioObjectPropertyScopeGlobal)
        var uid = uid as CFString
        var id = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = withUnsafeMutablePointer(to: &uid) { qualifier in
            AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                       UInt32(MemoryLayout<CFString>.size), qualifier, &size, &id)
        }
        return status == noErr && id != AudioDeviceID(kAudioObjectUnknown) ? id : nil
    }
}
