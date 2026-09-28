import AppKit
import Combine
import SwiftUI
import TypelessCore

// MARK: - General

struct GeneralPage: View {
    @ObservedObject var settings: AppSettings
    @State private var micGranted = Permissions.microphoneGranted
    @State private var axGranted = Permissions.accessibilityGranted
    @State private var launchAtLogin = LaunchAtLogin.isEnabled
    @State private var launchNeedsApproval = LaunchAtLogin.needsApproval
    @State private var launchError: String?
    private let timer = Timer.publish(every: 1.5, on: .main, in: .common).autoconnect()

    var body: some View {
        PageScaffold(page: .general) {
            CardSection(title: L("权限", "Permissions")) {
                CardRow(icon: "mic.fill", iconColor: .red, title: L("麦克风", "Microphone"),
                        subtitle: L("用来录下你的语音", "To record your voice")) {
                    PermissionControl(granted: micGranted) {
                        Permissions.requestMicrophone { _ in micGranted = Permissions.microphoneGranted }
                        if AVAuthorizationDenied.isDenied { Permissions.openMicrophoneSettings() }
                    }
                }
                CardDivider(inset: 50)
                CardRow(icon: "accessibility", iconColor: .blue, title: L("辅助功能", "Accessibility"),
                        subtitle: L("用来监听快捷键，并把文字粘贴到光标处", "To listen for the shortcut and paste text at the cursor")) {
                    PermissionControl(granted: axGranted) {
                        Permissions.promptAccessibility()
                        Permissions.openAccessibilitySettings()
                    }
                }
            }

            CardSection(title: L("外观", "Appearance")) {
                CardRow(icon: "globe", iconColor: .teal, title: L("界面语言", "Language")) {
                    Picker("", selection: $settings.appLanguage) {
                        Text(L("跟随系统", "System")).tag(AppLanguage.system)
                        Text("简体中文").tag(AppLanguage.zh)
                        Text("English").tag(AppLanguage.en)
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            }

            CardSection(title: L("启动", "Startup"), footer: launchFooter) {
                CardRow(icon: "power", iconColor: .green, title: L("登录时自动启动", "Open at login"),
                        subtitle: L("开机后在菜单栏待命", "Waits in the menu bar after you log in")) {
                    Toggle("", isOn: Binding(
                        get: { launchAtLogin },
                        set: { newValue in
                            do {
                                try LaunchAtLogin.set(newValue)
                                launchError = nil
                            } catch {
                                launchError = error.localizedDescription
                            }
                            refreshLaunchState()
                        }
                    ))
                    .toggleStyle(.switch)
                    .labelsHidden()
                }
            }

            CardSection(title: L("听写", "Dictation")) {
                CardRow(icon: "doc.on.clipboard.fill", iconColor: .brown, title: L("恢复剪贴板", "Restore clipboard"),
                        subtitle: L("插入文字后，把剪贴板恢复成原来的内容", "Put your previous clipboard back after inserting")) {
                    Toggle("", isOn: $settings.restoreClipboard).toggleStyle(.switch).labelsHidden()
                }
                CardDivider(inset: 50)
                CardRow(icon: "speaker.wave.2.fill", iconColor: .pink, title: L("提示音", "Sounds"),
                        subtitle: L("开始和结束录音时播放轻提示音", "Soft chime when recording starts and stops")) {
                    Toggle("", isOn: $settings.playSounds).toggleStyle(.switch).labelsHidden()
                }
                CardDivider(inset: 50)
                CardRow(icon: "timer", iconColor: .orange, title: L("单次最长录音", "Maximum recording"),
                        subtitle: L("到时间会自动结束并处理", "Stops and processes automatically at the limit")) {
                    Stepper(L("\(settings.maxRecordingMinutes) 分钟", "\(settings.maxRecordingMinutes) min"),
                            value: $settings.maxRecordingMinutes, in: 1...60)
                        .fixedSize()
                }
            }
        }
        .onReceive(timer) { _ in
            micGranted = Permissions.microphoneGranted
            axGranted = Permissions.accessibilityGranted
            refreshLaunchState()
        }
    }

    private var launchFooter: String? {
        if let launchError { return L("无法修改：", "Couldn't change it: ") + launchError }
        if launchNeedsApproval {
            return L("需要在「系统设置 → 通用 → 登录项」中允许 OpenTypeless。",
                     "Allow OpenTypeless in System Settings → General → Login Items.")
        }
        return nil
    }

    private func refreshLaunchState() {
        launchAtLogin = LaunchAtLogin.isEnabled
        launchNeedsApproval = LaunchAtLogin.needsApproval
    }
}

private enum AVAuthorizationDenied {
    static var isDenied: Bool { !Permissions.microphoneGranted && Permissions.microphoneAsked }
}

private struct PermissionControl: View {
    let granted: Bool
    let action: () -> Void

    var body: some View {
        if granted {
            StatusPill(text: L("已授权", "Granted"), color: .green)
        } else {
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

    private var monitor: Any?
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
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
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
                finish(Hotkey(keyCode: code, isModifierOnly: true))
            }
            return nil
        }

        sawKeyDown = true
        let mods = event.modifierFlags.intersection([.command, .option, .control, .shift])
        if code == 53, mods.isEmpty { stop(); return nil }
        if mods.isEmpty, Hotkey.functionKeys[code] == nil {
            message = L("单独的字母键会影响正常打字，请搭配 ⌃ ⌥ ⇧ ⌘ 使用，或者只按一个修饰键 / F1–F20。",
                        "A plain key would break normal typing — combine it with ⌃ ⌥ ⇧ ⌘, or press a single modifier / F1–F20.")
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

    var body: some View {
        let hotkey = settings.hotkey
        PageScaffold(page: .shortcut) {
            Card {
                VStack(spacing: 16) {
                    Text(recorder.isRecording ? L("请按下新的快捷键…", "Press the new shortcut…") : L("当前快捷键", "Current shortcut"))
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(.secondary)
                    if recorder.isRecording {
                        RecordingPlaceholder()
                    } else {
                        KeyCaps(caps: hotkey.keyCaps, large: true)
                    }
                    HStack(spacing: 10) {
                        if recorder.isRecording {
                            Button(L("取消", "Cancel")) { recorder.stop() }
                        } else {
                            Button {
                                recorder.onRecorded = { settings.hotkey = $0 }
                                recorder.start()
                            } label: {
                                Label(L("录制快捷键", "Record shortcut"), systemImage: "record.circle")
                            }
                            .buttonStyle(.glassProminent)
                        }
                    }
                    if let message = recorder.message {
                        Text(message).font(.system(size: 11.5)).foregroundStyle(.orange).multilineTextAlignment(.center)
                    } else {
                        Text(L("可以是单个修饰键（如 Fn、右 ⌘），也可以是组合键（如 ⌥ Space、F5）",
                               "A single modifier (Fn, right ⌘…) or a combination (⌥ Space, F5…)"))
                            .font(.system(size: 11.5)).foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 26)
            }

            CardSection(title: L("常用", "Presets")) {
                GlassEffectContainer(spacing: 10) {
                HStack(spacing: 10) {
                    ForEach(Hotkey.presets, id: \.keyCode) { preset in
                        PresetChip(hotkey: preset, selected: hotkey == preset) {
                            recorder.stop()
                            settings.hotkey = preset
                        }
                    }
                }
                }
                .padding(12)
            }

            CardSection(title: L("使用方法", "How it works")) {
                CardRow(icon: "hand.point.down.fill", iconColor: .blue, title: L("按住说话", "Hold to talk"),
                        subtitle: L("按住快捷键说话，松开后自动整理并插入到光标处",
                                    "Hold the shortcut while you speak; release to clean up and insert")) { EmptyView() }
                CardDivider(inset: 50)
                CardRow(icon: "hand.tap.fill", iconColor: .purple, title: L("轻点一下：免手持", "Tap once: hands-free"),
                        subtitle: L("适合长段口述，说完再按一次结束", "For long dictation — press again when done")) { EmptyView() }
                CardDivider(inset: 50)
                CardRow(icon: "escape", iconColor: .gray, title: L("Esc 取消", "Esc to cancel"),
                        subtitle: L("录音或处理过程中随时取消", "Cancel any time while recording or processing")) { EmptyView() }
            }

            if hotkey == .fn {
                Banner(symbol: "globe", color: .blue,
                       text: L("建议把「系统设置 → 键盘 → 按下 🌐 键时」设为「不执行任何操作」，避免轻点 Fn 时弹出表情面板。",
                               "Set System Settings → Keyboard → “Press 🌐 key to” → “Do Nothing”, so tapping Fn doesn’t open the emoji picker.")) {
                    Button(L("打开键盘设置", "Keyboard Settings")) { Permissions.openKeyboardSettings() }.controlSize(.small)
                }
            }
            if hotkey.shadowsCommonShortcut {
                Banner(symbol: "exclamationmark.triangle.fill", color: .orange,
                       text: L("这个组合会在所有应用里覆盖同名的系统快捷键。", "This combination overrides the same shortcut in every app.")) {
                    EmptyView()
                }
            }
        }
        .onDisappear { recorder.stop() }
    }
}

private struct RecordingPlaceholder: View {
    @State private var pulse = false

    var body: some View {
        RoundedRectangle(cornerRadius: 10, style: .continuous)
            .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
            .frame(width: 180, height: 48)
            .overlay(Circle().fill(.red).frame(width: 10, height: 10).opacity(pulse ? 1 : 0.3))
            .onAppear { withAnimation(.easeInOut(duration: 0.7).repeatForever()) { pulse = true } }
    }
}

private struct PresetChip: View {
    let hotkey: Hotkey
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 6) {
                Text(hotkey.displayName).font(.system(size: 13, weight: .semibold, design: .rounded))
                Text(hotkey == .fn ? L("默认", "Default") : " ")
                    .font(.system(size: 10.5))
                    .foregroundStyle(selected ? .white.opacity(0.85) : .secondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 8)
        }
        .buttonStyle(selected ? AnyPrimitiveButtonStyle(.glassProminent) : AnyPrimitiveButtonStyle(.glass))
        .buttonBorderShape(.roundedRectangle(radius: 14))
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

extension ProviderID {
    var symbol: String {
        switch self {
        case .openrouter: return "arrow.triangle.branch"
        case .openai: return "sparkle"
        case .groq: return "bolt.fill"
        case .siliconflow: return "drop.fill"
        case .deepseek: return "fish.fill"
        case .custom: return "server.rack"
        }
    }

    var color: Color {
        switch self {
        case .openrouter: return .indigo
        case .openai: return .teal
        case .groq: return .orange
        case .siliconflow: return .purple
        case .deepseek: return .blue
        case .custom: return .gray
        }
    }

    var capabilities: String {
        supportsSTT ? L("语音转文字 · 文字整理", "Speech-to-text · Clean-up") : L("文字整理", "Clean-up")
    }
}

struct ProvidersPage: View {
    @ObservedObject var settings: AppSettings
    @State private var expanded: Set<ProviderID> = []

    var body: some View {
        PageScaffold(page: .providers) {
            VStack(spacing: 10) {
                ForEach(ProviderID.allCases) { id in
                    ProviderCard(settings: settings, id: id, expanded: Binding(
                        get: { expanded.contains(id) },
                        set: { if $0 { expanded.insert(id) } else { expanded.remove(id) } }
                    ))
                }
            }
        }
        .onAppear {
            expanded = [settings.sttProvider, settings.polishProvider]
        }
    }
}

private struct ProviderCard: View {
    @ObservedObject var settings: AppSettings
    let id: ProviderID
    @Binding var expanded: Bool
    @State private var testResult: (ok: Bool, text: String)?
    @State private var testing = false

    var body: some View {
        Card {
            Button { withAnimation(.easeOut(duration: 0.18)) { expanded.toggle() } } label: {
                HStack(spacing: 12) {
                    IconBadge(symbol: id.symbol, color: id.color, size: 30)
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 6) {
                            Text(id.displayName).font(.system(size: 13.5, weight: .semibold))
                            ForEach(usageTags, id: \.self) { tag in
                                Text(tag)
                                    .font(.system(size: 10, weight: .medium))
                                    .padding(.horizontal, 6).padding(.vertical, 1.5)
                                    .background(Capsule().fill(Color.accentColor.opacity(0.14)))
                                    .foregroundStyle(Color.accentColor)
                            }
                        }
                        Text(id.capabilities).font(.system(size: 11.5)).foregroundStyle(.secondary)
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
                .padding(.horizontal, 14)
                .padding(.vertical, 12)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if expanded {
                CardDivider(inset: 0)
                VStack(alignment: .leading, spacing: 12) {
                    FieldRow(label: "API Key") {
                        SecureField(id.keyPlaceholder, text: Binding(
                            get: { settings.apiKey(for: id) },
                            set: { settings.setAPIKey($0, for: id) }
                        ))
                        .textFieldStyle(.roundedBorder)
                        Button(L("粘贴", "Paste")) {
                            if let text = NSPasteboard.general.string(forType: .string) { settings.setAPIKey(text, for: id) }
                        }
                    }
                    FieldRow(label: "Base URL") {
                        TextField(id == .custom ? "http://localhost:8000/v1" : id.defaultBaseURL, text: Binding(
                            get: { settings.baseURL(for: id) },
                            set: { settings.setBaseURL($0, for: id) }
                        ))
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
                            Text(L("任何兼容 OpenAI 接口的服务：/audio/transcriptions 与 /chat/completions",
                                   "Any OpenAI-compatible server: /audio/transcriptions and /chat/completions"))
                                .font(.system(size: 11.5)).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if let testResult {
                            Label(testResult.text, systemImage: testResult.ok ? "checkmark.circle.fill" : "xmark.octagon.fill")
                                .font(.system(size: 11.5))
                                .foregroundStyle(testResult.ok ? .green : .red)
                                .lineLimit(2)
                                .help(testResult.text)
                        }
                        Button(testing ? L("测试中…", "Testing…") : L("测试连接", "Test")) { test() }
                            .disabled(testing || settings.endpoint(for: id) == nil)
                    }
                }
                .padding(14)
            }
        }
    }

    private var usageTags: [String] {
        var tags: [String] = []
        if settings.sttProvider == id { tags.append(L("转写中使用", "Speech-to-text")) }
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
                testResult = (true, try await APIClient(endpoint: endpoint).verifyCredentials())
            } catch {
                testResult = (false, APIError.from(error).localizedDescription)
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
                .frame(width: 70, alignment: .leading)
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

    var body: some View {
        PageScaffold(page: .models) {
            CardSection(title: L("语音转文字", "Speech to text"),
                        footer: L("长语音会在停顿处自动切成 ≤28 秒的片段，边录边转，每片失败会自动重试。",
                                  "Long recordings are split at pauses into ≤28 s segments that are transcribed while you talk, each retried on failure.")) {
                CardRow(icon: "building.2.fill", iconColor: .indigo, title: L("服务商", "Provider")) {
                    ProviderPicker(selection: settings.sttProvider, options: ProviderID.allCases.filter(\.supportsSTT),
                                   settings: settings) { settings.selectSTTProvider($0) }
                }
                CardDivider(inset: 50)
                CardRow(icon: "waveform", iconColor: .blue, title: L("模型", "Model")) {
                    ModelField(text: $settings.sttModel, models: sttModels)
                }
                CardDivider(inset: 50)
                CardRow(icon: "character.bubble.fill", iconColor: .teal, title: L("说话语言", "Spoken language"),
                        subtitle: L("固定语言可以提高准确率；中英混说请选自动", "Fixing it can help accuracy; use Auto for mixed speech")) {
                    Picker("", selection: $settings.sttLanguage) {
                        Text(L("自动检测", "Auto-detect")).tag("")
                        Text("中文").tag("zh")
                        Text("English").tag("en")
                        Text("日本語").tag("ja")
                        Text("한국어").tag("ko")
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            }
            if !settings.isConfigured(settings.sttProvider) {
                setupBanner(for: settings.sttProvider)
            }

            CardSection(title: L("文字整理", "Clean-up")) {
                CardRow(icon: "wand.and.stars", iconColor: .purple, title: L("用 AI 整理文字", "Clean up with AI"),
                        subtitle: L("去掉口头禅和自我修正，自动分段、整理成列表", "Removes fillers and false starts, adds paragraphs and lists")) {
                    Toggle("", isOn: $settings.polishEnabled).toggleStyle(.switch).labelsHidden()
                }
                if settings.polishEnabled {
                    CardDivider(inset: 50)
                    CardRow(icon: "building.2.fill", iconColor: .indigo, title: L("服务商", "Provider")) {
                        ProviderPicker(selection: settings.polishProvider, options: ProviderID.allCases,
                                       settings: settings) { settings.selectPolishProvider($0) }
                    }
                    CardDivider(inset: 50)
                    CardRow(icon: "cpu.fill", iconColor: .pink, title: L("模型", "Model"),
                            subtitle: settings.polishProvider == .openrouter
                                ? L("推理会自动关闭或降到最低，以减少延迟", "Reasoning is turned off or minimised for speed") : nil) {
                        ModelField(text: $settings.polishModel, models: chatModels)
                    }
                    CardDivider(inset: 50)
                    CardRow(icon: "arrow.triangle.branch", iconColor: .orange, title: L("备用模型", "Backup model"),
                            subtitle: L("首选模型 \(hedgeDelayLabel) 内还没开始输出、或请求失败时，同时请求备用模型，用先出字的那个",
                                        "If the main model hasn't started answering within \(hedgeDelayLabel), or fails, the backup is asked too and the first to answer wins")) {
                        Toggle("", isOn: $settings.polishBackupEnabled).toggleStyle(.switch).labelsHidden()
                    }
                    if settings.polishBackupEnabled {
                        CardDivider(inset: 50)
                        CardRow(icon: "building.2", iconColor: .indigo, title: L("备用服务商", "Backup provider")) {
                            ProviderPicker(selection: settings.polishBackupProvider, options: ProviderID.allCases,
                                           settings: settings) { settings.selectPolishBackupProvider($0) }
                        }
                        CardDivider(inset: 50)
                        CardRow(icon: "cpu", iconColor: .pink, title: L("备用模型 ID", "Backup model ID"),
                                subtitle: L("选一个不同厂商的快速模型，两边不容易同时变慢", "Pick a fast model from another vendor so both are rarely slow at once")) {
                            ModelField(text: $settings.polishBackupModel, models: backupChatModels)
                        }
                    }
                }
            }
            if settings.polishEnabled, !settings.isConfigured(settings.polishProvider) {
                setupBanner(for: settings.polishProvider)
            } else if settings.polishEnabled, settings.polishBackupEnabled, settings.polishBackupProvider != settings.polishProvider,
                      !settings.isConfigured(settings.polishBackupProvider) {
                setupBanner(for: settings.polishBackupProvider)
            }
        }
        .task(id: "\(settings.sttProvider.rawValue)|\(settings.apiKey(for: settings.sttProvider).count)") {
            sttModels = await loadModels(settings.sttProvider, speech: true)
        }
        .task(id: "\(settings.polishProvider.rawValue)|\(settings.apiKey(for: settings.polishProvider).count)") {
            chatModels = await loadModels(settings.polishProvider, speech: false)
        }
        .task(id: "\(settings.polishBackupProvider.rawValue)|\(settings.apiKey(for: settings.polishBackupProvider).count)") {
            backupChatModels = await loadModels(settings.polishBackupProvider, speech: false)
        }
        .onChange(of: settings.polishModel) { controller.refreshModelInfo() }
        .onChange(of: settings.polishProvider) { controller.refreshModelInfo() }
        .onChange(of: settings.polishBackupProvider) { controller.refreshModelInfo() }
        .onChange(of: settings.polishBackupEnabled) { controller.refreshModelInfo() }
    }

    private var hedgeDelayLabel: String {
        HedgedPolish.defaultHedgeDelay.formatted(.number.precision(.fractionLength(0...1))) + L(" 秒", " s")
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

private struct ProviderPicker: View {
    let selection: ProviderID
    let options: [ProviderID]
    @ObservedObject var settings: AppSettings
    let onSelect: (ProviderID) -> Void

    var body: some View {
        Menu {
            ForEach(options) { id in
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
        } label: {
            Text(selection.displayName)
        }
        .fixedSize()
    }
}

/// Free-text model ID with a browse menu of the provider's models (grouped by vendor when long).
private struct ModelField: View {
    @Binding var text: String
    let models: [APIClient.ModelInfo]

    var body: some View {
        HStack(spacing: 6) {
            TextField(L("模型 ID", "Model ID"), text: $text)
                .textFieldStyle(.roundedBorder)
                .font(.system(size: 12, design: .monospaced))
                .frame(width: 270)
            Menu {
                if models.isEmpty {
                    Text(L("填写 API Key 后可浏览模型", "Add an API key to browse models"))
                } else if models.count > 40 {
                    ForEach(groups, id: \.0) { vendor, items in
                        Menu(vendor) {
                            ForEach(items) { model in Button(model.id) { text = model.id } }
                        }
                    }
                } else {
                    ForEach(models) { model in Button(model.id) { text = model.id } }
                }
            } label: {
                Image(systemName: "list.bullet")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help(L("浏览可用模型", "Browse models"))
        }
    }

    private var groups: [(String, [APIClient.ModelInfo])] {
        let grouped = Dictionary(grouping: models) { $0.id.split(separator: "/").first.map(String.init) ?? $0.id }
        return grouped.keys.sorted().map { ($0, grouped[$0]!) }
    }
}

// MARK: - Style

struct StylePage: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        PageScaffold(page: .style) {
            CardSection(title: L("专有名词 / 词汇表", "Vocabulary"),
                        footer: L("用逗号或换行分隔。整理时会按这里的写法纠正识别错误，比如人名、产品名、项目名。",
                                  "Comma- or line-separated. Clean-up uses these spellings to fix recognition errors — names, products, projects.")) {
                TextEditor(text: $settings.vocabulary)
                    .font(.system(size: 13))
                    .scrollContentBackground(.hidden)
                    .padding(10)
                    .frame(height: 110)
            }
            CardSection(title: L("自动学习", "Learning"),
                        footer: L("粘贴后如果你在输入框里改了某个识别错的词（比如把 TypeList 改成 Typeless），离开输入框或发送后会自动把它加进词汇表。只学发音相近的改动，不学改写、改数字和普通词替换。输入框内容只在本机读取。",
                                  "If you fix a misrecognised word after pasting (say TypeList → Typeless), it's added to the vocabulary once you leave the field or send. Only sound-alike fixes are learned, never rewrites, changed numbers or ordinary word swaps. The field is read on this Mac only.")) {
                CardRow(icon: "character.book.closed.fill", iconColor: .orange, title: L("从我的修改中学习", "Learn from my corrections")) {
                    Toggle("", isOn: $settings.learnFromEdits).toggleStyle(.switch).labelsHidden()
                }
                if !settings.learnedTerms.isEmpty {
                    ForEach(settings.learnedTerms.reversed()) { learned in
                        CardDivider(inset: 14)
                        LearnedTermRow(learned: learned) { settings.forget(learned) }
                    }
                }
            }
            CardSection(title: L("整理偏好（可选）", "Clean-up preferences (optional)"),
                        footer: L("例如：「不要使用列表」「保留我的口语语气」「英文术语保持小写」。",
                                  "e.g. “Never use bullet lists”, “Keep my casual tone”, “Keep technical terms lowercase”.")) {
                TextEditor(text: $settings.extraInstructions)
                    .font(.system(size: 13))
                    .scrollContentBackground(.hidden)
                    .padding(10)
                    .frame(height: 150)
            }
        }
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
            Text(learned.date.formatted(date: .abbreviated, time: .omitted)).font(.system(size: 11)).foregroundStyle(.tertiary)
            Button(action: onForget) { Image(systemName: "xmark.circle.fill") }
                .buttonStyle(.borderless).foregroundStyle(.secondary)
                .help(L("从词汇表删除，以后不再自动学习", "Remove from the vocabulary and never learn it again"))
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
    }
}

// MARK: - History

struct HistoryPage: View {
    @ObservedObject var history: HistoryStore
    @ObservedObject var settings: AppSettings
    let controller: SessionController
    @State private var selection: DictationRecord.ID?
    @State private var storageBytes: Int64?

    var body: some View {
        PageScaffold(page: .history, scrolls: false) {
            Card {
                CardRow(icon: "externaldrive.fill", iconColor: .gray, title: L("录音保存时间", "Keep recordings"),
                        subtitle: retentionSubtitle) {
                    Picker("", selection: $settings.historyRetention) {
                        ForEach(HistoryRetention.allCases) { Text($0.label).tag($0) }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            }
            .onChange(of: settings.historyRetention) { history.applyRetention() }
            .task(id: history.changeCount) {
                storageBytes = await Task.detached { HistoryStore.storageBytes() }.value
            }

            if history.records.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "waveform.badge.mic").font(.system(size: 36)).foregroundStyle(.tertiary)
                    Text(L("还没有听写记录", "No dictations yet")).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                HStack(alignment: .top, spacing: 14) {
                    Card {
                        ScrollView {
                            LazyVStack(spacing: 2) {
                                ForEach(history.records) { record in
                                    HistoryRow(record: record, selected: record.id == selectedID) { selection = record.id }
                                }
                            }
                            .padding(6)
                        }
                    }
                    .frame(width: 250)

                    if let record = history.records.first(where: { $0.id == selectedID }) {
                        HistoryDetail(record: record, controller: controller, history: history)
                    }
                }
                .frame(maxHeight: .infinity)
            }
        }
    }

    private var selectedID: DictationRecord.ID? { selection ?? history.records.first?.id }

    private var retentionSubtitle: String {
        let used = storageBytes.map {
            L("目前占用 ", "Using ") + ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) + L("。", ". ")
        } ?? ""
        return used + L("每分钟录音约 1.9 MB。转写失败的录音会一直保留，方便重试。",
                        "Recordings take about 1.9 MB per minute. Failed dictations keep theirs so you can retry.")
    }
}

private struct HistoryRow: View {
    let record: DictationRecord
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Circle().fill(record.status.color).frame(width: 6, height: 6)
                    Text(record.date.shortStamp).font(.system(size: 11)).foregroundStyle(.secondary)
                    Spacer()
                    Text(record.duration.durationLabel).font(.system(size: 11)).foregroundStyle(.tertiary)
                }
                Text(record.finalText.isEmpty ? (record.error ?? L("（空）", "(empty)")) : record.finalText)
                    .font(.system(size: 12.5))
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .foregroundStyle(record.finalText.isEmpty ? .secondary : .primary)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(selected ? Color.accentColor.opacity(0.14) : .clear)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

private struct HistoryDetail: View {
    let record: DictationRecord
    let controller: SessionController
    let history: HistoryStore

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text(record.date.formatted(date: .abbreviated, time: .shortened)).font(.system(size: 12, weight: .medium))
                    Text("·").foregroundStyle(.tertiary)
                    Text(record.duration.durationLabel).font(.system(size: 12)).foregroundStyle(.secondary)
                    Spacer()
                    StatusPill(text: record.status.label, color: record.status.color)
                }
                if let error = record.error {
                    Banner(symbol: "exclamationmark.triangle.fill", color: .orange, text: error) { EmptyView() }
                }
                if let timing = record.timing?.summary {
                    Text(timing).font(.system(size: 11)).foregroundStyle(.secondary).textSelection(.enabled)
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        if let polished = record.polishedText {
                            TextBlock(title: L("整理后", "Cleaned up"), text: polished)
                        }
                        TextBlock(title: L("原始转写", "Raw transcript"), text: record.rawText)
                    }
                }
                HStack(spacing: 8) {
                    Button(L("复制", "Copy")) { TextInserter.copyToClipboard(record.finalText) }
                        .disabled(record.finalText.isEmpty)
                    if record.hasAudio {
                        Button(L("重新转写", "Re-transcribe")) { controller.retry(record, paste: false) }
                            .help(L("完成后复制到剪贴板", "Copies the result to the clipboard"))
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([record.audioURL])
                        } label: { Image(systemName: "folder") }
                            .help(L("在访达中显示录音", "Show recording in Finder"))
                    }
                    Spacer()
                    Button(role: .destructive) { history.delete(record) } label: { Image(systemName: "trash") }
                        .help(L("删除", "Delete"))
                }
                .controlSize(.small)
            }
            .padding(16)
        }
        .frame(maxHeight: .infinity, alignment: .top)
    }
}

private struct TextBlock: View {
    let title: String
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            Text(text.isEmpty ? L("（空）", "(empty)") : text)
                .font(.system(size: 13))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.04)))
        }
    }
}

extension DictationRecord.Status {
    var label: String {
        switch self {
        case .done: return L("完成", "Done")
        case .polishFailed: return L("未整理", "Not cleaned up")
        case .failed: return L("失败", "Failed")
        case .recording, .processing: return L("进行中", "In progress")
        }
    }

    var color: Color {
        switch self {
        case .done: return .green
        case .polishFailed: return .orange
        case .failed: return .red
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
