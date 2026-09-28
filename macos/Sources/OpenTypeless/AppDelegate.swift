import AppKit
import Combine
import SwiftUI
import TypelessCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let settings = AppSettings.shared
    private let controller = SessionController()
    private let hotkey = HotkeyMonitor.shared
    private let updater = Updater.shared
    private lazy var settingsWindow = SettingsWindowController(controller: controller)
    private var statusItem: NSStatusItem!
    private var popover: StatusPopover!
    private var cancellables: Set<AnyCancellable> = []
    private var menuLanguage: AppLanguage?

    func applicationDidFinishLaunching(_ notification: Notification) {
        setUpMainMenu()
        setUpStatusItem()

        hotkey.hotkey = settings.hotkey
        hotkey.onPress = { [weak self] in self?.controller.hotkeyPressed() }
        hotkey.onRelease = { [weak self] in self?.controller.hotkeyReleased() }
        hotkey.onOtherKey = { [weak self] in self?.controller.otherKeyPressed() }
        hotkey.onEscape = { [weak self] in self?.controller.escapePressed() }
        hotkey.start()

        settings.objectWillChange
            .receive(on: RunLoop.main)
            .sink { [weak self] in
                guard let self else { return }
                if self.hotkey.hotkey != self.settings.hotkey { self.hotkey.hotkey = self.settings.hotkey }
                if self.menuLanguage != AppLanguage.resolved { self.setUpMainMenu() }
            }
            .store(in: &cancellables)

        controller.$state
            .receive(on: RunLoop.main)
            .sink { [weak self] state in
                self?.updateStatusIcon(state)
                if state == .idle { self?.updater.sessionBecameIdle() }
            }
            .store(in: &cancellables)

        NotificationCenter.default.addObserver(forName: .openSettings, object: nil, queue: .main) { [weak self] note in
            let page = (note.userInfo?["page"] as? String).flatMap(SettingsPage.init(rawValue:))
            Task { @MainActor in self?.settingsWindow.show(page: page) }
        }

        controller.refreshModelInfo()
        LaunchAtLogin.enableByDefaultOnce()
        updater.canInstallNow = { [weak self] in self?.controller.state == .idle }
        updater.start()

        // First run: ask for what we need up front, so the first dictation just works.
        if !Permissions.microphoneGranted { Permissions.requestMicrophone { _ in } }
        if !Permissions.accessibilityGranted {
            Permissions.promptAccessibility()
            settingsWindow.show(page: .general)
        } else if !settings.isConfigured(settings.sttProvider) {
            settingsWindow.show(page: .providers)
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        // Opening the app again from Finder/Spotlight shows the settings.
        settingsWindow.show()
        return false
    }

    // MARK: - Main menu

    /// Text fields get ⌘C/⌘V/⌘X/⌘A/⌘Z from the Edit menu's key equivalents. A menu-bar-only app has
    /// no main menu by default, so without this, paste doesn't work in the settings window.
    private func setUpMainMenu() {
        menuLanguage = AppLanguage.resolved
        let main = NSMenu()

        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: L("设置…", "Settings…"), action: #selector(openSettingsAction), keyEquivalent: ",").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: L("关闭窗口", "Close Window"), action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        appMenu.addItem(withTitle: L("退出 OpenTypeless", "Quit OpenTypeless"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: L("编辑", "Edit"))
        edit.addItem(withTitle: L("撤销", "Undo"), action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: L("重做", "Redo"), action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: L("剪切", "Cut"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: L("复制", "Copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: L("粘贴", "Paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: L("全选", "Select All"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        NSApp.mainMenu = main
    }

    @objc private func openSettingsAction() { settingsWindow.show() }

    // MARK: - Status item + popover

    private func setUpStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.target = self
        statusItem.button?.action = #selector(togglePopover)
        statusItem.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        updateStatusIcon(.idle)

        popover = StatusPopover(rootView: MenuPopoverView(
            controller: controller,
            openSettings: { [weak self] page in self?.settingsWindow.show(page: page) },
            close: { [weak self] in self?.popover.close() }
        ))
    }

    @objc private func togglePopover() {
        guard let button = statusItem.button else { return }
        if popover.isShown {
            popover.close()
        } else {
            popover.show(from: button)
        }
    }

    private func updateStatusIcon(_ state: SessionController.State) {
        let name: String
        switch state {
        case .idle: name = "waveform"
        case .recording: name = "waveform.circle.fill"
        case .processing: name = "ellipsis.circle"
        }
        let image = NSImage(systemSymbolName: name, accessibilityDescription: "OpenTypeless")
        image?.isTemplate = true
        statusItem.button?.image = image
    }
}
