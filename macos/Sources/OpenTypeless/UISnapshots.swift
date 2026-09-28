import AppKit
import SwiftUI
import TypelessCore

/// `OpenTypeless --snapshot-ui <dir>` renders the settings tabs and HUD states to PNGs, for checking
/// the UI without screen-recording permission.
@MainActor
enum UISnapshots {
    static func run(outputDirectory: String) {
        // `--demo`: pretend permissions are granted (pair with OPENTYPELESS_SUPPORT_DIR pointing at sample history).
        Permissions.assumeGranted = CommandLine.arguments.contains("--demo")
        let dir = URL(fileURLWithPath: outputDirectory)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let controller = SessionController()
        let original = UserDefaults.standard.string(forKey: AppLanguage.defaultsKey)
        defer { UserDefaults.standard.set(original, forKey: AppLanguage.defaultsKey) }
        if CommandLine.arguments.contains("--popover-only") {
            checkPopover(controller: controller, dir: dir)
            return
        }

        let requested = CommandLine.arguments.firstIndex(of: "--lang").flatMap { index in
            CommandLine.arguments.indices.contains(index + 1) ? AppLanguage(rawValue: CommandLine.arguments[index + 1]) : nil
        }
        for language in requested.map { [$0] } ?? (live ? [AppLanguage.zh] : [AppLanguage.zh, .en]) {
            UserDefaults.standard.set(language.rawValue, forKey: AppLanguage.defaultsKey)
            for page in SettingsPage.allCases {
                let navigation = SettingsNavigation()
                navigation.page = page
                let url = dir.appendingPathComponent("settings-\(language.rawValue)-\(page.rawValue).png")
                if live {
                    let window = SettingsWindowController.makeWindow(navigation: navigation, controller: controller)
                    window.center()
                    showAndCapture(window, to: url)
                } else {
                    let view = SettingsDetailView(navigation: navigation, controller: controller)
                    save(NSHostingView(rootView: view), size: NSSize(width: 660, height: 620), to: url)
                }
            }
            let popover = MenuPopoverView(controller: controller, openSettings: { _ in }, close: {})
                .background(Color(nsColor: .windowBackgroundColor))
            let host = NSHostingView(rootView: popover)
            save(host, size: host.fittingSize, to: dir.appendingPathComponent("popover-\(language.rawValue).png"))
        }

        let model = HUDModel()
        model.levels = (0..<18).map { i in Float(abs(sin(Double(i) * 0.7))) * 0.8 + 0.1 }
        model.startedAt = Date().addingTimeInterval(-83)
        let phases: [(String, HUDModel.Phase)] = [
            ("hud-recording", .recording),
            ("hud-working", .working),
            ("hud-copied", .copied),
            ("hud-error", .error(L("转写失败，可在菜单栏重试", "Failed — retry from the menu bar"))),
        ]
        for (name, phase) in phases {
            model.phase = phase
            // A busy backdrop, to judge the glass against real content.
            let hud = HUDView(model: model)
            let view = live ? AnyView(hud) : AnyView(hud.background(
                LinearGradient(colors: [.orange, .pink, .blue], startPoint: .leading, endPoint: .trailing)))
            save(NSHostingView(rootView: view), size: NSSize(width: 380, height: 72), to: dir.appendingPathComponent("\(name).png"))
        }
        if live { checkPopover(controller: controller, dir: dir) }
        print("✓ snapshots in \(dir.path)")
    }

    /// Opens the real popover from a real status item and checks it sits under the icon, on screen.
    private static func checkPopover(controller: SessionController, dir: URL) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = NSImage(systemSymbolName: "waveform", accessibilityDescription: nil)
        let popover = StatusPopover(rootView: MenuPopoverView(controller: controller, openSettings: { _ in }, close: {}))
        RunLoop.main.run(until: Date().addingTimeInterval(0.5))
        guard let button = item.button, let buttonWindow = button.window else { return }
        popover.show(from: button)
        RunLoop.main.run(until: Date().addingTimeInterval(1.0))
        let icon = buttonWindow.convertToScreen(button.convert(button.bounds, to: nil))
        if let window = popover.window, let screen = buttonWindow.screen {
            let frame = window.frame
            print(String(format: "icon midX %.0f, bottom %.0f | popover x %.0f–%.0f, y %.0f–%.0f | screen top %.0f, menu bar bottom %.0f",
                         icon.midX, icon.minY, frame.minX, frame.maxX, frame.minY, frame.maxY,
                         screen.frame.maxY, screen.visibleFrame.maxY))
            print("popover under icon:", frame.minX <= icon.midX && icon.midX <= frame.maxX,
                  "| below menu bar:", frame.maxY <= icon.minY + 1,
                  "| fully on screen:", screen.frame.contains(frame))
            capture(window, to: dir.appendingPathComponent("popover-live.png"))
        }
        popover.close()
        NSStatusBar.system.removeStatusItem(item)
    }

    /// `--live`: show windows on screen briefly and capture them with the window server, so Liquid
    /// Glass (which offscreen rendering can't draw) shows up as it really looks.
    static var live: Bool { CommandLine.arguments.contains("--live") }

    private typealias WindowImageFn = @convention(c) (CGRect, UInt32, UInt32, UInt32) -> Unmanaged<CGImage>?
    private static let windowImage: WindowImageFn? = {
        guard let handle = dlopen(nil, RTLD_NOW), let symbol = dlsym(handle, "CGWindowListCreateImage") else { return nil }
        return unsafeBitCast(symbol, to: WindowImageFn.self)
    }()

    private static func capture(_ window: NSWindow, to url: URL) {
        // kCGWindowListOptionIncludingWindow = 1 << 3, kCGWindowImageBoundsIgnoreFraming = 1 << 0
        guard let fn = windowImage,
              let image = fn(.null, 1 << 3, UInt32(window.windowNumber), 1 << 0)?.takeRetainedValue() else {
            print("capture failed for \(url.lastPathComponent)")
            return
        }
        let rep = NSBitmapImageRep(cgImage: image)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    private static func showAndCapture(_ window: NSWindow, to url: URL) {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        RunLoop.main.run(until: Date().addingTimeInterval(0.9))
        capture(window, to: url)
        window.orderOut(nil)
    }

    private static func save(_ view: NSView, size: NSSize, to url: URL, titled: Bool = false) {
        let origin = live
            ? NSPoint(x: (NSScreen.main?.frame.midX ?? 800) - size.width / 2, y: (NSScreen.main?.frame.midY ?? 500) - size.height / 2)
            : NSPoint(x: -10000, y: -10000)
        let window = NSWindow(contentRect: NSRect(origin: origin, size: size),
                              styleMask: titled ? [.titled, .closable, .miniaturizable, .fullSizeContentView] : [.borderless],
                              backing: .buffered, defer: false)
        if titled {
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.toolbar = NSToolbar(identifier: "snapshot")
            window.toolbarStyle = .unified
        } else {
            window.isOpaque = false
            window.backgroundColor = .clear
        }
        window.contentView = view
        if live {
            view.frame = NSRect(origin: .zero, size: window.contentRect(forFrameRect: window.frame).size)
            showAndCapture(window, to: url)
            return
        }
        view.frame = NSRect(origin: .zero, size: size)
        window.orderFrontRegardless()
        view.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.4))
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
        window.orderOut(nil)
    }
}
