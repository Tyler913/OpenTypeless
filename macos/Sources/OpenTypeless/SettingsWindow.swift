import AppKit
import SwiftUI
import TypelessCore

enum SettingsPage: String, CaseIterable, Identifiable {
    case home, general, shortcut, providers, models, style, history

    var id: String { rawValue }

    var title: String {
        switch self {
        case .home: return L("主页", "Home")
        case .general: return L("通用", "General")
        case .shortcut: return L("快捷键", "Shortcut")
        case .providers: return L("服务商", "Providers")
        case .models: return L("模型", "Models")
        case .style: return L("词汇与风格", "Vocabulary & Style")
        case .history: return L("历史记录", "History")
        }
    }

    var subtitle: String {
        switch self {
        case .home: return L("你用语音写了多少、省了多少时间、花了多少钱", "What you've dictated, the time it saved and what it cost")
        case .general: return L("界面语言、开机启动与系统权限", "Language, startup and system permissions")
        case .shortcut: return L("选择用来开始听写的按键", "Choose the key that starts dictation")
        case .providers: return L("填写 API Key。语音转文字和文字整理可以使用不同的服务商。",
                                  "Add API keys. Speech-to-text and clean-up can use different providers.")
        case .models: return L("为语音转文字和文字整理分别选择模型", "Pick a model for each step")
        case .style: return L("让整理结果更符合你的用词和习惯", "Teach the clean-up your terms and preferences")
        case .history: return L("每次听写都会保存，失败的可以重试", "Every dictation is saved; failed ones can be retried")
        }
    }

    var symbol: String {
        switch self {
        case .home: return "house.fill"
        case .general: return "gearshape.fill"
        case .shortcut: return "keyboard.fill"
        case .providers: return "key.fill"
        case .models: return "cpu.fill"
        case .style: return "textformat"
        case .history: return "clock.fill"
        }
    }

    var color: Color {
        switch self {
        case .home: return .blue
        case .general: return .gray
        case .shortcut: return .blue
        case .providers: return .orange
        case .models: return .purple
        case .style: return .green
        case .history: return .indigo
        }
    }
}

@MainActor
final class SettingsNavigation: ObservableObject {
    @Published var page: SettingsPage = .home
}

/// The page area to the right of the sidebar.
struct SettingsDetailView: View {
    @ObservedObject var settings = AppSettings.shared
    @ObservedObject var navigation: SettingsNavigation
    let controller: SessionController

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .buttonStyle(.glass)
            // Rebuild everything when the UI language changes so every L(…) string refreshes.
            .id(settings.appLanguage)
    }

    @ViewBuilder private var content: some View {
        switch navigation.page {
        case .home: HomePage(settings: settings, navigation: navigation)
        case .general: GeneralPage(settings: settings)
        case .shortcut: ShortcutPage(settings: settings)
        case .providers: ProvidersPage(settings: settings)
        case .models: ModelsPage(settings: settings, navigation: navigation, controller: controller)
        case .style: StylePage(settings: settings)
        case .history: HistoryPage(history: HistoryStore.shared, settings: settings, controller: controller)
        }
    }
}

/// Native sidebar list — on macOS 26+ this is drawn as a floating Liquid Glass panel.
struct SettingsSidebar: View {
    @ObservedObject var navigation: SettingsNavigation
    @ObservedObject var settings: AppSettings

    private var version: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String).map { "v\($0)" } ?? "dev"
    }

    private var ready: Bool {
        settings.isConfigured(settings.sttProvider) && Permissions.microphoneGranted && Permissions.accessibilityGranted
    }

    var body: some View {
        List(selection: Binding<SettingsPage?>(
            get: { navigation.page },
            set: { if let page = $0 { navigation.page = page } }
        )) {
            ForEach(SettingsPage.allCases) { page in
                Label {
                    Text(page.title)
                } icon: {
                    IconBadge(symbol: page.symbol, color: page.color, size: 22)
                }
                .padding(.vertical, 2)
                .tag(page)
            }
        }
        .listStyle(.sidebar)
        .id(settings.appLanguage)
        .safeAreaInset(edge: .top, spacing: 0) {
            HStack(spacing: 10) {
                AppTile(size: 34)
                VStack(alignment: .leading, spacing: 1) {
                    Text("OpenTypeless").font(.system(size: 14, weight: .semibold))
                    Text(version).font(.system(size: 11)).foregroundStyle(.secondary)
                }
                Spacer()
            }
            .padding(.horizontal, 16)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            HStack {
                StatusPill(text: ready ? L("一切就绪", "Ready") : L("需要完成设置", "Setup needed"),
                           color: ready ? .green : .orange)
                Spacer()
            }
            .padding(16)
        }
    }
}

/// Title + subtitle + scrolling content, shared by every page.
struct PageScaffold<Content: View>: View {
    let page: SettingsPage
    var scrolls = true
    @ViewBuilder var content: Content

    var body: some View {
        let stack = VStack(alignment: .leading, spacing: 22) {
            VStack(alignment: .leading, spacing: 4) {
                Text(page.title).font(.system(size: 24, weight: .bold))
                Text(page.subtitle).font(.system(size: 13)).foregroundStyle(.secondary)
            }
            content
        }
        .padding(.horizontal, 30)
        .padding(.top, 12)
        .padding(.bottom, 28)
        .frame(maxWidth: .infinity, alignment: .leading)

        if scrolls {
            ScrollView { stack }.scrollIndicators(.automatic)
        } else {
            stack.frame(maxHeight: .infinity, alignment: .top)
        }
    }
}

/// Opens / focuses the settings window.
@MainActor
final class SettingsWindowController {
    private var window: NSWindow?
    let navigation = SettingsNavigation()
    private let controller: SessionController

    init(controller: SessionController) {
        self.controller = controller
    }

    func show(page: SettingsPage? = nil) {
        if let page { navigation.page = page }
        if window == nil {
            window = Self.makeWindow(navigation: navigation, controller: controller)
            window?.center()
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    /// An AppKit split view with a sidebar item: on macOS 26+ AppKit draws that sidebar as a floating
    /// Liquid Glass panel, with the page content extending underneath it.
    static func makeWindow(navigation: SettingsNavigation, controller: SessionController) -> NSWindow {
        let split = NSSplitViewController()
        let sidebar = NSSplitViewItem(sidebarWithViewController: NSHostingController(
            rootView: SettingsSidebar(navigation: navigation, settings: AppSettings.shared)))
        sidebar.minimumThickness = 220
        sidebar.maximumThickness = 220
        sidebar.canCollapse = false
        let detail = NSSplitViewItem(viewController: NSHostingController(
            rootView: SettingsDetailView(navigation: navigation, controller: controller)))
        detail.minimumThickness = 600
        split.addSplitViewItem(sidebar)
        split.addSplitViewItem(detail)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 880, height: 620),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.title = "OpenTypeless"
        window.toolbar = NSToolbar(identifier: "settings")
        window.toolbarStyle = .unified
        window.contentViewController = split
        window.setContentSize(NSSize(width: 880, height: 620))
        window.contentMinSize = NSSize(width: 820, height: 520)
        window.isReleasedWhenClosed = false
        return window
    }
}
