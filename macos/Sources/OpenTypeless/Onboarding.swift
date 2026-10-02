import AppKit
import Combine
import SwiftUI
import TypelessCore

/// The guide on the very first launch: welcome, an API key, a first dictation, done. It is shown once; whether it
/// was finished, skipped or closed, later launches start as usual.
enum Onboarding {
    static let shownKey = "didShowOnboarding"

    /// Whether this launch shows the guide. Someone updating from a version without it already has a working
    /// setup, so for them it's marked as seen instead.
    static func shouldShow(_ settings: AppSettings) -> Bool {
        guard !UserDefaults.standard.bool(forKey: shownKey) else { return false }
        if settings.isConfigured(settings.sttProvider) {
            markShown()
            return false
        }
        return true
    }

    static func markShown() { UserDefaults.standard.set(true, forKey: shownKey) }

    /// The providers offered, recommended first. Each of them does both steps; the rest are in Settings → Providers.
    static let providers: [ProviderID] = [.openrouter, .openai, .groq, .siliconflow]

    /// With an OpenRouter key: Microsoft MAI-Transcribe for speech-to-text, Gemini 3.1 Flash Lite (the fastest
    /// clean-up model in eval/README.md) for clean-up.
    static func models(for id: ProviderID) -> (stt: String, chat: String) {
        id == .openrouter ? ("microsoft/mai-transcribe-2", "google/gemini-3.1-flash-lite") : (id.defaultSTTModel, id.defaultChatModel)
    }

    /// Uses this provider and key for both speech-to-text and clean-up.
    static func apply(_ id: ProviderID, key: String, to settings: AppSettings) {
        let models = models(for: id)
        settings.setAPIKey(key, for: id)
        settings.sttProvider = id
        settings.sttModel = models.stt
        settings.polishProvider = id
        settings.polishModel = models.chat
    }
}

enum OnboardingStep: Int, CaseIterable {
    case welcome, provider, tryIt, done
}

/// The guide's own window; closing it at any step ends the guide.
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    private var window: NSWindow?
    private let controller: SessionController
    private let onClose: () -> Void

    init(controller: SessionController, onClose: @escaping () -> Void) {
        self.controller = controller
        self.onClose = onClose
    }

    var isShown: Bool { window != nil }

    func bringToFront() {
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    func show() {
        // Marked as soon as it appears, so it never comes back, however this launch ends.
        Onboarding.markShown()
        let view = OnboardingView(controller: controller, finish: { [weak self] in self?.window?.close() })
        let window = Self.makeWindow(view)
        window.delegate = self
        window.center()
        self.window = window
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    static func makeWindow(_ view: OnboardingView) -> NSWindow {
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: OnboardingView.size),
            styleMask: [.titled, .closable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.title = "OpenTypeless"
        window.isMovableByWindowBackground = true
        window.contentViewController = NSHostingController(rootView: view)
        window.setContentSize(OnboardingView.size)
        window.isReleasedWhenClosed = false
        return window
    }

    func windowWillClose(_ notification: Notification) {
        window = nil
        onClose()
    }
}

struct OnboardingView: View {
    static let size = NSSize(width: 640, height: 580)

    @ObservedObject private var settings = AppSettings.shared
    private let controller: SessionController
    @State private var step: OnboardingStep
    @State private var provider: ProviderID = .openrouter
    @State private var key = ""
    private let finish: () -> Void

    init(controller: SessionController, step: OnboardingStep = .welcome, finish: @escaping () -> Void) {
        self.controller = controller
        _step = State(initialValue: step)
        self.finish = finish
    }

    private var trimmedKey: String { key.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        VStack(spacing: 0) {
            StepDots(current: step).padding(.top, 38)
            Group {
                switch step {
                case .welcome: WelcomeStep(next: { go(.provider) })
                case .provider: ProviderStep(settings: settings, provider: $provider, key: $key, save: saveKey)
                case .tryIt: TryStep(settings: settings, controller: controller, addKey: { go(.provider) }, next: { go(.done) })
                case .done: DoneStep(settings: settings)
                }
            }
            .id(step)
            .transition(.asymmetric(insertion: .move(edge: .trailing).combined(with: .opacity),
                                    removal: .move(edge: .leading).combined(with: .opacity)))
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            footer
        }
        .frame(width: Self.size.width, height: Self.size.height)
        .background(alignment: .top) {
            // A soft wash of the brand colours behind the top of every step.
            Ellipse()
                .fill(Theme.brand)
                .frame(width: 560, height: 260)
                .blur(radius: 90)
                .opacity(0.22)
                .offset(y: -150)
        }
        .clipped()
        .ignoresSafeArea()
        .buttonStyle(.glass)
    }

    @ViewBuilder private var footer: some View {
        HStack {
            switch step {
            case .welcome:
                Spacer()
                SkipButton(title: L("跳过引导", "Skip the tour"), action: finish)
                Spacer()
            case .provider:
                SkipButton(title: L("稍后设置", "Set up later"), action: { go(.tryIt) })
                Spacer()
                Button(L("继续", "Continue"), action: saveKey)
                    .buttonStyle(.glassProminent)
                    .controlSize(.large)
                    .keyboardShortcut(.defaultAction)
                    .disabled(trimmedKey.isEmpty)
            case .tryIt:
                SkipButton(title: L("跳过", "Skip"), action: { go(.done) })
                Spacer()
                Button(L("继续", "Continue")) { go(.done) }.buttonStyle(.glassProminent).controlSize(.large)
            case .done:
                Spacer()
                Button(L("开始使用", "Start dictating"), action: finish).buttonStyle(.glassProminent).controlSize(.large)
            }
        }
        .padding(.horizontal, 28)
        .padding(.bottom, 24)
        .frame(height: 76)
    }

    private func saveKey() {
        guard !trimmedKey.isEmpty else { return }
        Onboarding.apply(provider, key: trimmedKey, to: settings)
        controller.refreshModelInfo()
        go(.tryIt)
    }

    private func go(_ next: OnboardingStep) {
        withAnimation(.smooth(duration: 0.35)) { step = next }
    }
}

private struct StepDots: View {
    let current: OnboardingStep

    var body: some View {
        HStack(spacing: 7) {
            ForEach(OnboardingStep.allCases, id: \.self) { step in
                Capsule()
                    .fill(step == current ? AnyShapeStyle(Theme.brand) : AnyShapeStyle(Color.primary.opacity(0.15)))
                    .frame(width: step == current ? 22 : 7, height: 7)
            }
        }
        .animation(.smooth(duration: 0.35), value: current)
        .accessibilityElement()
        .accessibilityLabel(L("第 \(current.rawValue + 1) 步，共 \(OnboardingStep.allCases.count) 步",
                              "Step \(current.rawValue + 1) of \(OnboardingStep.allCases.count)"))
    }
}

private struct SkipButton: View {
    let title: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
        }
        .buttonStyle(HoverButtonStyle())
    }
}

/// Title and subtitle at the top of a step.
private struct StepHeader: View {
    let title: String
    let subtitle: String

    var body: some View {
        VStack(spacing: 8) {
            Text(title).font(.system(size: 26, weight: .bold))
            Text(subtitle)
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 520)
        }
    }
}

// MARK: - Welcome

private struct WelcomeStep: View {
    let next: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            Spacer()
            AppTile(size: 104)
            Text("OpenTypeless")
                .font(.system(size: 38, weight: .bold))
                .padding(.top, 26)
            Text(L("按住一个键说话，松开后整理好的文字就出现在光标处。", "Hold a key and speak. Let go, and clean text appears at your cursor."))
                .font(.system(size: 14))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.top, 10)
            HStack(spacing: 6) {
                Image(systemName: "chevron.left.forwardslash.chevron.right")
                Text(L("完全开源 · MIT 许可", "Fully open source · MIT licensed"))
            }
            .font(.system(size: 12, weight: .medium))
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .glassEffect(.regular, in: .capsule)
            .padding(.top, 18)
            Spacer()
            Button(action: next) {
                Image(systemName: "arrow.right")
                    .font(.system(size: 20, weight: .semibold))
                    .frame(width: 34, height: 34)
            }
            .buttonStyle(.glassProminent)
            .buttonBorderShape(.circle)
            .controlSize(.large)
            .keyboardShortcut(.defaultAction)
            .help(L("继续", "Continue"))
            .accessibilityLabel(L("继续", "Continue"))
            Spacer().frame(height: 18)
        }
        .padding(.horizontal, 40)
    }
}

// MARK: - Provider

private struct ProviderStep: View {
    @ObservedObject var settings: AppSettings
    @Binding var provider: ProviderID
    @Binding var key: String
    let save: () -> Void

    var body: some View {
        VStack(spacing: 22) {
            StepHeader(title: L("连接服务商", "Connect a provider"),
                       subtitle: L("录音会用你自己的 API Key 发给你选的服务商转写和整理。推荐 OpenRouter：一个 Key 就能同时用于两步。",
                                   "Your recording goes to a provider you choose, with your own API key. OpenRouter is recommended: one key covers both transcription and clean-up."))
                .padding(.top, 22)

            GlassEffectContainer(spacing: 8) {
                HStack(spacing: 8) {
                    ForEach(Onboarding.providers) { id in
                        ProviderChip(id: id, selected: provider == id) { provider = id }
                    }
                }
            }

            Card {
                HStack(spacing: 8) {
                    SecureField(provider.keyPlaceholder, text: $key)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit(save)
                    Button(L("粘贴", "Paste")) {
                        if let text = NSPasteboard.general.string(forType: .string) { key = text }
                    }
                }
                .padding(14)
                if let url = provider.keysURL {
                    CardDivider(inset: 0)
                    HStack(spacing: 6) {
                        Text(L("还没有 Key？", "No key yet?")).foregroundStyle(.secondary)
                        Link(destination: url) {
                            Label(L("去 \(provider.displayName) 创建一个", "Create one at \(provider.displayName)"), systemImage: "arrow.up.right.square")
                        }
                        Spacer()
                    }
                    .font(.system(size: 12))
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
                }
            }

            ModelSummary(provider: provider)
        }
        .padding(.horizontal, 44)
        .frame(maxHeight: .infinity, alignment: .top)
        // A key saved earlier (coming back from the next step) is filled in again.
        .onChange(of: provider, initial: true) { key = settings.apiKey(for: provider) }
    }
}

private struct ProviderChip: View {
    let id: ProviderID
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Text(id == .siliconflow ? L("硅基流动", "SiliconFlow") : id.displayName)
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(1)
                Text(id == .openrouter ? L("推荐", "Recommended") : " ")
                    .font(.system(size: 10.5))
                    .foregroundStyle(selected ? .white.opacity(0.85) : .secondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
        }
        .buttonStyle(selected ? AnyPrimitiveButtonStyle(.glassProminent) : AnyPrimitiveButtonStyle(.glass))
        .buttonBorderShape(.roundedRectangle(radius: 14))
    }
}

/// The two models the chosen provider will be set up with.
private struct ModelSummary: View {
    let provider: ProviderID

    var body: some View {
        let models = Onboarding.models(for: provider)
        VStack(alignment: .leading, spacing: 8) {
            Card {
                row(icon: "waveform", color: .blue, title: L("语音转文字", "Speech-to-text"), model: models.stt)
                CardDivider(inset: 50)
                row(icon: "sparkles", color: .purple, title: L("文字整理", "Clean-up"), model: models.chat)
            }
            Text(L("之后可以随时在「设置 → 模型」里更换。", "You can change them any time in Settings → Models."))
                .font(.system(size: 11.5))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 4)
        }
    }

    private func row(icon: String, color: Color, title: String, model: String) -> some View {
        HStack(spacing: 12) {
            IconBadge(symbol: icon, color: color, size: 24)
            Text(title).font(.system(size: 13))
            Spacer()
            Text(model).font(.system(size: 12, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
    }
}

// MARK: - Try it

private struct TryStep: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject var controller: SessionController
    let addKey: () -> Void
    let next: () -> Void
    @ObservedObject private var history = HistoryStore.shared
    @State private var text = ""
    @State private var error: String?
    @State private var started = Date()
    @State private var micGranted = Permissions.microphoneGranted
    @State private var axGranted = Permissions.accessibilityGranted
    @FocusState private var focused: Bool
    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(spacing: 18) {
            StepHeader(title: L("试一试", "Try it out"),
                       subtitle: L("点一下下面的框，按住 \(settings.hotkey.displayName) 说一句话，松开后文字就会出现在这里。",
                                   "Click in the box below, hold \(settings.hotkey.displayName) and say something. Let go, and your words appear here."))
                .padding(.top, 22)

            KeyCaps(caps: settings.hotkey.keyCaps, large: true)

            if !micGranted || !axGranted {
                Card {
                    if !micGranted {
                        CardRow(icon: "mic.fill", iconColor: .red, title: L("麦克风", "Microphone"),
                                subtitle: L("用来录下你的语音", "To record your voice")) {
                            Button(L("授权…", "Grant…")) {
                                if Permissions.microphoneAsked { Permissions.openMicrophoneSettings() }
                                Permissions.requestMicrophone { _ in micGranted = Permissions.microphoneGranted }
                            }
                            .controlSize(.small)
                        }
                    }
                    if !micGranted && !axGranted { CardDivider(inset: 50) }
                    if !axGranted {
                        CardRow(icon: "accessibility", iconColor: .blue, title: L("辅助功能", "Accessibility"),
                                subtitle: L("用来监听快捷键，并把文字粘贴到光标处", "To listen for the shortcut and paste text at the cursor")) {
                            Button(L("授权…", "Grant…")) {
                                Permissions.promptAccessibility()
                                Permissions.openAccessibilitySettings()
                            }
                            .controlSize(.small)
                        }
                    }
                }
            } else if !settings.isConfigured(settings.sttProvider) {
                Banner(symbol: "key.fill", color: .orange,
                       text: L("还没有填写 API Key，听写暂时用不了。", "There's no API key yet, so dictation can't work.")) {
                    Button(L("填写 API Key", "Add a key"), action: addKey).controlSize(.small)
                }
            }

            ZStack(alignment: .topLeading) {
                TextEditor(text: $text)
                    .font(.system(size: 14))
                    .scrollContentBackground(.hidden)
                    .scrollIndicators(.never)
                    .focused($focused)
                    .padding(10)
                if text.isEmpty {
                    Text(L("你说的话会出现在这里…", "Your words will appear here…"))
                        .font(.system(size: 14))
                        .foregroundStyle(.tertiary)
                        .padding(.horizontal, 15)
                        .padding(.vertical, 10)
                        .allowsHitTesting(false)
                }
            }
            .frame(height: 104)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color(nsColor: .textBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(focused ? Color.accentColor.opacity(0.6) : Color.primary.opacity(0.12), lineWidth: focused ? 2 : 1))

            status.frame(height: 18)

            if settings.hotkey == .fn {
                Text(L("轻点 Fn 会弹出表情面板？", "Tapping Fn opens the emoji picker?"))
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
                + Text(" ")
                + Text(L("在「键盘设置」里把「按下 🌐 键时」设为「不执行任何操作」。",
                         "In Keyboard Settings, set “Press 🌐 key to” to “Do Nothing”."))
                    .font(.system(size: 11.5))
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, 44)
        .frame(maxHeight: .infinity, alignment: .top)
        .onAppear {
            started = Date()
            focused = true
            if !Permissions.microphoneAsked { Permissions.requestMicrophone { _ in micGranted = Permissions.microphoneGranted } }
        }
        .onReceive(timer) { _ in
            micGranted = Permissions.microphoneGranted
            axGranted = Permissions.accessibilityGranted
        }
        .onChange(of: history.changeCount) { checkLatest() }
    }

    @ViewBuilder private var status: some View {
        switch controller.state {
        case .recording:
            Label(L("正在听…", "Listening…"), systemImage: "waveform").foregroundStyle(.red)
                .font(.system(size: 12, weight: .medium))
        case .processing:
            Label(L("正在整理…", "Writing…"), systemImage: "ellipsis").foregroundStyle(.secondary)
                .font(.system(size: 12, weight: .medium))
        case .idle:
            if let error {
                Label(error, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.red)
                    .font(.system(size: 12)).lineLimit(1).help(error)
            } else if !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                Label(L("成功了！在任何能打字的地方都可以这样用。", "It works! Use it anywhere you can type."), systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green)
                    .font(.system(size: 12, weight: .medium))
            }
        }
    }

    /// The paste normally lands in the box; if it didn't, the dictation is shown from History instead.
    private func checkLatest() {
        guard let latest = history.records.first, latest.date >= started else { return }
        switch latest.status {
        case .done, .polishFailed:
            error = nil
            if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { text = latest.finalText }
        case .failed:
            error = latest.error
        default:
            break
        }
    }
}

// MARK: - Done

private struct DoneStep: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        VStack(spacing: 22) {
            Image(systemName: "checkmark")
                .font(.system(size: 36, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 84, height: 84)
                .background(Circle().fill(LinearGradient(colors: [.green.opacity(0.8), .green], startPoint: .top, endPoint: .bottom)))
                .shadow(color: .green.opacity(0.35), radius: 12, y: 4)
                .padding(.top, 34)
            StepHeader(title: L("一切就绪", "You're all set"),
                       subtitle: L("OpenTypeless 在菜单栏里待命。在任何应用里按住 \(settings.hotkey.displayName) 就能听写。",
                                   "OpenTypeless waits in the menu bar. Hold \(settings.hotkey.displayName) in any app to dictate."))
            Card {
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
        }
        .padding(.horizontal, 44)
        .frame(maxHeight: .infinity, alignment: .top)
    }
}
