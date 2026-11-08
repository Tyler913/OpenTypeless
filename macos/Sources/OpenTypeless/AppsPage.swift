import AppKit
import SwiftUI
import TypelessCore
import UniformTypeIdentifiers

/// Settings for particular apps (see AppProfile): a card for each app, opened to change what it overrides. There are none
/// until the user adds one. Below them, the recommended settings for common apps (see `RecommendedApps`) and the apps
/// they cover.
struct AppsPage: View {
    @ObservedObject var settings: AppSettings
    @State private var expanded: Set<String>

    init(settings: AppSettings) {
        self.settings = settings
        // The snapshots show the first sample profile opened.
        _expanded = State(initialValue: UISnapshots.active ? Set(settings.appProfiles.prefix(1).map(\.id)) : [])
    }

    var body: some View {
        PageScaffold(page: .apps,
                     help: L("给一个应用添加配置后，在它里面听写时就用这里的设置，比如在 Slack 里说完直接发送，在邮件里用更正式的语气。其他应用照常使用你平时的设置。",
                             "Add an app to give it its own settings: send messages right away in Slack, say, or a more formal tone in your email app. Everywhere else your usual settings apply.")
                         + "\n\n" + L("开始录音时在最前面的应用决定用哪个配置，松开按键前换到别的应用也不变。配置里设为「默认」的项目跟随你平时的设置。",
                                       "The app in front when you start recording decides which profile is used, even if you switch apps before you finish. Whatever a profile leaves on Default follows your usual settings.")) {
            if !settings.appProfiles.isEmpty { addButton }
        } content: {
            if settings.appProfiles.isEmpty {
                Card {
                    VStack(spacing: 8) {
                        Image(systemName: SettingsPage.apps.symbol).font(.system(size: 28)).foregroundStyle(.tertiary)
                        Text(L("还没有应用配置", "No app profiles yet")).font(.system(size: 14, weight: .semibold))
                        Text(L("让某个应用用自己的设置，比如在 Slack 里说完直接发送", "Give an app its own settings, like sending right away in Slack"))
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                            .fixedSize(horizontal: false, vertical: true)
                        addButton.padding(.top, 6)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 24)
                    .padding(.horizontal, 16)
                }
            } else {
                ForEach(settings.appProfiles) { profile in
                    AppProfileCard(settings: settings, profile: profile, expanded: Binding(
                        get: { expanded.contains(profile.id) },
                        set: { if $0 { expanded.insert(profile.id) } else { expanded.remove(profile.id) } }
                    ))
                }
            }
            RecommendedAppsSection(settings: settings)
        }
    }

    /// Opens the Add menu. Nothing about running apps is read until then: not when the page is shown, nor while it's open.
    private var addButton: some View {
        Button { showAddMenu() } label: {
            HStack(spacing: 6) {
                Label(L("添加应用", "Add app"), systemImage: "plus")
                Image(systemName: "chevron.down").font(.system(size: 9, weight: .semibold)).foregroundStyle(.secondary)
            }
        }
        .fixedSize()
    }

    /// The running apps that have no profile yet, read now (names only, which is quick), and the Applications folder for
    /// any other. Their icons are looked up off the main thread and fill in as they come (see `AppIcons`).
    private func showAddMenu() {
        let candidates = RunningApp.list().filter { app in !settings.appProfiles.contains { $0.matches(app.bundleID) } }
        let menu = NSMenu()
        menu.addItem(.sectionHeader(title: L("正在运行的应用", "Running apps")))
        if candidates.isEmpty {
            menu.addItem(NSMenuItem(title: L("没有其他正在运行的应用", "No other running apps"), action: nil, keyEquivalent: ""))
        }
        for app in candidates {
            let item = MenuAction.item(app.name) { add(bundleID: app.bundleID, name: app.name) }
            item.image = AppIcons.placeholder
            menu.addItem(item)
            Task { if let icon = await AppIcons.icon(for: app.bundleID) { item.image = AppIcons.menuSized(icon) } }
        }
        menu.addItem(.separator())
        menu.addItem(MenuAction.item(L("从「应用程序」文件夹选择…", "Choose from Applications…")) { chooseApplication() })
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
    }

    private func add(bundleID: String, name: String) {
        let profile = settings.addAppProfile(bundleID: bundleID, name: name)
        expanded.insert(profile.id)
    }

    /// An app that isn't running: picked from /Applications (or anywhere else) by its bundle.
    private func chooseApplication() {
        let panel = NSOpenPanel()
        panel.directoryURL = URL(fileURLWithPath: "/Applications", isDirectory: true)
        panel.allowedContentTypes = [.application]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.prompt = L("添加", "Add")
        panel.begin { response in
            guard response == .OK, let url = panel.url, let bundleID = Bundle(url: url)?.bundleIdentifier else { return }
            let name = FileManager.default.displayName(atPath: url.path)
            add(bundleID: bundleID, name: name.hasSuffix(".app") ? String(name.dropLast(4)) : name)
        }
    }
}

/// An app in the Add menu: one with a Dock icon (not a background helper), other than OpenTypeless itself.
private struct RunningApp {
    let bundleID: String
    let name: String

    /// Read when the Add menu opens; a fixed pair for the UI snapshots, which never ask the system.
    @MainActor static func list() -> [RunningApp] {
        if UISnapshots.active {
            return [RunningApp(bundleID: "com.apple.Notes", name: "Notes"), RunningApp(bundleID: "com.apple.Safari", name: "Safari")]
        }
        var seen = Set<String>()
        return NSWorkspace.shared.runningApplications.compactMap { app -> RunningApp? in
            guard app.activationPolicy == .regular, let id = app.bundleIdentifier, id != Bundle.main.bundleIdentifier,
                  seen.insert(id).inserted else { return nil }
            return RunningApp(bundleID: id, name: app.localizedName ?? id)
        }
        .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }
}

/// App icons by bundle ID, looked up off the main thread and once each: LaunchServices and the icon service can be slow
/// to answer. None are looked up for the UI snapshots.
@MainActor
private enum AppIcons {
    private static var cache: [String: NSImage] = [:]

    /// Shown until an app's own icon is found, or when it has none.
    static let placeholder = NSImage(systemSymbolName: "app", accessibilityDescription: nil) ?? NSImage(size: NSSize(width: 16, height: 16))

    static func icon(for bundleID: String) async -> NSImage? {
        if let cached = cache[bundleID] { return cached }
        guard !UISnapshots.active else { return nil }
        let found = await Task.detached(priority: .utility) {
            IconBox(image: NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID).map { NSWorkspace.shared.icon(forFile: $0.path) })
        }.value
        if let image = found.image { cache[bundleID] = image }
        return found.image
    }

    /// A copy at menu size: a menu item shows its image at the image's own size.
    static func menuSized(_ icon: NSImage) -> NSImage {
        let small = (icon.copy() as? NSImage) ?? icon
        small.size = NSSize(width: 16, height: 16)
        return small
    }
}

/// Hands an icon back from the lookup: NSImage isn't Sendable, but this one is passed on, never shared.
private struct IconBox: @unchecked Sendable {
    let image: NSImage?
}

/// A menu item that runs a closure.
private final class MenuAction: NSObject {
    private let action: () -> Void

    private init(_ action: @escaping () -> Void) {
        self.action = action
    }

    @objc private func run() { action() }

    static func item(_ title: String, action: @escaping () -> Void) -> NSMenuItem {
        let handler = MenuAction(action)
        let item = NSMenuItem(title: title, action: #selector(run), keyEquivalent: "")
        item.target = handler
        // The item keeps it: `target` is weak.
        item.representedObject = handler
        return item
    }
}

/// One app's profile: its icon, name and what it changes; opened, a picker for each setting, its own clean-up
/// preferences and Remove.
private struct AppProfileCard: View {
    @ObservedObject var settings: AppSettings
    let profile: AppProfile
    @Binding var expanded: Bool
    @State private var icon: NSImage?

    var body: some View {
        Card {
            Button { withAnimation(.easeOut(duration: 0.18)) { expanded.toggle() } } label: {
                HStack(spacing: 12) {
                    if let icon {
                        Image(nsImage: icon).resizable().frame(width: 28, height: 28)
                    } else {
                        Image(systemName: "app.fill").font(.system(size: 24)).foregroundStyle(.tertiary).frame(width: 28, height: 28)
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        Text(profile.name).font(.system(size: 13, weight: .medium))
                        Text(summary).font(.system(size: 11.5)).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Image(systemName: "chevron.right")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.tertiary)
                        .rotationEffect(.degrees(expanded ? 90 : 0))
                }
                .rowPadding(.horizontal, 14)
                .rowPadding(.vertical, 9)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if expanded {
                CardDivider(inset: 0)
                CardRow(title: L("用 AI 整理文字", "Clean up with AI")) {
                    Picker(L("用 AI 整理文字", "Clean up with AI"), selection: binding(\.strength)) {
                        Text(L("默认（\(defaultStrength.displayName)）", "Default (\(defaultStrength.displayName))"))
                            .tag(PolishStrength?.none)
                        Divider()
                        ForEach(PolishStrength.allCases) { strength in
                            Text(strength.displayName).tag(PolishStrength?.some(strength))
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                CardDivider()
                CardRow(title: L("插入后自动回车", "Press Return after inserting")) {
                    OverridePicker(title: L("插入后自动回车", "Press Return after inserting"), value: binding(\.pressEnter),
                                   global: settings.pressEnterAfterInsert)
                }
                CardDivider()
                CardRow(title: L("说话语言", "Spoken language")) {
                    Picker(L("说话语言", "Spoken language"), selection: binding(\.language)) {
                        Text(L("默认（\(AppProfileCard.languageName(settings.sttLanguage))）",
                               "Default (\(AppProfileCard.languageName(settings.sttLanguage)))")).tag(String?.none)
                        Divider()
                        Text(L("自动检测", "Auto-detect")).tag(String?.some(""))
                        ForEach(AppLanguage.supported) { language in
                            Text(AppProfileCard.languageName(language.rawValue)).tag(String?.some(language.rawValue))
                        }
                        // A language this version doesn't list, kept as it was saved.
                        if let code = profile.language, !code.isEmpty, !AppLanguage.supported.contains(where: { $0.rawValue == code }) {
                            Text(code).tag(String?.some(code))
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                CardDivider()
                CardRow(title: L("排版", "Layout"), help: Self.layoutHelp) {
                    Picker(L("排版", "Layout"), selection: binding(\.layout)) {
                        Text(L("默认（\(Self.layoutName(builtInLayout))）", "Default (\(Self.layoutName(builtInLayout)))"))
                            .tag(TextLayout?.none)
                        Divider()
                        ForEach(TextLayout.allCases) { layout in
                            Text(Self.layoutName(layout)).tag(TextLayout?.some(layout))
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                // Only used while the text is cleaned up.
                .disabled(!cleansUp)
                .opacity(cleansUp ? 1 : 0.5)
                CardDivider()
                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 5) {
                        Text(L("这个应用的整理偏好", "Clean-up preferences for this app")).font(.system(size: 13))
                        HelpTip(text: L("加在「词汇与风格」里的整理偏好之后，只在这个应用里生效。例如：「语气正式一些」「不要使用列表」。",
                                        "Added after your clean-up preferences under Vocabulary & Style, in this app only. e.g. “Use a more formal tone”, “Never use bullet lists”."),
                                topic: L("这个应用的整理偏好", "Clean-up preferences for this app"))
                    }
                    TextEditor(text: binding(\.instructions))
                        .font(.system(size: 13))
                        .scrollContentBackground(.hidden)
                        .padding(6)
                        .frame(height: 76)
                        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.04)))
                        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Color.primary.opacity(0.08)))
                }
                .rowPadding(.all, 14)
                // Only used while the text is cleaned up.
                .disabled(!cleansUp)
                .opacity(cleansUp ? 1 : 0.5)
                CardDivider(inset: 0)
                HStack {
                    Text(profile.app).font(.system(size: 11, design: .monospaced)).foregroundStyle(.tertiary).textSelection(.enabled)
                    Spacer()
                    Button(role: .destructive) {
                        settings.removeAppProfile(id: profile.id)
                    } label: {
                        Label(L("移除", "Remove"), systemImage: "trash")
                    }
                    .controlSize(.small)
                }
                .rowPadding(.horizontal, 14)
                .rowPadding(.vertical, 10)
            }
        }
        .task(id: profile.app) { icon = await AppIcons.icon(for: profile.app) }
    }

    /// One setting of this profile, saved as it's changed.
    private func binding<Value>(_ keyPath: WritableKeyPath<AppProfile, Value>) -> Binding<Value> {
        Binding(
            get: { (settings.appProfiles.first { $0.id == profile.id } ?? profile)[keyPath: keyPath] },
            set: { value in
                var updated = settings.appProfiles.first { $0.id == profile.id } ?? profile
                updated[keyPath: keyPath] = value
                settings.updateAppProfile(updated)
            }
        )
    }

    /// "Clean-up off · Return on · English", or that everything follows the usual settings.
    private var summary: String {
        var parts: [String] = []
        if let strength = profile.strength {
            parts.append(L("整理：\(strength.displayName)", "Clean-up: \(strength.displayName)"))
        }
        if let enter = profile.pressEnter { parts.append(enter ? L("回车：开", "Return on") : L("回车：关", "Return off")) }
        if let language = profile.language { parts.append(Self.languageName(language)) }
        if let layout = profile.layout { parts.append(Self.layoutName(layout)) }
        if !profile.instructions.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            parts.append(L("自定义整理偏好", "Own clean-up preferences"))
        }
        return parts.isEmpty ? L("全部跟随你平时的设置", "Uses your usual settings") : parts.joined(separator: " · ")
    }

    /// The strength this app gets when its profile leaves it on Default: the one recommended for it while the recommended
    /// settings are on, else the global one (off while clean-up is off).
    private var defaultStrength: PolishStrength {
        settings.globalDictationSettings.applying(nil, recommendation: settings.recommendation(for: profile.app)).effectiveStrength
    }

    /// Whether this app's dictations are cleaned up.
    private var cleansUp: Bool { (profile.strength ?? defaultStrength) != .off }

    /// The layout this app gets when its profile leaves it on Default: its built-in one, or the prompt's own.
    private var builtInLayout: TextLayout { TextLayout.builtIn(for: profile.app) ?? .natural }

    /// A layout's name in the picker and the card's summary.
    static func layoutName(_ layout: TextLayout) -> String {
        switch layout {
        case .natural: return L("自然", "Natural")
        case .chat: return L("聊天", "Chat")
        case .structured: return L("轻结构", "Light structure")
        case .email: return L("邮件", "Email")
        }
    }

    static var layoutHelp: String {
        L("整理后的文字怎么分段、要不要列表。聊天：一整段，不列表，句尾不加句号。自然：短的不分段，明确列举三项以上才列表。轻结构（写给 AI 的提示词、文档）：更积极地分段，两项以上的并列就列表。邮件：说了称呼和落款就各占一行。只改排版，不改你说的话。常见的聊天、AI、邮件应用默认就用对应的排版。",
          "How the cleaned-up text is laid out. Chat: one block, no lists, no full stop at the end. Natural: short text stays one paragraph, a list only for three or more items you enumerate. Light structure (prompts for an AI, documents): more paragraphs, and a list for two or more parallel items. Email: a greeting and sign-off you dictate go on lines of their own. Only the layout changes, never your words. Common chat, AI and mail apps use their layout by default.")
    }

    /// A spoken-language code as the Models page names it; "" is auto-detect.
    static func languageName(_ code: String) -> String {
        if code.isEmpty { return L("自动检测", "Auto-detect") }
        guard let language = AppLanguage(rawValue: code), language != .system else { return code }
        return language == .zh ? "中文" : language.nativeName
    }
}

/// The recommended settings for common apps: a switch, and the apps they cover by kind with the strength each kind gets,
/// shown whether it's on or off, so it's plain which apps a profile of their own would override.
private struct RecommendedAppsSection: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        CardSection(title: L("推荐设置", "Recommended settings")) {
            CardRow(title: L("常用应用的推荐设置", "Recommended settings for common apps"),
                    subtitle: L("聊天和写代码用轻度，邮件用润色", "Light for chat and code, Polished for email"),
                    help: L("下面这些常用应用自动用推荐的整理强度。只改整理强度，不会自动回车，也不改说话语言；文字整理关闭时不起作用。给某个应用添加配置并设了整理强度，就按你的配置来。",
                            "These common apps get the recommended clean-up strength. Only the strength changes, never Return or the spoken language, and nothing while clean-up is off. An app's own profile with a strength set wins."),
                    isOn: $settings.recommendedAppSettings)
            ForEach(AppCategory.allCases) { kind in
                CardDivider()
                CardRow(title: kind.displayName, subtitle: Self.names(kind)) {
                    Text(kind.strength.displayName).foregroundStyle(.secondary)
                }
                .opacity(settings.recommendedAppSettings ? 1 : 0.5)
            }
        }
    }

    /// The Mac apps of this kind the table covers.
    static func names(_ kind: AppCategory) -> String {
        RecommendedApps.all.filter { $0.category == kind && !$0.mac.isEmpty }.map(\.displayName).joined(separator: ", ")
    }
}

/// On, off, or the global setting (named with its current value).
private struct OverridePicker: View {
    let title: String
    @Binding var value: Bool?
    let global: Bool

    var body: some View {
        Picker(title, selection: $value) {
            Text(global ? L("默认（开）", "Default (on)") : L("默认（关）", "Default (off)")).tag(Bool?.none)
            Divider()
            Text(L("开", "On")).tag(Bool?.some(true))
            Text(L("关", "Off")).tag(Bool?.some(false))
        }
        .labelsHidden()
        .fixedSize()
    }
}
