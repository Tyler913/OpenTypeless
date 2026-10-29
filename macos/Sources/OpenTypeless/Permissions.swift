import AppKit
import AVFoundation
import ApplicationServices
import Combine

enum Permissions {
    /// Set by `--snapshot-ui --demo` so README screenshots show a set-up app.
    nonisolated(unsafe) static var assumeGranted = false

    static var microphoneGranted: Bool {
        assumeGranted || AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
    }

    static var microphoneAsked: Bool {
        AVCaptureDevice.authorizationStatus(for: .audio) != .notDetermined
    }

    static func requestMicrophone(_ completion: @escaping (Bool) -> Void) {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: completion(true)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                DispatchQueue.main.async { completion(granted) }
            }
        default: completion(false)
        }
    }

    /// Needed both for the global key listener and for posting ⌘V into other apps.
    static var accessibilityGranted: Bool { assumeGranted || AXIsProcessTrusted() }

    static func promptAccessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }

    static func openAccessibilitySettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
    }

    static func openMicrophoneSettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")
    }

    static func openKeyboardSettings() {
        open("x-apple.systempreferences:com.apple.Keyboard-Settings.extension")
    }

    private static func open(_ string: String) {
        if let url = URL(string: string) { NSWorkspace.shared.open(url) }
    }
}

/// Whether the microphone and Accessibility are allowed, for the "Ready" pill, the popover's banners and the General
/// page. macOS says nothing when the user grants either in System Settings, so it's checked again whenever the app
/// comes to the front, and every 1.5 s while something showing it is open (the settings window or the popover).
@MainActor
final class PermissionsMonitor: ObservableObject {
    static let shared = PermissionsMonitor()

    /// What is showing the permissions, so polling stops once all of them have closed.
    enum Viewer: Hashable { case settingsWindow, popover }

    @Published private(set) var microphoneGranted = Permissions.microphoneGranted
    @Published private(set) var accessibilityGranted = Permissions.accessibilityGranted
    private var viewers: Set<Viewer> = []
    private var timer: Timer?

    private init() {
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { PermissionsMonitor.shared.refresh() }
        }
    }

    /// Reads both permissions now; views only redraw when one has changed.
    func refresh() {
        let microphone = Permissions.microphoneGranted
        if microphone != microphoneGranted { microphoneGranted = microphone }
        let accessibility = Permissions.accessibilityGranted
        if accessibility != accessibilityGranted { accessibilityGranted = accessibility }
    }

    func setWatching(_ watching: Bool, by viewer: Viewer) {
        if watching { viewers.insert(viewer) } else { viewers.remove(viewer) }
        if viewers.isEmpty {
            timer?.invalidate()
            timer = nil
        } else if timer == nil {
            refresh()
            let timer = Timer(timeInterval: 1.5, repeats: true) { _ in
                MainActor.assumeIsolated { PermissionsMonitor.shared.refresh() }
            }
            RunLoop.main.add(timer, forMode: .common)
            self.timer = timer
        } else {
            refresh()
        }
    }
}
