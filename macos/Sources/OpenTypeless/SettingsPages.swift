import AppKit
import Combine
import SwiftUI
import TypelessCore
import UniformTypeIdentifiers

// MARK: - General

struct GeneralPage: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject private var permissions = PermissionsMonitor.shared
    /// The microphones and the login item, read off the main thread (see `SystemStatus`). Not `@State` initialised
    /// from CoreAudio and the login-item service: that ran every time the page was rebuilt, on every click.
    @ObservedObject private var status = SystemStatus.shared
    @State private var launchError: String?
    @StateObject private var tester = MicrophoneTester()

    private var launchAtLogin: Bool { status.snapshot.launchAtLogin }
    private var launchNeedsApproval: Bool { status.snapshot.launchNeedsApproval }
    private var microphones: [Microphone] { status.snapshot.microphones }
    private var defaultMicrophone: Microphone? { status.snapshot.defaultMicrophone }

    var body: some View {
        PageScaffold(page: .general) {
            CardSection(title: L("权限", "Permissions")) {
                CardRow(title: L("麦克风", "Microphone"), help: L("用来录下你的语音", "To record your voice")) {
                    PermissionControl(granted: permissions.microphoneGranted) {
                        Permissions.requestMicrophone { _ in permissions.refresh() }
                        if AVAuthorizationDenied.isDenied { Permissions.openMicrophoneSettings() }
                    }
                }
                CardDivider()
                CardRow(title: L("辅助功能", "Accessibility"),
                        help: L("用来监听快捷键，并把文字粘贴到光标处", "To listen for the shortcut and paste text at the cursor")) {
                    PermissionControl(granted: permissions.accessibilityGranted) {
                        Permissions.promptAccessibility()
                        Permissions.openAccessibilitySettings()
                    }
                }
            }

            CardSection(title: L("听写", "Dictation")) {
                CardRow(title: L("恢复剪贴板", "Restore clipboard"),
                        help: L("插入文字后，把剪贴板恢复成原来的内容", "Put your previous clipboard back after inserting"),
                        isOn: $settings.restoreClipboard)
                CardDivider()
                CardRow(title: L("没有地方输入时存进草稿本", "Save to Scratchpad when there's nowhere to type"),
                        help: L("没有能输入文字的地方（比如没有选中输入框，或者已经切到别的应用）时，听写的文字照常复制到剪贴板，同时加进草稿本里当天的笔记。从菜单栏打开草稿本。",
                                "When a dictation has nowhere to go (no text field has focus, or you switched apps), it's copied to the clipboard as usual and also added to today's note in the Scratchpad. Open the Scratchpad from the menu bar."),
                        isOn: $settings.saveToScratchpad)
                CardDivider()
                CardRow(title: L("贴合前后文", "Fit to surrounding text"),
                        subtitle: L("按光标前后的文字调整空格和大小写", "Adjusts spacing and capitals at the cursor"),
                        help: L("插入到已有文字中间时，按光标前后的文字补上缺的空格、把句中的首字母改成小写、去掉会提前结束句子的句号。读不到光标附近的文字时（比如密码框），按原样插入。",
                                "When you dictate into existing text, adds a missing space, lowercases a capital in mid-sentence and drops a full stop that would end the sentence early, going by the text around the cursor. Where that can't be read (a password field, some apps), the text goes in as it is."),
                        isOn: $settings.fitToSurroundingText)
                CardDivider()
                CardRow(title: L("插入后自动回车", "Press Return after inserting"),
                        help: L("文字粘贴进去后自动按一次回车，在聊天框或给 AI 写提示词时直接发送。文字只复制到剪贴板时不会按。",
                                "Presses Return once the text is pasted, so a chat message or AI prompt is sent right away. Not pressed when the text only goes to the clipboard."),
                        isOn: $settings.pressEnterAfterInsert)
                CardDivider()
                CardRow(title: L("说“换行”另起一行", "Say “new line” for a line break"),
                        help: L("单独说“换行”或“新段落”（前后稍作停顿），会换成换行或空一行；其他语言的说法也可以，比如 new line、new paragraph。在一句话中间说到这些词时照原样保留；但在列举里单独说出（“空格，换行，制表符”）时也会变成换行。",
                                "Say “new line” or “new paragraph” on its own, with a short pause before and after, for a line break or a blank line; 换行 / 新段落 and the other languages’ words work too. Said within a sentence, the words stay as they are; said on their own in a list (“spaces, new line, tabs”), they become a break too."),
                        isOn: $settings.spokenLayoutCommands)
                CardDivider()
                CardRow(title: L("提示音", "Sounds"),
                        subtitle: L("开始和结束录音时播放轻提示音", "Soft chime when recording starts and stops"),
                        isOn: $settings.playSounds)
                CardDivider()
                CardRow(title: L("录音时静音", "Mute audio while recording"),
                        help: L("录音期间把扬声器静音，音乐和视频不会打扰你说话，结束后自动恢复。",
                                "Mutes your speakers while you're recording, so music or a video doesn't get in the way, and turns them back on when you stop."),
                        isOn: $settings.muteWhileRecording)
                CardDivider()
                CardRow(title: L("实时预览（测试版）", "Live preview (beta)"),
                        subtitle: L("说话时在录音条上方显示文字", "Shows your words above the capsule as you talk"),
                        help: L("说话时在录音条上方显示识别到的文字，由这台 Mac 本地识别。只是预览：插入的文字仍然来自语音转文字服务商。第一次使用会下载对应语言的语音模型。",
                                "Shows what you're saying above the capsule as you talk, recognised on this Mac. Only a preview: the inserted text still comes from your speech-to-text provider. The first time, macOS downloads a speech model for your language."),
                        isOn: $settings.livePreview)
                CardDivider()
                CardRow(title: L("录音条位置", "Capsule position"),
                        help: L("录音条显示在你正在用的窗口所在的屏幕上。选「光标附近」时，它出现在正在输入的那一行下方（下方放不下就在上方），不会挡住这一行；读不到光标位置的应用里显示在屏幕底部。",
                                "The capsule shows on the screen of the window you're working in. Near the text cursor puts it just below the line you're typing in (above it when there's no room below), never over it; in apps that don't say where the cursor is, it shows at the bottom of the screen.")) {
                    Picker(L("录音条位置", "Capsule position"), selection: $settings.hudPosition) {
                        ForEach(HUDPosition.allCases, id: \.self) { position in
                            Text(position.title).tag(position)
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                CardDivider()
                CardRow(title: L("单次最长录音", "Maximum recording"),
                        help: L("到时间会自动结束并处理", "Stops and processes automatically at the limit")) {
                    ValueStepper(text: L("\(settings.maxRecordingMinutes) 分钟", "\(settings.maxRecordingMinutes) min"),
                                 value: $settings.maxRecordingMinutes, range: 1...60)
                }
            }

            CardSection(title: L("麦克风", "Microphone"), footer: microphoneFooter) {
                CardRow(title: L("输入设备", "Input device"),
                        help: L("有的电脑默认用的是虚拟麦克风，可以在这里选真正的麦克风",
                                "Some Macs default to a virtual device; pick your real microphone here")) {
                    Picker(L("输入设备", "Input device"), selection: $settings.microphoneUID) {
                        Text(L("跟随系统", "System default") + (defaultMicrophone.map { L("（\($0.name)）", " (\($0.name))") } ?? "")).tag("")
                        ForEach(microphones) { Text($0.label).tag($0.uid) }
                        if !settings.microphoneUID.isEmpty, !microphones.contains(where: { $0.uid == settings.microphoneUID }) {
                            // Until the first read is in, the chosen one isn't known to be missing.
                            Text(status.isLoaded ? L("未连接的设备", "Disconnected device") : "").tag(settings.microphoneUID)
                        }
                    }
                    .labelsHidden()
                    .frame(maxWidth: 240)
                }
                CardDivider()
                CardRow(title: L("测试麦克风", "Test microphone"),
                        subtitle: tester.isRunning ? L("说几句话，音量条应该随你的声音起伏", "Say something: the bar should rise and fall with your voice") : nil) {
                    HStack(spacing: 10) {
                        if tester.isRunning { LevelMeter(level: tester.level, peak: tester.peak).frame(width: 130) }
                        Button(tester.isRunning ? L("停止", "Stop") : L("测试", "Test")) {
                            if tester.isRunning { tester.stop() } else { tester.start(deviceUID: selectedMicrophoneUID) }
                        }
                        .controlSize(.small)
                    }
                }
                CardDivider()
                CardRow(title: L("预热麦克风", "Keep microphone ready"),
                        subtitle: L("按下快捷键立刻开始录音", "Recording starts the instant you press the key"),
                        help: L("按下快捷键立刻开始录音，并带上按键前的一小段，第一个字不会被吞掉（AirPods 这类耳机尤其明显）。麦克风会一直开着，蓝牙耳机会切到通话模式，音乐音质会下降。",
                                "Recording starts the instant you press the key and includes the moment before it, so the first word isn't clipped (noticeable with AirPods). The microphone stays on, and Bluetooth headphones switch to call mode, which lowers music quality."),
                        isOn: $settings.keepMicrophoneWarm)
            }
            .onChange(of: settings.microphoneUID) {
                if tester.isRunning { tester.start(deviceUID: selectedMicrophoneUID) }
            }

            CardSection(title: L("偏好", "Preferences"), footer: launchFooter) {
                CardRow(title: L("界面语言", "Language")) {
                    Picker(L("界面语言", "Language"), selection: $settings.appLanguage) {
                        Text(L("跟随系统", "System")).tag(AppLanguage.system)
                        ForEach(AppLanguage.supported) { language in
                            Text(language.nativeName).tag(language)
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                CardDivider()
                CardRow(title: L("登录时自动启动", "Open at login"),
                        help: L("开机后在菜单栏待命", "Waits in the menu bar after you log in"),
                        isOn: Binding(
                            get: { launchAtLogin },
                            set: { newValue in
                                // Registering asks the background-task service, which can take a while: not on the main thread.
                                status.assumeLaunchAtLogin(newValue)
                                Task {
                                    let failure = await Task.detached(priority: .userInitiated) { () -> String? in
                                        do {
                                            try LaunchAtLogin.set(newValue)
                                            return nil
                                        } catch {
                                            return error.localizedDescription
                                        }
                                    }.value
                                    launchError = failure
                                    status.launchAtLoginChanged()
                                }
                            }
                        ))
                if launchAtLogin {
                    CardDivider()
                    CardRow(title: L("自动启动时打开主页", "Show Home when opened at login"),
                            help: L("关闭时开机后只在菜单栏待命；手动打开应用总会显示主页",
                                    "Off: it waits in the menu bar after you log in. Opening the app yourself always shows Home"),
                            isOn: $settings.showHomeAtLogin)
                }
                CardDivider()
                CardRow(title: L("你的打字速度", "Your typing speed"),
                        help: L("用来计算节省的时间（默认每分钟 100 字）", "Used to work out the time saved (100 wpm by default)") + "\n\n"
                            + L("「节省的时间」= 按这个速度打出同样的字所需的时间 − 实际说话的时间。",
                                "“Time saved” is how long typing the same words at this speed would take, minus the time you spent talking.")) {
                    ValueStepper(text: L("\(settings.typingWordsPerMinute) 字/分钟", "\(settings.typingWordsPerMinute) wpm"),
                                 value: $settings.typingWordsPerMinute, range: 10...300, step: 5)
                }
            }

            UpdatesSection(settings: settings)
        }
        .onAppear { status.setWatching(true) }
        .onDisappear {
            status.setWatching(false)
            tester.stop()
        }
    }

    private var selectedMicrophoneUID: String? { settings.microphoneUID.isEmpty ? nil : settings.microphoneUID }

    private var microphoneFooter: String? {
        if let error = tester.error { return error }
        guard status.isLoaded else { return nil }
        guard !settings.microphoneUID.isEmpty else {
            return defaultMicrophone?.isVirtual == true
                ? L("系统默认的输入是虚拟设备，如果听写没有声音，请在上面选择真正的麦克风。",
                    "The system default input is a virtual device. If dictations come out empty, pick your real microphone above.")
                : nil
        }
        guard let chosen = microphones.first(where: { $0.uid == settings.microphoneUID }) else {
            return L("选中的麦克风没有连接，暂时使用系统默认输入。", "The chosen microphone isn't connected, so the system default is used until it's back.")
        }
        return chosen.isVirtual
            ? L("这是虚拟设备，如果听写没有声音，请换成真正的麦克风。", "This is a virtual device. If dictations come out empty, pick a real microphone.")
            : nil
    }

    private var launchFooter: String? {
        if let launchError { return L("无法修改：", "Couldn't change it: ") + launchError }
        if launchNeedsApproval {
            return L("需要在「系统设置 → 通用 → 登录项」中允许 OpenTypeless。",
                     "Allow OpenTypeless in System Settings → General → Login Items.")
        }
        return nil
    }
}

/// Current version, update status and the one action that fits it, plus the automatic-check switch.
private struct UpdatesSection: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject var updater = Updater.shared

    var body: some View {
        CardSection(title: L("更新", "Updates"), footer: updater.manualReason) {
            CardRow(title: "OpenTypeless \(updater.currentVersionText)", subtitle: status) {
                action
            }
            if let update = updater.update {
                CardDivider()
                CardRow(title: L("\(update.version) 更新内容", "What’s new in \(update.version)"),
                        subtitle: update.notes.isEmpty ? nil : String(update.notes.prefix(280))) {
                    HStack(spacing: 8) {
                        if updater.canSkip {
                            Button(L("跳过此版本", "Skip this version")) { updater.skipUpdate() }.controlSize(.small)
                        }
                        Button(L("发布说明", "Release notes")) { NSWorkspace.shared.open(update.pageURL) }.controlSize(.small)
                    }
                }
            }
            CardDivider()
            CardRow(title: L("自动检查更新", "Check automatically"),
                    help: L("每天一次，在后台下载，由你决定何时安装", "Once a day. Downloads in the background; installs when you choose"),
                    isOn: $settings.autoCheckUpdates)
        }
    }

    private var status: String {
        let version = updater.update?.version.description ?? ""
        switch updater.phase {
        case .idle:
            if updater.isDevelopmentBuild { return L("开发版本不会检查更新", "Development builds don’t check for updates") }
            return updater.lastChecked.map { L("上次检查：", "Last checked ") + $0.shortStamp } ?? L("还没有检查过", "Not checked yet")
        case .checking: return L("正在检查…", "Checking…")
        case .upToDate: return L("已是最新版本", "You’re up to date")
        case .available: return L("有新版本 \(version)", "Version \(version) is available")
        case let .downloading(progress): return L("正在下载 \(version)… \(Int(progress * 100))%", "Downloading \(version)… \(Int(progress * 100))%")
        case .ready:
            return updater.installPending
                ? L("这次听写结束后自动重启并更新", "Restarts to update when this dictation finishes")
                : L("\(version) 已下载，重启即可完成更新", "\(version) is downloaded. Restart to finish updating")
        case .installing: return L("正在重启…", "Restarting…")
        case let .failed(message): return message
        }
    }

    @ViewBuilder private var action: some View {
        switch updater.phase {
        case .checking, .downloading, .installing:
            ProgressView().controlSize(.small)
        case .ready:
            Button(L("重启并更新", "Restart to update")) { updater.installAndRelaunch() }
                .controlSize(.small)
                .buttonStyle(.borderedProminent)
                .disabled(updater.installPending)
        case .available:
            if let update = updater.update {
                Button(L("前往下载…", "Download…")) { NSWorkspace.shared.open(update.pageURL) }.controlSize(.small)
            }
        case .idle, .upToDate, .failed:
            if !updater.isDevelopmentBuild {
                Button(L("检查更新", "Check now")) { updater.checkNow() }.controlSize(.small)
            }
        }
    }
}

/// A number and the stepper that changes it, side by side (a form would otherwise put the number at the row's start).
private struct ValueStepper: View {
    let text: String
    @Binding var value: Int
    let range: ClosedRange<Int>
    var step = 1

    var body: some View {
        HStack(spacing: 6) {
            Text(text).monospacedDigit()
            Stepper(text, value: $value, in: range, step: step).labelsHidden()
        }
        .fixedSize()
    }
}

private enum AVAuthorizationDenied {
    static var isDenied: Bool { !Permissions.microphoneGranted && Permissions.microphoneAsked }
}

private struct PermissionControl: View {
    let granted: Bool
    let action: () -> Void

    /// Nothing once granted: only a missing permission needs a control.
    var body: some View {
        if !granted {
            Button(L("授权…", "Grant…"), action: action).controlSize(.small)
        }
    }
}

// MARK: - Shortcut

/// Records a new hotkey from the settings window. Local event monitors run on the main thread.
final class HotkeyRecorder: ObservableObject {
    @Published var isRecording = false
    @Published var message: String?
    var onRecorded: ((Hotkey) -> Void)?
    /// False for an action's shortcut, which fires as its keys go down: a modifier on its own would fire with every
    /// other shortcut that uses it.
    var allowsModifierOnly = true

    private var monitor: Any?
    private var observers: [NSObjectProtocol] = []
    private var pendingModifier: Int64?
    private var sawKeyDown = false

    func start() {
        guard !isRecording else { return }
        isRecording = true
        message = nil
        pendingModifier = nil
        HotkeyMonitor.shared.isSuspended = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { [weak self] event in
            self?.handle(event) ?? event
        }
        // The dictation shortcut is off while recording, so recording mustn't outlive the settings window (whose page
        // also stops it on disappearing), nor go on while another app is in front, where no key would ever reach it.
        let center = NotificationCenter.default
        observers = [center.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.stop()
        }]
        if let window = NSApp.keyWindow {
            observers.append(center.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self] _ in
                self?.stop()
            })
        }
    }

    func stop() {
        // A page has two recorders (the dictation hotkey's, the actions'): stopping the idle one mustn't switch the
        // dictation shortcut back on while the other is recording.
        guard isRecording else { return }
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
        isRecording = false
        HotkeyMonitor.shared.isSuspended = false
    }

    private static func nsFlag(for keyCode: Int64) -> NSEvent.ModifierFlags {
        switch keyCode {
        case 54, 55: return .command
        case 58, 61: return .option
        case 59, 62: return .control
        case 56, 60: return .shift
        case 63: return .function
        default: return []
        }
    }

    private func handle(_ event: NSEvent) -> NSEvent? {
        let code = Int64(event.keyCode)
        if event.type == .flagsChanged {
            guard Hotkey.modifierKeys[code] != nil else { return nil }
            if event.modifierFlags.contains(Self.nsFlag(for: code)) {
                pendingModifier = code
                sawKeyDown = false
            } else if pendingModifier == code, !sawKeyDown {
                if allowsModifierOnly {
                    finish(Hotkey(keyCode: code, isModifierOnly: true))
                } else {
                    message = ShortcutConflict.modifierOnly.message
                }
            }
            return nil
        }

        sawKeyDown = true
        let mods = event.modifierFlags.intersection([.command, .option, .control, .shift])
        if code == 53, mods.isEmpty { stop(); return nil }
        if mods.isEmpty, Hotkey.functionKeys[code] == nil {
            message = allowsModifierOnly
                ? L("单独的字母键会影响正常打字，请搭配 ⌃ ⌥ ⇧ ⌘ 使用，或者只按一个修饰键 / F1–F20。",
                    "A plain key would break normal typing — combine it with ⌃ ⌥ ⇧ ⌘, or press a single modifier / F1–F20.")
                : L("单独的字母键会影响正常打字，请搭配 ⌃ ⌥ ⇧ ⌘ 使用，或者用 F1–F20。",
                    "A plain key would break normal typing — combine it with ⌃ ⌥ ⇧ ⌘, or use F1–F20.")
            return nil
        }
        var flags: CGEventFlags = []
        if mods.contains(.command) { flags.insert(.maskCommand) }
        if mods.contains(.option) { flags.insert(.maskAlternate) }
        if mods.contains(.control) { flags.insert(.maskControl) }
        if mods.contains(.shift) { flags.insert(.maskShift) }
        finish(Hotkey(keyCode: code, modifiers: flags.rawValue, isModifierOnly: false,
                      keyLabel: Hotkey.label(forKeyCode: code, characters: event.charactersIgnoringModifiers)))
        return nil
    }

    private func finish(_ hotkey: Hotkey) {
        stop()
        onRecorded?(hotkey)
    }
}

struct ShortcutPage: View {
    @ObservedObject var settings: AppSettings
    @StateObject private var recorder = HotkeyRecorder()
    /// Records the actions' shortcuts; `recordingAction` says whose.
    @StateObject private var actionRecorder = HotkeyRecorder()
    @State private var recordingAction: ShortcutAction?

    /// A warning when the dictation hotkey takes the send key's presses (see `SendKey.isTaken`).
    private var sendKeyWarning: String? {
        let hotkey = settings.hotkey
        guard settings.sendKey.isTaken(byHotkeyKey: hotkey.keyCode, isModifierOnly: hotkey.isModifierOnly, codes: SendKey.codes) else {
            return nil
        }
        return L("听写快捷键也用到了这个键，录音时会先被当作听写快捷键，发送键就不起作用了。请换一个发送键或快捷键。",
                 "Your dictation shortcut uses this key too, and while recording it's read as the shortcut first, so the send key won't work. Pick another send key or shortcut.")
    }

    /// How the shortcut works, and what it can be.
    private var hotkeyHelp: String {
        [L("按住快捷键说话，松开后自动整理并插入到光标处", "Hold the shortcut while you speak; release to clean up and insert"),
         L("轻点一下：免手持", "Tap once: hands-free") + "\n" + L("适合长段口述，说完再按一次结束", "For long dictation — press again when done"),
         L("Esc 取消", "Esc to cancel") + "\n" + L("录音或处理过程中随时取消", "Cancel any time while recording or processing"),
         L("可以是单个修饰键（如 Fn、右 ⌘），也可以是组合键（如 ⌥ Space、F5）", "A single modifier (Fn, right ⌘…) or a combination (⌥ Space, F5…)")]
            .joined(separator: "\n\n")
    }

    var body: some View {
        let hotkey = settings.hotkey
        PageScaffold(page: .shortcut) {
            CardSection(title: L("听写快捷键", "Dictation shortcut"), help: hotkeyHelp) {
                HStack(spacing: 14) {
                    if recorder.isRecording {
                        RecordingPlaceholder()
                    } else {
                        KeyCaps(caps: hotkey.keyCaps, large: true)
                    }
                    VStack(alignment: .leading, spacing: 2) {
                        Text(recorder.isRecording ? L("请按下新的快捷键…", "Press the new shortcut…") : L("按住说话", "Hold to talk"))
                            .font(.system(size: 13))
                        if !recorder.isRecording {
                            Text(L("轻点一下进入免手持 · Esc 取消", "Tap once for hands-free · Esc to cancel"))
                                .font(.system(size: 11.5))
                                .foregroundStyle(.secondary)
                        }
                    }
                    Spacer(minLength: 8)
                    if recorder.isRecording {
                        Button(L("取消", "Cancel")) { recorder.stop() }.controlSize(.small)
                    } else {
                        Button(L("录制快捷键", "Record shortcut")) {
                            actionRecorder.stop()
                            recorder.onRecorded = { recorded in
                                // An action's keys would start a dictation instead, and the action would never fire.
                                if !recorded.isModifierOnly,
                                   let taken = settings.shortcutMap.action(keyCode: recorded.chord.keyCode,
                                                                           modifiers: recorded.chord.modifiers) {
                                    recorder.message = ShortcutConflict.action(taken).message
                                } else {
                                    settings.hotkey = recorded
                                }
                            }
                            recorder.start()
                        }
                        .controlSize(.small)
                    }
                }
                .rowPadding(.all, 14)
                if let message = recorder.message {
                    Text(message)
                        .font(.system(size: 11.5))
                        .foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                        .rowPadding(.horizontal, 14)
                        .rowPadding(.bottom, 10)
                }
                CardDivider()
                // The common choices, one click each; a recorded shortcut selects none of them.
                Picker(L("听写快捷键", "Dictation shortcut"), selection: Binding<Int>(
                    get: { Hotkey.presets.firstIndex(of: hotkey) ?? -1 },
                    set: { index in
                        guard Hotkey.presets.indices.contains(index) else { return }
                        recorder.stop()
                        settings.hotkey = Hotkey.presets[index]
                    }
                )) {
                    ForEach(Hotkey.presets.indices, id: \.self) { index in
                        Text(Hotkey.presets[index].displayName).tag(index)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .rowPadding(.all, 10)
            }

            CommandModeSection(settings: settings)

            CardSection(title: L("说完即发送", "Finish and send"), footer: sendKeyWarning) {
                CardRow(title: L("发送键", "Send key"),
                        subtitle: L("结束听写，文字插入后自动发送", "Ends the dictation, then sends the text"),
                        help: L("录音时按下它会结束听写，文字插入后再在同一个应用里按一次这个键把它发出去。免手持时可以一步说完并发送。",
                                "Press it while recording to end the dictation; once the text is in, the same key is pressed in that app to send it. Hands-free, that ends and sends in one go.")
                            + "\n\n" + L("只在文字粘贴进录音时所在的应用后才按；文字只复制到剪贴板、按了 Esc 或锁屏时不会发送。",
                                          "Only pressed once the text is pasted into the app you were dictating in: not when it only goes to the clipboard, after Esc, or once the screen locks.")) {
                    Picker(L("发送键", "Send key"), selection: $settings.sendKey) {
                        ForEach(SendKey.allCases, id: \.self) { key in
                            Text(key.title).tag(key)
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            }

            CardSection(title: L("更多快捷键", "More shortcuts"),
                        help: L("在两次听写之间按下时生效。可以是 ⌃ ⌥ ⇧ ⌘ 组合键（如 ⌃⌘V），也可以是 F1–F20。",
                                "They work between dictations. Use a combination with ⌃ ⌥ ⇧ ⌘ (⌃⌘V…) or F1–F20.")) {
                ForEach(Array(ShortcutAction.allCases.enumerated()), id: \.element) { index, action in
                    if index > 0 { CardDivider() }
                    actionRow(action)
                }
            }
            if let message = actionRecorder.message {
                Banner(symbol: "exclamationmark.triangle.fill", color: .orange, text: message) { EmptyView() }
            }

            if hotkey == .fn {
                Banner(symbol: "globe", color: .blue, text: L("轻点 Fn 可能会弹出表情面板", "Tapping Fn may open the emoji picker")) {
                    HStack(spacing: 8) {
                        HelpTip(text: L("建议把「系统设置 → 键盘 → 按下 🌐 键时」设为「不执行任何操作」，避免轻点 Fn 时弹出表情面板。",
                                        "Set System Settings → Keyboard → “Press 🌐 key to” → “Do Nothing”, so tapping Fn doesn’t open the emoji picker."))
                        Button(L("打开键盘设置", "Keyboard Settings")) { Permissions.openKeyboardSettings() }.controlSize(.small)
                    }
                }
            }
            if hotkey.shadowsCommonShortcut
                || ShortcutAction.allCases.contains(where: { settings.shortcut(for: $0)?.shadowsCommonShortcut == true }) {
                Banner(symbol: "exclamationmark.triangle.fill", color: .orange,
                       text: L("这个组合会在所有应用里覆盖同名的系统快捷键。", "This combination overrides the same shortcut in every app.")) {
                    EmptyView()
                }
            }
        }
        .onDisappear {
            recorder.stop()
            actionRecorder.stop()
        }
    }

    /// An action, its shortcut, and buttons to record or clear it.
    private func actionRow(_ action: ShortcutAction) -> some View {
        CardRow(title: action.title, help: action.subtitle) {
            HStack(spacing: 8) {
                if actionRecorder.isRecording, recordingAction == action {
                    Text(L("请按下新的快捷键…", "Press the new shortcut…")).font(.system(size: 12)).foregroundStyle(.secondary)
                    Button(L("取消", "Cancel")) { actionRecorder.stop() }.controlSize(.small)
                } else if let shortcut = settings.shortcut(for: action) {
                    KeyCaps(caps: shortcut.keyCaps)
                    Button(L("更改", "Change")) { record(action) }.controlSize(.small)
                    Button { settings.setShortcut(nil, for: action) } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                        .help(L("清除快捷键", "Clear shortcut"))
                        .accessibilityLabel(L("清除快捷键", "Clear shortcut"))
                } else {
                    Button(L("录制快捷键", "Record shortcut")) { record(action) }.controlSize(.small)
                }
            }
        }
    }

    /// Records `action`'s shortcut; one that clashes (see `ShortcutConflict`) is refused, with the reason shown.
    private func record(_ action: ShortcutAction) {
        recorder.stop()
        recordingAction = action
        actionRecorder.allowsModifierOnly = false
        actionRecorder.onRecorded = { recorded in
            if let conflict = settings.shortcutMap.conflict(recorded.chord, for: action, dictation: settings.hotkey.chord,
                                                            escapeKeyCode: 53) {
                actionRecorder.message = conflict.message
            } else {
                settings.setShortcut(recorded, for: action)
            }
        }
        actionRecorder.start()
    }
}

/// Command mode: on or off, and the key that, pressed while the dictation shortcut is held, makes the recording an
/// instruction for the text selected in the app in front (see `SelectionCommand`).
private struct CommandModeSection: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        let chord = settings.commandChord
        CardSection(title: L("用语音编辑选中的文字", "Edit selected text by voice"),
                    help: L("选中任意应用里的文字，按住听写快捷键时再按一下 \(chord.label)，说出怎么改（“正式一点”“翻译成英文”“修正语法”），松开后选中的文字就会被替换，可以用撤销恢复。问关于它的问题（“这是什么意思”），答案会显示并复制。没有选中文字时什么都不会改；密码框和终端不会被读取。",
                            "Select text in any app, hold the dictation shortcut and press \(chord.label) too, and say how to change it (“more formal”, “translate to English”, “fix the grammar”): when you let go, the selection is replaced, and Undo brings it back. Ask about it (“what does this mean”) and the answer is shown and copied. Nothing changes when no text is selected; password fields and terminals are never read.")) {
            CardRow(title: L("编辑模式", "Command mode"),
                    subtitle: L("按住听写快捷键时再按一下 \(chord.label)，这次说的话会用来修改选中的文字",
                                "Hold the dictation shortcut and press \(chord.label) too: what you say edits the selected text"),
                    isOn: $settings.commandModeEnabled)
            if settings.commandModeEnabled {
                CardDivider()
                CardRow(title: L("切换键", "Switch key"),
                        subtitle: settings.commandChordClashes ? L("和翻译模式的切换键相同，请换一个", "Translation mode uses this key too — pick another") : nil,
                        help: L("按住听写快捷键时按下它（左右两边都可以）", "Pressed while you hold the dictation shortcut (either side)")) {
                    Picker(L("切换键", "Switch key"), selection: $settings.commandChord) {
                        ForEach(TranslationChord.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            }
        }
    }
}

private struct RecordingPlaceholder: View {
    @State private var pulse = false

    var body: some View {
        RoundedRectangle(cornerRadius: 10, style: .continuous)
            .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
            .frame(width: 96, height: 38)
            .overlay(Circle().fill(.red).frame(width: 10, height: 10).opacity(pulse ? 1 : 0.3))
            .onAppear { withAnimation(.easeInOut(duration: 0.7).repeatForever()) { pulse = true } }
    }
}

/// Lets a view switch between two button styles at runtime.
struct AnyPrimitiveButtonStyle: PrimitiveButtonStyle {
    private let make: (Configuration) -> AnyView

    init<S: PrimitiveButtonStyle>(_ style: S) {
        make = { AnyView(style.makeBody(configuration: $0)) }
    }

    func makeBody(configuration: Configuration) -> some View { make(configuration) }
}

// MARK: - Providers

/// The providers that are set up (in use, or with a key or address of their own), and an Add menu with the rest, grouped
/// by what they do, so the page stays short however many providers there are.
struct ProvidersPage: View {
    @ObservedObject var settings: AppSettings
    @State private var expanded: Set<ProviderID> = []
    /// Shown since the page opened, so a provider whose key was just cleared doesn't vanish under the pointer.
    @State private var shown: Set<ProviderID> = []

    var body: some View {
        PageScaffold(page: .providers,
                     help: L("填写 API Key。语音转文字和文字整理可以使用不同的服务商。",
                             "Add API keys. Speech-to-text and clean-up can use different providers.")
                         + "\n\n" + L("这里只列出已经设置的服务商，其他的在「添加服务商」里。",
                                       "Only the providers you've set up are listed; add others with Add provider.")) {
            if !available.isEmpty { addMenu }
        } content: {
            ForEach(listed) { id in
                ProviderCard(settings: settings, id: id, expanded: Binding(
                    get: { expanded.contains(id) },
                    set: { if $0 { expanded.insert(id) } else { expanded.remove(id) } }
                ), onRemove: {
                    shown.remove(id)
                    expanded.remove(id)
                })
            }
        }
        .onAppear {
            expanded = [settings.sttProvider, settings.polishProvider]
            shown = Set(ProviderID.allCases.filter(settings.isSetUp))
        }
    }

    private var listed: [ProviderID] {
        ProviderID.allCases.filter { shown.contains($0) || settings.isSetUp($0) }
    }

    private var available: [ProviderID] {
        ProviderID.allCases.filter { !listed.contains($0) }
    }

    private var addMenu: some View {
        Menu {
            ForEach(ProviderGroup.allCases, id: \.self) { group in
                let ids = available.filter { $0.group == group }
                if !ids.isEmpty {
                    Section(group.title) {
                        ForEach(ids) { id in
                            Button(id.displayName) { add(id) }
                        }
                    }
                }
            }
        } label: {
            Label(L("添加服务商", "Add provider"), systemImage: "plus")
        }
        .fixedSize()
    }

    private func add(_ id: ProviderID) {
        settings.addProvider(id)
        shown.insert(id)
        expanded.insert(id)
    }
}

extension ProviderGroup {
    /// The heading of the providers of this group in the Add menu.
    var title: String {
        switch self {
        case .both: return L("语音转文字和文字整理", "Speech-to-text and clean-up")
        case .chat: return L("文字整理", "Clean-up")
        case .speech: return L("语音转文字", "Speech to text")
        case .local: return L("本机或自建服务", "On this computer or your own server")
        }
    }
}

private struct ProviderCard: View {
    @ObservedObject var settings: AppSettings
    let id: ProviderID
    @Binding var expanded: Bool
    /// Called once Remove has taken the provider off the page.
    var onRemove: () -> Void = {}
    @State private var testResult: (ok: Bool, text: String, color: Color)?
    @State private var testing = false

    var body: some View {
        Card {
            Button { withAnimation(.easeOut(duration: 0.18)) { expanded.toggle() } } label: {
                HStack(spacing: 12) {
                    HStack(spacing: 6) {
                        Text(id.displayName).font(.system(size: 13, weight: .medium))
                        ForEach(usageTags, id: \.self) { tag in
                            Text(tag)
                                .font(.system(size: 10, weight: .medium))
                                .padding(.horizontal, 6).padding(.vertical, 1.5)
                                .background(Capsule().fill(Color.accentColor.opacity(0.14)))
                                .foregroundStyle(Color.accentColor)
                        }
                    }
                    Spacer()
                    if settings.isConfigured(id) {
                        StatusPill(text: L("已配置", "Ready"), color: .green)
                    } else {
                        StatusPill(text: L("未配置", "Not set"), color: .secondary)
                    }
                    Image(systemName: "chevron.right")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.tertiary)
                        .rotationEffect(.degrees(expanded ? 90 : 0))
                }
                .rowPadding(.horizontal, 14)
                .rowPadding(.vertical, 11)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if expanded {
                CardDivider(inset: 0)
                VStack(alignment: .leading, spacing: 12) {
                    FieldRow(label: L("API Key", "API Key")) {
                        SecureField(id.keyPlaceholder, text: Binding(
                            get: { settings.savedAPIKey(for: id) },
                            set: { settings.setAPIKey($0, for: id) }
                        ), prompt: Text(id.keyPlaceholder))
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .textFieldStyle(.roundedBorder)
                        Button(L("粘贴", "Paste")) {
                            if let text = NSPasteboard.general.string(forType: .string) { settings.setAPIKey(text, for: id) }
                        }
                    }
                    if id == .openrouter, AppSettings.openRouterKeyFromEnvironment != nil {
                        Text(L("正在使用环境变量 OPENROUTER_API_KEY 中的 Key；没有设置这个变量时才用这里保存的 Key",
                               "Using the key in the OPENROUTER_API_KEY environment variable; the key saved here is used when it isn't set"))
                            .font(.system(size: 11.5))
                            .foregroundStyle(.secondary)
                            .padding(.leading, 100)
                    }
                    FieldRow(label: L("接口地址", "Base URL")) {
                        TextField(id == .custom ? "http://localhost:8000/v1" : id.defaultBaseURL, text: Binding(
                            get: { settings.baseURL(for: id) },
                            set: { settings.setBaseURL($0, for: id) }
                        ), prompt: Text(id == .custom ? "http://localhost:8000/v1" : id.defaultBaseURL))
                        .labelsHidden()
                        .multilineTextAlignment(.leading)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(size: 12, design: .monospaced))
                        if id != .custom, settings.baseURL(for: id) != id.defaultBaseURL {
                            Button(L("恢复默认", "Reset")) { settings.setBaseURL(id.defaultBaseURL, for: id) }
                        }
                    }
                    HStack(spacing: 10) {
                        if let url = id.keysURL {
                            Link(destination: url) {
                                Label(L("获取 API Key", "Get an API key"), systemImage: "arrow.up.right.square")
                                    .font(.system(size: 12))
                            }
                        } else {
                            Text(id.isLocal
                                 ? L("运行在这台电脑上，不需要 API Key", "Runs on this computer; no API key needed")
                                 : L("任何兼容 OpenAI 接口的服务：/audio/transcriptions 与 /chat/completions",
                                     "Any OpenAI-compatible server: /audio/transcriptions and /chat/completions"))
                                .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if let testResult {
                            Label(testResult.text, systemImage: testResult.ok ? "checkmark.circle.fill" : "xmark.octagon.fill")
                                .font(.system(size: 11.5))
                                .foregroundStyle(testResult.color)
                                .lineLimit(2)
                                .help(testResult.text)
                        }
                        Button(testing ? L("测试中…", "Testing…") : L("测试连接", "Test")) { test() }
                            .help(L("检查 API Key，并测三次往返延迟取中位数", "Checks the API key and times three round trips (median)"))
                            .disabled(testing || settings.endpoint(for: id) == nil)
                    }
                }
                .rowPadding(.all, 14)
                CardDivider(inset: 0)
                HStack {
                    Text(supportedSteps).font(.system(size: 11.5)).foregroundStyle(.secondary)
                    Spacer()
                    // A provider a step uses stays: switch the step to another one first.
                    Button(role: .destructive) {
                        settings.removeProvider(id)
                        onRemove()
                    } label: {
                        Label(L("移除", "Remove"), systemImage: "trash")
                    }
                    .controlSize(.small)
                    .disabled(settings.isInUse(id))
                    .help(settings.isInUse(id)
                          ? L("正在使用，先在「模型」里换成别的服务商", "In use: choose another provider under Models first")
                          : L("移除这个服务商和它的 API Key", "Removes this provider and its API key"))
                }
                .rowPadding(.horizontal, 14)
                .rowPadding(.vertical, 10)
            }
        }
    }

    /// What this provider can do here: "Speech-to-text and clean-up", or one of them.
    private var supportedSteps: String {
        if id.supportsSTT && id.supportsChat { return ProviderGroup.both.title }
        return id.supportsSTT ? L("仅语音转文字", "Speech-to-text only") : L("仅文字整理", "Clean-up only")
    }

    private var usageTags: [String] {
        var tags: [String] = []
        if settings.speechEngine == .provider, settings.sttProvider == id { tags.append(L("转写中使用", "Speech-to-text")) }
        if settings.polishEnabled, settings.polishProvider == id { tags.append(L("整理中使用", "Clean-up")) }
        return tags
    }

    private func test() {
        guard let endpoint = settings.endpoint(for: id) else { return }
        testing = true
        testResult = nil
        Task {
            defer { testing = false }
            do {
                // Three round trips; the median latency shows how quickly this provider answers from here.
                let check = try await APIClient(endpoint: endpoint).checkConnection()
                let color: Color = switch check.speed {
                case .fast: .green
                case .fine: .orange
                case .slow: .red
                }
                testResult = (true, check.summary, color)
            } catch {
                testResult = (false, APIError.from(error).localizedDescription, .red)
            }
        }
    }
}

private struct FieldRow<Content: View>: View {
    let label: String
    @ViewBuilder var content: Content

    var body: some View {
        HStack(spacing: 8) {
            Text(label)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .frame(width: 92, alignment: .leading)
            content
        }
    }
}

// MARK: - Models

struct ModelsPage: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject var navigation: SettingsNavigation
    let controller: SessionController
    @State private var sttModels: [APIClient.ModelInfo] = []
    @State private var chatModels: [APIClient.ModelInfo] = []
    @State private var backupChatModels: [APIClient.ModelInfo] = []
    @State private var backupSTTModels: [APIClient.ModelInfo] = []
    /// The on-device recognizer's locale for the spoken language (see `OnDeviceModelRow`).
    @State private var onDeviceLocale: String?

    var body: some View {
        PageScaffold(page: .models) {
            CardSection(title: L("语音转文字", "Speech to text"),
                        help: L("长语音会在停顿处自动切成 ≤28 秒的片段，边录边转，每片失败会自动重试。",
                                "Long recordings are split at pauses into ≤28 s segments that are transcribed while you talk, each retried on failure.")) {
                CardRow(title: L("转写方式", "Engine"),
                        subtitle: settings.speechEngine == .onDevice
                            ? L("免费、离线；准确率不如云端模型。", "Free and offline; less accurate than cloud models.") : nil,
                        help: L("「设备端」用 macOS 自带的语音识别在这台 Mac 上转写：不需要 API Key，录音不会离开电脑。第一次用某种语言时会先下载它的语音模型。文字整理仍然用下面的服务商；没有配置时，文字按识别结果直接插入。",
                                "On-device uses the speech recognition built into macOS, on this Mac: no API key, and your recording never leaves it. A language's speech model is downloaded the first time you use it. Clean-up still uses its provider below; without one, the text goes in as recognised.")) {
                    Picker(L("转写方式", "Engine"), selection: $settings.speechEngine) {
                        Text(L("服务商", "Provider")).tag(SpeechEngine.provider)
                        Text(L("设备端", "On-device")).tag(SpeechEngine.onDevice)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .fixedSize()
                }
                CardDivider()
                if settings.speechEngine == .onDevice {
                    OnDeviceModelRow(locale: onDeviceLocale)
                } else {
                    CardRow(title: L("服务商", "Provider")) {
                        ProviderPicker(selection: settings.sttProvider, options: ProviderID.allCases.filter(\.supportsSTT),
                                       settings: settings) { settings.selectSTTProvider($0) }
                    }
                    CardDivider()
                    CardRow(title: L("模型", "Model"),
                            help: VocabularyHints.style(provider: settings.sttProvider, model: settings.sttModel) != nil
                                ? L("你的词汇表会作为拼写提示一起发给这个模型", "Your vocabulary is sent to this model as spelling hints")
                                : L("这个模型不接收词汇提示，词汇表只用于文字整理", "This model takes no vocabulary hints; your vocabulary is used in clean-up only")) {
                        ModelField(text: $settings.sttModel, models: sttModels)
                    }
                    if settings.sttProvider == .openrouter {
                        SpeechHostRow(settings: settings, model: settings.sttModel)
                    }
                    CardDivider()
                    PriceRow(settings: settings, provider: settings.sttProvider, model: settings.sttModel, speech: true)
                    CardDivider()
                }
                CardRow(title: L("说话语言", "Spoken language"),
                        help: L("固定语言可以提高准确率；中英混说请选自动", "Fixing it can help accuracy; use Auto for mixed speech")) {
                    Picker(L("说话语言", "Spoken language"), selection: $settings.sttLanguage) {
                        Text(L("自动检测", "Auto-detect")).tag("")
                        ForEach(AppLanguage.supported) { language in
                            Text(language == .zh ? "中文" : language.nativeName).tag(language.rawValue)
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                if settings.speechEngine == .provider {
                CardDivider()
                CardRow(title: L("备用语音转文字", "Backup speech-to-text"),
                        subtitle: L("太慢或失败时同时请求备用，用先返回的", "Asked too when a request is slow or fails"),
                        help: L("某一段比平时慢很多（按音频长度和最近的速度估算）或者失败时，同时请求备用服务商，用先返回的那个",
                                "If a segment takes much longer than usual (judged by its length and recent speed) or fails, the backup is asked too and the first answer wins"),
                        isOn: $settings.sttBackupEnabled)
                if settings.sttBackupEnabled {
                    CardDivider()
                    CardRow(title: L("备用服务商", "Backup provider")) {
                        ProviderPicker(selection: settings.sttBackupProvider, options: ProviderID.allCases.filter(\.supportsSTT),
                                       settings: settings) { settings.selectSTTBackupProvider($0) }
                    }
                    CardDivider()
                    CardRow(title: L("备用模型 ID", "Backup model ID"),
                            help: L("选一个不同厂商的模型，两边不容易同时变慢", "Pick a model from another vendor so both are rarely slow at once")) {
                        ModelField(text: $settings.sttBackupModel, models: backupSTTModels)
                    }
                    CardDivider()
                    PriceRow(settings: settings, provider: settings.sttBackupProvider, model: settings.sttBackupModel, speech: true)
                }
                }
            }
            if settings.speechEngine == .onDevice {
                // Nothing to set up for speech-to-text.
            } else if !settings.isConfigured(settings.sttProvider) {
                setupBanner(for: settings.sttProvider)
            } else if settings.sttBackupEnabled, !settings.isConfigured(settings.sttBackupProvider) {
                setupBanner(for: settings.sttBackupProvider)
            }

            CardSection(title: L("文字整理", "Clean-up")) {
                CardRow(title: L("用 AI 整理文字", "Clean up with AI"),
                        subtitle: L("去掉口头禅和自我修正，自动分段、整理成列表", "Removes fillers and false starts, adds paragraphs and lists"),
                        isOn: $settings.polishEnabled)
                if settings.polishEnabled {
                    CardDivider()
                    CardRow(title: L("服务商", "Provider")) {
                        ProviderPicker(selection: settings.polishProvider, options: ProviderID.allCases.filter(\.supportsChat),
                                       settings: settings) { settings.selectPolishProvider($0) }
                    }
                    CardDivider()
                    CardRow(title: L("模型", "Model"),
                            help: settings.polishProvider.minimisesReasoning
                                ? L("推理会自动关闭或降到最低，以减少延迟", "Reasoning is turned off or minimised for speed") : nil) {
                        ModelField(text: $settings.polishModel, models: chatModels)
                    }
                    if settings.polishProvider == .openrouter {
                        HostRow(settings: settings, model: settings.polishModel)
                    }
                    CardDivider()
                    PriceRow(settings: settings, provider: settings.polishProvider, model: settings.polishModel, speech: false)
                    CardDivider()
                    CardRow(title: L("备用模型", "Backup model"),
                            subtitle: L("太慢或失败时同时请求备用，用先返回的", "Asked too when a request is slow or fails"),
                            help: L("首选模型 \(hedgeDelayLabel) 内还没开始输出、或请求失败时，同时请求备用模型，用先出字的那个",
                                    "If the main model hasn't started answering within \(hedgeDelayLabel), or fails, the backup is asked too and the first to answer wins"),
                            isOn: $settings.polishBackupEnabled)
                    if settings.polishBackupEnabled {
                        CardDivider()
                        CardRow(title: L("备用服务商", "Backup provider")) {
                            ProviderPicker(selection: settings.polishBackupProvider, options: ProviderID.allCases.filter(\.supportsChat),
                                           settings: settings) { settings.selectPolishBackupProvider($0) }
                        }
                        CardDivider()
                        CardRow(title: L("备用模型 ID", "Backup model ID"),
                                help: L("选一个不同厂商的快速模型，两边不容易同时变慢", "Pick a fast model from another vendor so both are rarely slow at once")) {
                            ModelField(text: $settings.polishBackupModel, models: backupChatModels)
                        }
                        if settings.polishBackupProvider == .openrouter {
                            HostRow(settings: settings, model: settings.polishBackupModel)
                        }
                        CardDivider()
                        PriceRow(settings: settings, provider: settings.polishBackupProvider, model: settings.polishBackupModel, speech: false)
                    }
                }
            }
            if settings.polishEnabled, !settings.isConfigured(settings.polishProvider) {
                setupBanner(for: settings.polishProvider)
            } else if settings.polishEnabled, settings.polishBackupEnabled, settings.polishBackupProvider != settings.polishProvider,
                      !settings.isConfigured(settings.polishBackupProvider) {
                setupBanner(for: settings.polishBackupProvider)
            }

            SpendLimitSection(settings: settings)
        }
        .task(id: "\(settings.sttProvider.rawValue)|\(settings.apiKey(for: settings.sttProvider).count)") {
            sttModels = await loadModels(settings.sttProvider, speech: true)
        }
        .task(id: "\(settings.sttBackupProvider.rawValue)|\(settings.apiKey(for: settings.sttBackupProvider).count)|\(settings.sttBackupEnabled)") {
            if settings.sttBackupEnabled { backupSTTModels = await loadModels(settings.sttBackupProvider, speech: true) }
        }
        .task(id: "\(settings.polishProvider.rawValue)|\(settings.apiKey(for: settings.polishProvider).count)") {
            chatModels = await loadModels(settings.polishProvider, speech: false)
        }
        .task(id: "\(settings.polishBackupProvider.rawValue)|\(settings.apiKey(for: settings.polishBackupProvider).count)") {
            backupChatModels = await loadModels(settings.polishBackupProvider, speech: false)
        }
        .task { PriceStore.shared.refreshIfStale() }
        // As soon as on-device is picked, or the language changes: fetch its model (nothing to do once it's there).
        .task(id: settings.speechEngine == .onDevice ? settings.sttLanguage : nil) {
            guard settings.speechEngine == .onDevice else { return }
            let found = await OnDeviceModels.locale(for: settings.sttLanguage)
            onDeviceLocale = found ?? ""
            if let found { OnDeviceModels.shared.install(found) }
        }
        .task(id: settings.sttProvider == .openrouter ? settings.sttModel : "") {
            if settings.sttProvider == .openrouter { await OpenRouterHosts.shared.load(settings.sttModel, settings: settings) }
        }
        .task(id: settings.polishProvider == .openrouter ? settings.polishModel : "") {
            if settings.polishProvider == .openrouter { await OpenRouterHosts.shared.load(settings.polishModel, settings: settings) }
        }
        .task(id: settings.polishBackupProvider == .openrouter ? settings.polishBackupModel : "") {
            if settings.polishBackupProvider == .openrouter { await OpenRouterHosts.shared.load(settings.polishBackupModel, settings: settings) }
        }
        .onChange(of: settings.polishModel) { controller.refreshModelInfo() }
        .onChange(of: settings.polishProvider) { controller.refreshModelInfo() }
        .onChange(of: settings.polishBackupProvider) { controller.refreshModelInfo() }
        .onChange(of: settings.polishBackupEnabled) { controller.refreshModelInfo() }
    }

    private var hedgeDelayLabel: String {
        HedgedPolish.defaultHedgeDelay.formatted(.number.precision(.fractionLength(0...2))) + L(" 秒", " s")
    }

    private func setupBanner(for id: ProviderID) -> some View {
        Banner(symbol: "exclamationmark.triangle.fill", color: .orange,
               text: L("\(id.displayName) 还没有配置 API Key", "\(id.displayName) has no API key yet")) {
            Button(L("去填写", "Add key")) { navigation.page = .providers }.controlSize(.small)
        }
    }

    private func loadModels(_ id: ProviderID, speech: Bool) async -> [APIClient.ModelInfo] {
        guard let endpoint = settings.endpoint(for: id) else { return [] }
        let client = APIClient(endpoint: endpoint)
        let models = (try? await client.listModels(outputModality: speech && id == .openrouter ? "transcription" : nil)) ?? []
        guard id != .openrouter else { return models.sorted { $0.id < $1.id } }
        // Other providers list every model together; split them by name.
        let filtered = models.filter { $0.looksLikeSpeechModel == speech }
        return (filtered.isEmpty ? models : filtered).sorted { $0.id < $1.id }
    }
}

/// The speech model of the recognizer on this Mac for the spoken language: shown only while it needs noticing (being
/// downloaded, failed to download, or no model for the language); fetched as soon as on-device is picked.
private struct OnDeviceModelRow: View {
    /// The spoken language's locale: nil until looked up, "" when there's no model for the language.
    let locale: String?
    @ObservedObject private var models = OnDeviceModels.shared

    var body: some View {
        Group {
            if locale == "" {
                notice(symbol: "exclamationmark.triangle.fill", text: OnDeviceRoute.unsupportedLanguageMessage)
            } else if let locale, let status = models.statuses[locale] {
                switch status {
                case let .downloading(fraction):
                    HStack(spacing: 10) {
                        Text(L("正在下载语音模型… \(Int(fraction * 100))%", "Downloading the speech model… \(Int(fraction * 100))%"))
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                        Spacer()
                        ProgressView(value: fraction).frame(width: 120)
                    }
                    .padding(.vertical, 2)
                case let .failed(message):
                    HStack(spacing: 10) {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        Text(L("语音模型下载失败", "Couldn't download the speech model"))
                            .font(.system(size: 12))
                            .help(message)
                        Spacer()
                        Button(L("重试", "Retry")) { models.install(locale) }.controlSize(.small)
                    }
                case .ready:
                    EmptyView()
                }
            }
        }
    }

    private func notice(symbol: String, text: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: symbol).foregroundStyle(.orange)
            Text(text).font(.system(size: 12))
            Spacer()
        }
    }
}

private struct ProviderPicker: View {
    let selection: ProviderID
    let options: [ProviderID]
    @ObservedObject var settings: AppSettings
    let onSelect: (ProviderID) -> Void

    var body: some View {
        // The providers set up on the Providers page first; the others, which need a key, after them.
        let setUp = options.filter { $0 == selection || settings.isSetUp($0) }
        let others = options.filter { !setUp.contains($0) }
        Menu {
            ForEach(setUp) { id in item(id) }
            if !others.isEmpty {
                Divider()
                ForEach(others) { id in item(id) }
            }
        } label: {
            Text(selection.displayName)
        }
        .fixedSize()
    }

    private func item(_ id: ProviderID) -> some View {
        Button {
            onSelect(id)
        } label: {
            if id == selection {
                Label(id.displayName, systemImage: "checkmark")
            } else {
                Text(settings.isConfigured(id) ? id.displayName : id.displayName + L("（未配置）", " (not set)"))
            }
        }
    }
}

/// The hosts OpenRouter serves each model from, looked up by the Models page as its models change.
@MainActor
final class OpenRouterHosts: ObservableObject {
    static let shared = OpenRouterHosts()
    @Published private(set) var hosts: [String: [APIClient.ModelHost]] = [:]

    func load(_ model: String, settings: AppSettings) async {
        guard let endpoint = settings.endpoint(for: .openrouter) else { return }
        hosts[model] = (try? await APIClient(endpoint: endpoint).listHosts(model: model)) ?? []
    }
}

/// Which company serves an OpenRouter clean-up model: whichever answers fastest, or one the user picks. Shown only when
/// the model has more than one host (or one was picked before).
private struct HostRow: View {
    @ObservedObject var settings: AppSettings
    let model: String
    @ObservedObject private var cache = OpenRouterHosts.shared
    private var hosts: [APIClient.ModelHost] { cache.hosts[model] ?? [] }

    var body: some View {
        // Nothing at all without a choice to show, so a settings form has no empty row for it. The page loads the hosts.
        Group {
            if hosts.count > 1 || chosen != nil {
                CardDivider()
                CardRow(title: L("托管方", "Host"), help: subtitle) {
                    Menu {
                        Button {
                            settings.setOpenRouterHost(nil, for: model)
                        } label: {
                            if chosen == nil { Label(automatic, systemImage: "checkmark") } else { Text(automatic) }
                        }
                        Divider()
                        if let chosen, !hosts.contains(where: { $0.tag == chosen }) {
                            Label(chosen, systemImage: "checkmark")
                        }
                        ForEach(hosts) { host in
                            Button {
                                settings.setOpenRouterHost(host.tag, for: model)
                            } label: {
                                if host.tag == chosen { Label(detail(host), systemImage: "checkmark") } else { Text(detail(host)) }
                            }
                        }
                    } label: {
                        Text(chosen.map(label) ?? automatic)
                    }
                    .fixedSize()
                }
            }
        }
    }

    private var chosen: String? { settings.openRouterHost(for: model) }
    private var automatic: String { L("自动（最快）", "Automatic (fastest)") }

    private var subtitle: String {
        guard let chosen else {
            return L("OpenRouter 选当下延迟最低的托管方", "OpenRouter uses whichever host is answering fastest")
        }
        return L("先请求 \(label(chosen))；它出错或不可用时，OpenRouter 自动换最快的另一家",
                 "\(label(chosen)) is asked first; if it errors or is down, OpenRouter falls back to the fastest other host")
    }

    /// "DeepInfra · turbo" for the tag "deepinfra/turbo".
    private func label(_ tag: String) -> String {
        guard let host = hosts.first(where: { $0.tag == tag }) else { return tag }
        let variant = tag.split(separator: "/", maxSplits: 1).dropFirst().first.map { " · \($0)" } ?? ""
        return host.name + variant
    }

    /// "DeepInfra · turbo — $0.15 / 1M in · 98% up"
    private func detail(_ host: APIClient.ModelHost) -> String {
        var parts: [String] = []
        if let price = host.inputPrice { parts.append(L("输入 \(UsageFormat.rate(price * 1_000_000)) / 百万", "\(UsageFormat.rate(price * 1_000_000)) / 1M in")) }
        if let uptime = host.uptime, uptime < 99.5 { parts.append(L("可用率 \(Int(uptime))%", "\(Int(uptime))% up")) }
        return label(host.tag) + (parts.isEmpty ? "" : " — " + parts.joined(separator: " · "))
    }
}

/// Why an OpenRouter speech model served by several hosts has no host choice: OpenRouter ignores routing on
/// /audio/transcriptions and picks by price (tested Oct 2026: `order`, `only` and `ignore` all made no difference).
private struct SpeechHostRow: View {
    @ObservedObject var settings: AppSettings
    let model: String
    @ObservedObject private var cache = OpenRouterHosts.shared
    private var hosts: [APIClient.ModelHost] { cache.hosts[model] ?? [] }

    var body: some View {
        // Nothing at all without a choice to show, so a settings form has no empty row for it. The page loads the hosts.
        Group {
            if hosts.count > 1 {
                CardDivider()
                CardRow(title: L("托管方", "Host"), help: subtitle) {
                    Text(L("自动（按价格）", "Automatic (by price)")).font(.system(size: 12)).foregroundStyle(.secondary)
                }
            }
        }
    }

    private var subtitle: String {
        let names = ListFormatter.localizedString(byJoining: Array(NSOrderedSet(array: hosts.map(\.name))) as? [String] ?? [])
        var text = L("由 \(names) 提供。语音转文字由 OpenRouter 按价格挑选，不能指定。",
                     "Served by \(names). For speech to text OpenRouter picks by price and can't be told which.")
        if hosts.contains(where: { $0.tag.hasPrefix("groq") }) {
            text += L("想一直用 Groq，就把服务商换成 Groq 并填上它的 Key。", " To always use Groq, choose Groq as the provider and add a Groq key.")
        }
        return text
    }
}

/// A model's price, next to where it's chosen: OpenRouter's live price (read-only), or fields to enter the price for
/// any other provider, so the Home page can count what it costs.
private struct PriceRow: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject var prices = PriceStore.shared
    let provider: ProviderID
    let model: String
    let speech: Bool

    var body: some View {
        CardRow(title: L("价格", "Price"), help: subtitle) {
            if provider == .openrouter {
                Text(openRouterPrice).font(.system(size: 12)).foregroundStyle(.secondary)
            } else if speech {
                HStack(spacing: 6) {
                    field(\.perMinute)
                    Text(L("美元/分钟", "$/min")).font(.system(size: 12)).foregroundStyle(.secondary)
                }
                .disabled(trimmedModel.isEmpty)
            } else {
                HStack(spacing: 6) {
                    Text(L("输入", "In")).font(.system(size: 12)).foregroundStyle(.secondary)
                    field(\.inputPerMillion)
                    Text(L("输出", "Out")).font(.system(size: 12)).foregroundStyle(.secondary)
                    field(\.outputPerMillion)
                }
                .disabled(trimmedModel.isEmpty)
            }
        }
    }

    private var trimmedModel: String { model.trimmingCharacters(in: .whitespaces) }

    private var subtitle: String {
        guard provider == .openrouter else {
            return speech
                ? L("按录音时长计费，美元 / 分钟。留空则不计入花费。", "Per minute of audio, in USD. Leave empty to leave it out of the spend.")
                : L("美元 / 百万 token（服务商不返回用量时按字数估算）。留空则不计入花费。",
                    "USD per million tokens (estimated from the text when the server doesn't say). Leave empty to leave it out of the spend.")
        }
        if prices.catalog.isEmpty { return L("正在从 OpenRouter 获取最新价格…", "Getting the latest prices from OpenRouter…") }
        return L("OpenRouter 实时价格，更新于 \(prices.catalog.fetchedAt.shortStamp)。实际按每次请求的扣费计算。",
                 "Live from OpenRouter, updated \(prices.catalog.fetchedAt.shortStamp). Each request counts what OpenRouter billed.")
    }

    private var openRouterPrice: String {
        if speech { return L("按实际扣费", "As billed") }
        guard let price = prices.catalog.price(trimmedModel) else {
            return trimmedModel.isEmpty ? "—" : L("OpenRouter 没有列出这个模型", "Not listed on OpenRouter")
        }
        let input = UsageFormat.rate(price.inputPerMillion ?? 0)
        let output = UsageFormat.rate(price.outputPerMillion ?? 0)
        return L("输入 \(input) · 输出 \(output) / 百万 token", "In \(input) · Out \(output) / 1M tokens")
    }

    private func field(_ keyPath: WritableKeyPath<ModelPrice, Double?>) -> some View {
        TextField("$", value: Binding<Double?>(
            get: { settings.customPrice(provider, model)?[keyPath: keyPath] },
            set: { value in
                var price = settings.customPrice(provider, model) ?? ModelPrice()
                price[keyPath: keyPath] = value.map { max(0, $0) }
                settings.setCustomPrice(provider, model, price)
            }
        ), format: .number.precision(.fractionLength(0...6)), prompt: Text(verbatim: "$"))
        .labelsHidden()
        .textFieldStyle(.roundedBorder)
        .multilineTextAlignment(.trailing)
        .frame(width: 80)
    }
}

/// The monthly spending limit: off by default; when on, a reminder in the capsule as this month's spend nears it and
/// when it reaches it (see `SpendLimit`). It never stops a dictation.
private struct SpendLimitSection: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject var usage = UsageStore.shared

    var body: some View {
        CardSection(title: L("花费", "Spending")) {
            CardRow(title: L("每月花费上限", "Monthly spending limit"),
                    subtitle: L("快到和到达上限时提醒你，听写不受影响", "A reminder near and at the limit; dictation keeps working"),
                    help: Self.help, isOn: $settings.spendLimitEnabled)
            if settings.spendLimitEnabled {
                CardDivider()
                CardRow(title: L("上限", "Limit"), subtitle: spentLine) {
                    HStack(spacing: 6) {
                        TextField("$", value: Binding<Double>(
                            get: { settings.monthlySpendLimit },
                            set: { settings.monthlySpendLimit = $0.isFinite ? max(0, $0) : 0 }
                        ), format: .number.precision(.fractionLength(0...2)), prompt: Text(verbatim: "$"))
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        .multilineTextAlignment(.trailing)
                        .frame(width: 80)
                        Text(L("美元/月", "$/month")).font(.system(size: 12)).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    /// What this month has cost so far, against the limit.
    private var spentLine: String {
        let spent = UsageFormat.money(usage.ledger.month(CalendarDay(Date())).cost)
        return L("本月已花费 \(spent)", "\(spent) spent this month")
    }

    static var help: String {
        let percent = Int((SpendLimit.warningFraction * 100).rounded())
        return L("本月花费到上限的 \(percent)% 时提醒一次，到上限时再提醒一次，在听写后的录音浮窗里。只是提醒，不会停止或取消听写。只计入知道价格的请求：OpenRouter 的请求，以及在本页填了价格的其他服务商的模型。按本地时间的自然月计算，每月 1 日重新开始；调高上限后，提醒也会重新开始。",
                 "You're reminded in the capsule after a dictation once this month's spend reaches \(percent)% of the limit, and again when it reaches the limit. It's only a reminder: no dictation is stopped or cancelled. Only requests with a known price count: OpenRouter's, and other providers' models you've entered a price for on this page. Months follow your local time and start over on the 1st, and so do the reminders when you raise the limit.")
    }
}

/// Free-text model ID with a browse menu of the provider's models (grouped by vendor when long).
private struct ModelField: View {
    @Binding var text: String
    let models: [APIClient.ModelInfo]

    var body: some View {
        HStack(spacing: 6) {
            TextField(L("模型 ID", "Model ID"), text: $text, prompt: Text(L("模型 ID", "Model ID")))
                .labelsHidden()
                .multilineTextAlignment(.leading)
                .textFieldStyle(.roundedBorder)
                .font(.system(size: 12, design: .monospaced))
                .frame(width: 270)
            ModelMenu(models: models, language: AppLanguage.resolved) { text = $0 }
                .equatable()
        }
    }
}

/// The browse menu. OpenRouter lists hundreds of models, and the Models page redraws on every key typed into any of
/// its fields: compared by the models it lists, the menu is only built again when they change.
private struct ModelMenu: View, Equatable {
    let models: [APIClient.ModelInfo]
    /// Its one string is in the UI language.
    let language: AppLanguage
    let pick: (String) -> Void

    static func == (a: ModelMenu, b: ModelMenu) -> Bool {
        a.language == b.language && a.models.count == b.models.count
            && zip(a.models, b.models).allSatisfy { $0.id == $1.id }
    }

    var body: some View {
        Menu {
            if models.isEmpty {
                Text(L("填写 API Key 后可浏览模型", "Add an API key to browse models"))
            } else if models.count > 40 {
                ForEach(groups, id: \.0) { vendor, items in
                    Menu(vendor) {
                        ForEach(items) { model in Button(model.id) { pick(model.id) } }
                    }
                }
            } else {
                ForEach(models) { model in Button(model.id) { pick(model.id) } }
            }
        } label: {
            Image(systemName: "list.bullet")
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help(L("浏览可用模型", "Browse models"))
    }

    private var groups: [(String, [APIClient.ModelInfo])] {
        let grouped = Dictionary(grouping: models) { $0.id.split(separator: "/").first.map(String.init) ?? $0.id }
        return grouped.keys.sorted().map { ($0, grouped[$0]!) }
    }
}

// MARK: - Style

struct StylePage: View {
    @ObservedObject var settings: AppSettings
    /// An imported file's terms, waiting for the user to confirm what it adds.
    @State private var pendingImport: VocabularyImport?
    @State private var notice: Notice?
    @State private var exported = false
    @State private var showAllLearned = false
    /// How many learned terms show before "Show all": the newest ones.
    private static let learnedShown = 5

    private struct VocabularyImport {
        let terms: [String]
        let plan: VocabularyTransfer.ImportPlan
    }

    private struct Notice {
        let title: String
        let message: String
    }

    var body: some View {
        PageScaffold(page: .style) {
            CardSection(title: L("专有名词 / 词汇表", "Vocabulary"),
                        help: L("用逗号或换行分隔，比如人名、产品名、项目名。支持的语音模型会把它们当作拼写提示，整理时也会按这里的写法纠正识别错误。",
                                "Comma- or line-separated: names, products, projects. Speech-to-text models that take them get these as spelling hints, and clean-up uses these spellings to fix recognition errors.")) {
                NoteEditor(text: $settings.vocabulary, placeholder: L("人名、产品名、项目名，用逗号或换行分隔", "Names, products, projects, separated by commas or lines"), height: 88)
                CardDivider(inset: 0)
                transferButtons
            }
            CardSection(title: L("拼写", "Spelling")) {
                CardRow(title: L("参考光标附近的文字拼写名字", "Use text around the cursor to spell names"),
                        help: L("开始录音时，读取你正在输入的文本框里光标前后最多 1500 个字符，连同转写一起发给文字整理模型，只用来把你说到的名字和术语拼成那里的写法，不会把其中的内容抄进你的文字。密码框永远不会读取，这些文字也不会保存在历史记录里。",
                                "When recording starts, up to 1,500 characters around your cursor in the field you're dictating into are read and sent with the transcript to your clean-up models, only so that the names and terms you say are spelled the way they appear there. Nothing from it is copied into your text. Password fields are never read, and the text isn't kept in History."),
                        isOn: $settings.useCursorContext)
                CardDivider()
                CardRow(title: L("从我的修改中学习", "Learn from my corrections"),
                        help: L("粘贴后如果你改了某个识别错的词（比如把 TypeList 改成 Typeless），离开输入框或发送后会自动把它加进词汇表。能读取的输入框直接比较前后内容；读不到的（微信、Firefox、Electron 应用等）会跟随你粘贴后的按键，但用中文、日文、韩文输入法打的字跟随不了。只学发音相近的改动，不学改写、改数字和普通词替换。一切都在本机完成。",
                                "If you fix a misrecognised word after pasting (say TypeList → Typeless), it's added to the vocabulary once you leave the field or send. Where the field can be read, its text is compared; where it can't (WeChat, Firefox, Electron apps…), the keys you press after the paste are followed, though not text typed with a Chinese, Japanese or Korean input method. Only sound-alike fixes are learned, never rewrites, changed numbers or ordinary word swaps. All of it stays on this Mac."),
                        isOn: $settings.learnFromEdits)
                let learned = Array(settings.learnedTerms.reversed())
                ForEach(showAllLearned ? learned : Array(learned.prefix(Self.learnedShown))) { term in
                    CardDivider()
                    LearnedTermRow(learned: term) { settings.forget(term) }
                }
                if !showAllLearned, learned.count > Self.learnedShown {
                    CardDivider()
                    Button { showAllLearned = true } label: {
                        Text(L("显示全部 \(learned.count) 个", "Show all \(learned.count)"))
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .rowPadding(.horizontal, 14)
                            .rowPadding(.vertical, 9)
                    }
                    .buttonStyle(HoverButtonStyle(cornerRadius: Theme.cardRadius))
                }
            }
            CardSection(title: L("整理偏好（可选）", "Clean-up preferences (optional)")) {
                NoteEditor(text: $settings.extraInstructions,
                           placeholder: L("例如：「不要使用列表」「保留我的口语语气」「英文术语保持小写」。",
                                          "e.g. “Never use bullet lists”, “Keep my casual tone”, “Keep technical terms lowercase”."),
                           height: 88)
            }
            CardSection(title: L("快捷短语", "Snippets"),
                        help: L("说出触发词，插入时会换成你写好的内容，一字不改，句子中间说也行。触发词会作为词汇提示交给语音识别和整理，让它原样保留。最好选一个平时不会说的说法，免得误替换。",
                                "Say a trigger and the text you wrote goes in instead, word for word, even mid-sentence. Triggers go to speech-to-text and clean-up as vocabulary, so they come through intact. Pick a phrase you wouldn't otherwise say, so it isn't replaced by accident.")) {
                SnippetList(settings: settings)
            }
            TranslationSection(settings: settings)
        }
        .alert(pendingImport.map { Self.importTitle($0.plan.added.count) } ?? "",
               isPresented: Binding(get: { pendingImport != nil }, set: { if !$0 { pendingImport = nil } }),
               presenting: pendingImport) { pending in
            Button(L("导入", "Import")) { settings.importVocabulary(pending.terms) }
            Button(L("取消", "Cancel"), role: .cancel) {}
        } message: { pending in
            Text(Self.importSummary(pending.plan))
        }
    }

    // MARK: Import and export

    private var transferButtons: some View {
        HStack(spacing: 8) {
            Button { importVocabulary() } label: {
                Label(L("导入…", "Import…"), systemImage: "square.and.arrow.down")
            }
            .help(L("从 CSV（第一列）或文本文件添加词汇，已有的词不会重复添加。导出的 CSV 里的误听写法仅供参考，不会导入。",
                    "Add terms from a CSV (its first column) or text file; ones you already have aren't added twice. The misheard forms in an exported CSV are for reference and aren't imported."))
            Menu {
                Button(L("CSV（含学到的误听写法）…", "CSV (with how learned terms were misheard)…")) { exportVocabulary(csv: true) }
                Button(L("纯文本（每行一个词）…", "Plain text (one term per line)…")) { exportVocabulary(csv: false) }
            } label: {
                Label(exported ? L("已导出", "Exported") : L("导出", "Export"), systemImage: exported ? "checkmark" : "square.and.arrow.up")
            }
            .fixedSize()
            .disabled(settings.vocabularyList.isEmpty)
            Spacer()
        }
        .controlSize(.small)
        .rowPadding(.horizontal, 12)
        .rowPadding(.vertical, 8)
        // On the row rather than the page, which already has the import confirmation: one alert per view.
        .alert(notice?.title ?? "", isPresented: Binding(get: { notice != nil }, set: { if !$0 { notice = nil } }), presenting: notice) { _ in
            Button(L("好", "OK")) {}
        } message: { notice in
            Text(notice.message)
        }
    }

    /// Reads the file, then asks before adding what's new to the vocabulary.
    private func importVocabulary() {
        Exports.chooseFile(types: VocabularyTransfer.importExtensions.compactMap { UTType(filenameExtension: $0) }) { url in
            Task {
                switch await Exports.readVocabulary(url) {
                case let .failure(error):
                    notice = Notice(title: L("无法导入", "Couldn't import"), message: error.message)
                case let .success(terms):
                    let plan = VocabularyTransfer.plan(terms, existing: settings.vocabularyList)
                    if terms.isEmpty {
                        notice = Notice(title: L("文件里没有词", "No terms in this file"),
                                        message: L("每行一个词，或用逗号分隔；CSV 文件取第一列。",
                                                   "Put one term per line or separate them with commas; from a CSV file, the first column is used."))
                    } else if plan.added.isEmpty {
                        // Nothing to add, but importing still means the user wants these terms (see importVocabulary).
                        settings.importVocabulary(terms)
                        let message: String
                        if plan.tooLong + plan.overLimit > 0 {
                            message = Self.importSummary(plan)
                        } else if plan.known == 1 {
                            message = L("文件里的词已经在词汇表里了。", "The term in the file is already in your vocabulary.")
                        } else {
                            message = L("文件里的 \(plan.known) 个词都已经在词汇表里了。", "All \(plan.known) terms in the file are already in your vocabulary.")
                        }
                        notice = Notice(title: L("没有新词可导入", "Nothing new to import"), message: message)
                    } else {
                        pendingImport = VocabularyImport(terms: terms, plan: plan)
                    }
                }
            }
        }
    }

    private func exportVocabulary(csv: Bool) {
        let terms = settings.vocabularyForExport
        let name = Exports.fileName(L("OpenTypeless 词汇表", "OpenTypeless Vocabulary"), extension: csv ? "csv" : "txt")
        Exports.chooseDestination(name: name, type: csv ? .commaSeparatedText : .plainText) { url in
            Task {
                if let failure = await Exports.write(to: url, { VocabularyTransfer.data(terms, csv: csv) }) {
                    notice = Notice(title: L("导出失败", "Couldn't export"), message: failure)
                    return
                }
                exported = true
                try? await Task.sleep(nanoseconds: 1_500_000_000)
                exported = false
            }
        }
    }

    private static func importTitle(_ count: Int) -> String {
        count == 1 ? L("导入 1 个新词？", "Import 1 new term?") : L("导入 \(count) 个新词？", "Import \(count) new terms?")
    }

    /// The first few new terms, what's left out and why, and how many of the file's terms are already in the vocabulary.
    private static func importSummary(_ plan: VocabularyTransfer.ImportPlan) -> String {
        var parts: [String] = []
        if !plan.added.isEmpty {
            parts.append(plan.added.prefix(10).joined(separator: L("、", ", ")) + (plan.added.count > 10 ? "…" : ""))
        }
        let length = VocabularyTransfer.maxTermLength, limit = VocabularyTransfer.maxTerms
        if plan.tooLong == 1 {
            parts.append(L("有 1 个条目超过 \(length) 个字符，没有导入。", "1 entry longer than \(length) characters is left out."))
        } else if plan.tooLong > 1 {
            parts.append(L("有 \(plan.tooLong) 个条目超过 \(length) 个字符，没有导入。", "\(plan.tooLong) entries longer than \(length) characters are left out."))
        }
        if plan.overLimit == 1 {
            parts.append(L("词汇表最多 \(limit) 个词，另有 1 个没有导入。", "The vocabulary holds up to \(limit) terms, so 1 more is left out."))
        } else if plan.overLimit > 1 {
            parts.append(L("词汇表最多 \(limit) 个词，另有 \(plan.overLimit) 个没有导入。", "The vocabulary holds up to \(limit) terms, so \(plan.overLimit) more are left out."))
        }
        if plan.known == 1 {
            parts.append(L("另有 1 个已经在词汇表里。", "1 more is already in your vocabulary."))
        } else if plan.known > 1 {
            parts.append(L("另有 \(plan.known) 个已经在词汇表里。", "\(plan.known) more are already in your vocabulary."))
        }
        return parts.joined(separator: "\n\n")
    }
}

/// Translation mode: on or off, the language dictations are translated into, and the key that switches a recording to
/// translation while the dictation shortcut is held.
private struct TranslationSection: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        let chord = settings.translationChord
        CardSection(title: L("翻译", "Translation"),
                    help: L("说什么语言都可以，插入的是翻译。只需一次模型请求，和整理一样快；翻译不可用时，会改为插入整理后的原文。",
                            "Speak in any language; what goes in is the translation. It takes one model request, as quick as a clean-up. When the translation can't be used, the cleaned-up original goes in instead.")) {
            CardRow(title: L("翻译模式", "Translation mode"),
                    subtitle: L("按住听写快捷键时再按一下 \(chord.label)，这次听写会翻译后再插入",
                                "Hold the dictation shortcut and press \(chord.label) too: that dictation goes in translated"),
                    isOn: $settings.translationEnabled)
            if settings.translationEnabled {
                CardDivider()
                CardRow(title: L("翻译成", "Translate into")) {
                    Picker(L("翻译成", "Translate into"), selection: $settings.translationLanguage) {
                        ForEach(TranslationLanguage.allCases, id: \.self) { Text($0.nativeName).tag($0.rawValue) }
                        Text(L("其他…", "Other…")).tag(AppSettings.otherTranslationLanguage)
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                if settings.translationLanguage == AppSettings.otherTranslationLanguage {
                    CardDivider()
                    CardRow(title: L("语言", "Language"),
                            help: L("用它的名字，比如 Italiano、繁體中文、Tiếng Việt",
                                    "By its name: Italiano, 繁體中文, Tiếng Việt…")) {
                        TextField(L("语言", "Language"), text: $settings.translationOtherLanguage, prompt: Text(L("语言", "Language")))
                            .labelsHidden()
                            .multilineTextAlignment(.leading)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 180)
                    }
                }
                CardDivider()
                CardRow(title: L("切换键", "Switch key"),
                        help: L("按住听写快捷键时按下它（左右两边都可以）",
                                "Pressed while you hold the dictation shortcut (either side)")) {
                    Picker(L("切换键", "Switch key"), selection: $settings.translationChord) {
                        ForEach(TranslationChord.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            }
        }
    }
}

/// A multi-line field filling its card, with a hint inside it while it's empty.
struct NoteEditor: View {
    @Binding var text: String
    let placeholder: String
    let height: CGFloat

    var body: some View {
        TextEditor(text: $text)
            .font(.system(size: 13))
            .scrollContentBackground(.hidden)
            .rowPadding(.all, 10)
            .frame(height: height)
            .overlay(alignment: .topLeading) {
                if text.isEmpty {
                    Text(placeholder)
                        .font(.system(size: 13))
                        .foregroundStyle(.tertiary)
                        .padding(.horizontal, 5)
                        .rowPadding(.horizontal, 10)
                        .rowPadding(.vertical, 10)
                        .allowsHitTesting(false)
                }
            }
    }
}

/// The snippets, each opening into an editor in place, and a new one added at the end.
private struct SnippetList: View {
    @ObservedObject var settings: AppSettings
    /// The snippet open in the editor; a new one isn't in the list until it's saved.
    @State private var editing: Snippet?

    var body: some View {
        if settings.snippets.isEmpty && editing == nil {
            CardRow(title: L("还没有快捷短语", "No snippets yet"),
                    subtitle: L("例如：说「我的邮箱」，插入 name@example.com", "For example, say “my email” to insert name@example.com")) {
                Button(L("添加快捷短语", "Add Snippet")) { editing = Snippet(trigger: "", expansion: "") }
            }
        } else {
            ForEach(Array(settings.snippets.enumerated()), id: \.element.id) { index, snippet in
                if index > 0 { CardDivider() }
                if editing?.id == snippet.id {
                    SnippetEditor(settings: settings, snippet: snippet) { editing = nil }
                } else {
                    SnippetRow(snippet: snippet, onEdit: { editing = snippet }, onDelete: {
                        if editing?.id == snippet.id { editing = nil }
                        settings.deleteSnippet(snippet)
                    })
                }
            }
            if let editing, !settings.snippets.contains(where: { $0.id == editing.id }) {
                if !settings.snippets.isEmpty { CardDivider() }
                SnippetEditor(settings: settings, snippet: editing) { self.editing = nil }
            } else {
                CardDivider()
                Button { editing = Snippet(trigger: "", expansion: "") } label: {
                    Label(L("添加快捷短语", "Add Snippet"), systemImage: "plus")
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .rowPadding(.horizontal, 14)
                        .rowPadding(.vertical, 10)
                }
                .buttonStyle(HoverButtonStyle(cornerRadius: Theme.cardRadius))
            }
        }
    }
}

private struct SnippetRow: View {
    let snippet: Snippet
    let onEdit: () -> Void
    let onDelete: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Button(action: onEdit) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(snippet.trigger).font(.system(size: 13, weight: .medium))
                    Text(snippet.expansion.split(whereSeparator: \.isNewline).joined(separator: " ↵ "))
                        .font(.system(size: 11.5)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.tail)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            Button(action: onEdit) { Image(systemName: "pencil") }
                .buttonStyle(.borderless).foregroundStyle(.secondary)
                .help(L("编辑快捷短语", "Edit snippet"))
                .accessibilityLabel(L("编辑快捷短语", "Edit snippet"))
            Button(action: onDelete) { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless).foregroundStyle(.secondary)
                .help(L("删除", "Delete"))
                .accessibilityLabel(L("删除", "Delete"))
        }
        .rowPadding(.horizontal, 14)
        .rowPadding(.vertical, 8)
    }
}

/// A snippet being written: its trigger, its expansion (several lines if need be), and why it can't be saved yet.
private struct SnippetEditor: View {
    @ObservedObject var settings: AppSettings
    let original: Snippet
    let onDone: () -> Void
    @State private var trigger: String
    @State private var expansion: String

    init(settings: AppSettings, snippet: Snippet, onDone: @escaping () -> Void) {
        self.settings = settings
        original = snippet
        self.onDone = onDone
        _trigger = State(initialValue: snippet.trigger)
        _expansion = State(initialValue: snippet.expansion)
    }

    /// Another snippet already has this trigger (compared the way dictations are searched for it).
    private var isTaken: Bool {
        let key = SnippetExpander.key(trigger)
        return !key.isEmpty && settings.snippets.contains { $0.id != original.id && SnippetExpander.key($0.trigger) == key }
    }

    private var canSave: Bool {
        !SnippetExpander.key(trigger).isEmpty && !expansion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !isTaken
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            TextField(L("触发词，比如：我的邮箱", "Trigger, e.g. my email"), text: $trigger, prompt: Text(L("触发词，比如：我的邮箱", "Trigger, e.g. my email")))
                .labelsHidden()
                .multilineTextAlignment(.leading)
                .textFieldStyle(.roundedBorder)
                .onChange(of: trigger) {
                    if trigger.count > SnippetExpander.maxTriggerLength { trigger = String(trigger.prefix(SnippetExpander.maxTriggerLength)) }
                }
            TextEditor(text: $expansion)
                .font(.system(size: 13))
                .scrollContentBackground(.hidden)
                .padding(6)
                .frame(height: 100)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.04)))
                .overlay(alignment: .topLeading) {
                    if expansion.isEmpty {
                        Text(L("替换成的内容，可以多行", "What goes in instead (can be several lines)"))
                            .font(.system(size: 13)).foregroundStyle(.tertiary)
                            .padding(.horizontal, 11).padding(.vertical, 6)
                            .allowsHitTesting(false)
                    }
                }
                .onChange(of: expansion) {
                    if expansion.count > SnippetExpander.maxExpansionLength {
                        expansion = String(expansion.prefix(SnippetExpander.maxExpansionLength))
                    }
                }
            HStack(spacing: 8) {
                if isTaken {
                    Text(L("已经有这个触发词了", "That trigger is already in use"))
                        .font(.system(size: 11.5)).foregroundStyle(.orange)
                }
                Spacer()
                Button(L("取消", "Cancel"), action: onDone)
                Button(L("保存", "Save")) {
                    // The expansion exactly as written, indentation and final line break included.
                    settings.saveSnippet(Snippet(id: original.id, trigger: trigger.trimmingCharacters(in: .whitespacesAndNewlines),
                                                 expansion: expansion))
                    onDone()
                }
                .disabled(!canSave)
            }
            .controlSize(.small)
        }
        .rowPadding(.horizontal, 14)
        .rowPadding(.vertical, 10)
    }
}

private struct LearnedTermRow: View {
    let learned: LearnedTerm
    let onForget: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Text(learned.term).font(.system(size: 13, weight: .medium))
            Text(L("听成了 ", "heard as ") + learned.heardAs.map { "“\($0)”" }.joined(separator: L("、", ", ")))
                .font(.system(size: 11.5)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 8)
            Text(learned.date.appFormatted(date: .abbreviated)).font(.system(size: 11)).foregroundStyle(.tertiary)
            Button(action: onForget) { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless).foregroundStyle(.secondary)
                .help(L("从词汇表删除，以后不再自动学习", "Remove from the vocabulary and never learn it again"))
        }
        .rowPadding(.horizontal, 14)
        .rowPadding(.vertical, 8)
    }
}

extension DictationRecord.Status {
    var label: String {
        switch self {
        case .done: return L("完成", "Done")
        case .polishFailed: return L("未整理", "Not cleaned up")
        case .failed: return L("失败", "Failed")
        case .cancelled: return L("已取消", "Cancelled")
        case .recording, .processing: return L("进行中", "In progress")
        }
    }

    var color: Color {
        switch self {
        case .done: return .green
        case .polishFailed: return .orange
        case .failed: return .red
        case .cancelled: return .gray
        case .recording, .processing: return .blue
        }
    }
}

extension Double {
    var durationLabel: String {
        let seconds = Int(self.rounded())
        return seconds >= 60 ? String(format: "%d:%02d", seconds / 60, seconds % 60) : "\(seconds)s"
    }
}
