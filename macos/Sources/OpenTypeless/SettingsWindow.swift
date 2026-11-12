import AppKit
import SwiftUI
import TypelessCore

enum SettingsPage: String, CaseIterable, Identifiable {
    case home, general, shortcut, providers, models, style, apps, history

    var id: String { rawValue }

    var title: String {
        switch self {
        case .home: return L("主页", "Home")
        case .general: return L("通用", "General")
        case .shortcut: return L("快捷键", "Shortcut")
        case .providers: return L("服务商", "Providers")
        case .models: return L("模型", "Models")
        case .style: return L("词汇与风格", "Vocabulary & Style")
        case .apps: return L("应用", "Apps")
        case .history: return L("历史记录", "History")
        }
    }

    var symbol: String {
        switch self {
        case .home: return "house"
        case .general: return "gearshape"
        case .shortcut: return "keyboard"
        case .providers: return "key"
        case .models: return "cpu"
        case .style: return "textformat"
        case .apps: return "square.grid.2x2"
        case .history: return "clock"
        }
    }

    /// The sidebar, top to bottom: what you come back to look at, then how dictation behaves, then the services behind it.
    static let groups: [[SettingsPage]] = [[.home, .history], [.general, .shortcut, .style, .apps], [.providers, .models]]
}

@MainActor
final class SettingsNavigation: ObservableObject {
    @Published var page: SettingsPage = .home
    /// False while the settings window is closed. The window is kept when closed, and SwiftUI never tells the views in
    /// it that they've gone, so the page is taken out instead: its onDisappear then stops the shortcut recorder, the
    /// microphone test and its timers, as hiding the Windows settings window does.
    @Published var isWindowOpen = true
}

/// The page area to the right of the sidebar.
struct SettingsDetailView: View {
    /// Only the UI language, not all of `AppSettings`: the pages watch the settings they show themselves, and a container
    /// that redrew on every change (each switch flipped, each key typed) rebuilt the whole page from scratch each time.
    @AppStorage(AppLanguage.defaultsKey) private var appLanguage: AppLanguage = .system
    @ObservedObject var navigation: SettingsNavigation
    let controller: SessionController

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            // Rebuild everything when the UI language changes so every L(…) string refreshes.
            .id(appLanguage)
    }

    private var settings: AppSettings { AppSettings.shared }

    @ViewBuilder private var content: some View {
        if !navigation.isWindowOpen {
            Color.clear
        } else {
            page
        }
    }

    @ViewBuilder private var page: some View {
        switch navigation.page {
        case .home: HomePage(settings: settings, navigation: navigation)
        case .general: GeneralPage(settings: settings)
        case .shortcut: ShortcutPage(settings: settings)
        case .providers: ProvidersPage(settings: settings)
        case .models: ModelsPage(settings: settings, navigation: navigation, controller: controller)
        case .style: StylePage(settings: settings)
        case .apps: AppsPage(settings: settings)
        case .history: HistoryPage(history: HistoryStore.shared, settings: settings, controller: controller, files: controller.files)
        }
    }
}

/// Native sidebar list — on macOS 26+ this is drawn as a floating Liquid Glass panel.
struct SettingsSidebar: View {
    @ObservedObject var navigation: SettingsNavigation
    @ObservedObject var settings: AppSettings
    @ObservedObject var permissions = PermissionsMonitor.shared

    private var ready: Bool {
        settings.speechReady && permissions.microphoneGranted && permissions.accessibilityGranted
    }

    var body: some View {
        List(selection: Binding<SettingsPage?>(
            get: { navigation.page },
            set: { if let page = $0 { navigation.page = page } }
        )) {
            // In groups a gap apart, without headings: eight pages don't need naming twice.
            ForEach(SettingsPage.groups, id: \.self) { group in
                Section {
                    ForEach(group) { page in
                        // Plain symbols in the sidebar's own tint, as in Mail or Finder: no colour per page.
                        Label(page.title, systemImage: page.symbol)
                            .tag(page)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .id(settings.appLanguage)
        .safeAreaInset(edge: .top, spacing: 0) {
            HStack(spacing: 10) {
                AppTile(size: 28)
                Text("OpenTypeless").font(.system(size: 14, weight: .semibold))
                Spacer()
            }
            .padding(.horizontal, 16)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if !ready {
                HStack {
                    StatusPill(text: L("需要完成设置", "Setup needed"), color: .orange)
                    Spacer()
                }
                .padding(16)
            }
        }
    }
}

/// The page's title (the sidebar already shows its icon), with an explanation behind a "?" and controls for the whole page at its end, above the content.
/// Shared by every page. A page of settings is a grouped `Form`, drawn by the system as in System Settings; Home and
/// History lay out their own content.
struct PageScaffold<Accessory: View, Content: View>: View {
    let page: SettingsPage
    var help: String? = nil
    var scrolls = true
    /// How wide the content gets; nil lets it fill the window, for pages that have use for the room.
    var maxWidth: CGFloat? = Theme.formWidth
    /// False for a page that opens on a headline of its own (Home).
    var showsTitle = true
    /// Whether a scrolling page of limited width is a grouped form, drawn by the system; false lays the content out as given.
    var form = true
    @ViewBuilder var accessory: Accessory
    @ViewBuilder var content: Content

    private var header: some View {
        HStack(spacing: 6) {
            Text(page.title)
                .font(.system(size: 20, weight: .bold))
                .lineLimit(1)
            if let help { HelpTip(text: help, topic: page.title) }
            Spacer(minLength: 12)
            accessory
        }
        .frame(minHeight: 30)
    }

    var body: some View {
        if scrolls, form, let maxWidth {
            VStack(spacing: 0) {
                header
                    .padding(.horizontal, 30)
                    .padding(.top, 10)
                    .frame(maxWidth: maxWidth + 60)
                Form { content }
                    .formStyle(.grouped)
                    // The window's own background behind the sections, as under the title.
                    .scrollContentBackground(.hidden)
                    .environment(\.inSettingsForm, true)
                    .frame(maxWidth: maxWidth + 40)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        } else {
            let stack = VStack(alignment: .leading, spacing: 20) {
                if showsTitle { header }
                content
            }
            .padding(.horizontal, 30)
            .padding(.top, showsTitle ? 10 : 14)
            .padding(.bottom, 28)
            .frame(maxWidth: maxWidth.map { $0 + 60 } ?? .infinity, alignment: .leading)
            .frame(maxWidth: .infinity)

            if scrolls {
                ScrollView { stack }.scrollIndicators(.automatic)
            } else {
                stack.frame(maxHeight: .infinity, alignment: .top)
            }
        }
    }
}

extension PageScaffold where Accessory == EmptyView {
    init(page: SettingsPage, help: String? = nil, scrolls: Bool = true, maxWidth: CGFloat? = Theme.formWidth,
         showsTitle: Bool = true, form: Bool = true, @ViewBuilder content: () -> Content) {
        self.init(page: page, help: help, scrolls: scrolls, maxWidth: maxWidth, showsTitle: showsTitle, form: form,
                  accessory: { EmptyView() }, content: content)
    }
}

/// Opens / focuses the settings window.
@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate {
    private var window: NSWindow?
    let navigation = SettingsNavigation()
    private let controller: SessionController

    init(controller: SessionController) {
        self.controller = controller
    }

    func show(page: SettingsPage? = nil) {
        if let page { navigation.page = page }
        navigation.isWindowOpen = true
        PermissionsMonitor.shared.setWatching(true, by: .settingsWindow)
        if window == nil {
            let window = Self.makeWindow(navigation: navigation, controller: controller)
            window.delegate = self
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    /// The window stays around for next time, but its page goes (see `SettingsNavigation.isWindowOpen`).
    func windowWillClose(_ notification: Notification) {
        navigation.isWindowOpen = false
        PermissionsMonitor.shared.setWatching(false, by: .settingsWindow)
    }

    /// An AppKit split view with a sidebar item: on macOS 26+ AppKit draws that sidebar as a floating
    /// Liquid Glass panel, with the page content extending underneath it.
    static func makeWindow(navigation: SettingsNavigation, controller: SessionController) -> NSWindow {
        let split = NSSplitViewController()
        let sidebarHost = NSHostingController(rootView: SettingsSidebar(navigation: navigation, settings: AppSettings.shared))
        let detailHost = NSHostingController(rootView: SettingsDetailView(navigation: navigation, controller: controller))
        // The split view and the window set the sizes (fixed sidebar, minimum page width, minimum window size). Left to
        // size themselves, the hosting views measured their whole page again for its smallest and largest size after
        // every change, a settings form twice more per click.
        sidebarHost.sizingOptions = []
        detailHost.sizingOptions = []
        let sidebar = NSSplitViewItem(sidebarWithViewController: sidebarHost)
        sidebar.minimumThickness = 220
        sidebar.maximumThickness = 220
        sidebar.canCollapse = false
        let detail = NSSplitViewItem(viewController: detailHost)
        detail.minimumThickness = 640
        split.addSplitViewItem(sidebar)
        split.addSplitViewItem(detail)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 960, height: 660),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.title = "OpenTypeless"
        window.toolbar = NSToolbar(identifier: "settings")
        window.toolbarStyle = .unifiedCompact
        window.contentViewController = split
        window.setContentSize(NSSize(width: 960, height: 660))
        window.contentMinSize = NSSize(width: 860, height: 540)
        window.isReleasedWhenClosed = false
        return window
    }
}
