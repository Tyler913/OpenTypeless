import AppKit
import SwiftUI
import TypelessCore

/// The panel shown when clicking the menu-bar icon.
struct MenuPopoverView: View {
    @ObservedObject var controller: SessionController
    @ObservedObject var settings = AppSettings.shared
    @ObservedObject var history = HistoryStore.shared
    @ObservedObject var updater = Updater.shared
    @ObservedObject var permissions = PermissionsMonitor.shared
    @ObservedObject var usage = UsageStore.shared
    let openSettings: (SettingsPage?) -> Void
    let close: () -> Void
    @State private var copiedID: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header
            statusModule
            spendModule
            warnings
            if hasRecent {
                Divider()
                recent
            }
            Divider()
            footer
        }
        .padding(12)
        .frame(width: 340)
        .id(settings.appLanguage)
    }

    // MARK: Sections

    private var header: some View {
        HStack(spacing: 10) {
            AppTile(size: 30)
            Text("OpenTypeless").font(.system(size: 14, weight: .bold))
            Spacer()
            // Idle shows nothing when everything is set up: the pill only speaks up when something needs attention.
            switch controller.state {
            case .idle: if !setupIssues.isEmpty { StatusPill(text: L("需要完成设置", "Setup needed"), color: .orange) }
            case .recording: StatusPill(text: L("录音中", "Recording"), color: .red)
            case .processing: StatusPill(text: L("处理中", "Processing"), color: .blue)
            }
        }
    }

    /// What the app is doing, as a Control Center module: a symbol in a circle coloured by the state, and how to dictate.
    private var statusModule: some View {
        let (symbol, color): (String, Color) = switch controller.state {
        case .idle: ("mic.fill", .accentColor)
        case .recording: ("waveform", .red)
        case .processing: ("ellipsis", .blue)
        }
        return HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 34, height: 34)
                .background(Circle().fill(color.gradient))
            VStack(alignment: .leading, spacing: 4) {
                switch controller.state {
                case .idle:
                    HStack(spacing: 5) {
                        Text(L("按住", "Hold"))
                        KeyCaps(caps: settings.hotkey.keyCaps)
                        Text(L("说话", "and speak"))
                    }
                    .font(.system(size: 13, weight: .medium))
                case .recording:
                    Text(L("录音中", "Recording")).font(.system(size: 13, weight: .semibold))
                case .processing:
                    Text(L("处理中", "Processing")).font(.system(size: 13, weight: .semibold))
                }
                Text(L("轻点一下进入免手持 · Esc 取消", "Tap once for hands-free · Esc to cancel"))
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.primary.opacity(0.06)))
    }

    /// This month's spend against the monthly limit, while one is set (see `SpendLimit`): the amount, and the same bar
    /// as on Home.
    @ViewBuilder private var spendModule: some View {
        if let month = usage.monthlySpend {
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline) {
                    Text(L("本月花费", "Spent this month")).font(.system(size: 12, weight: .medium))
                    Spacer(minLength: 8)
                    Text(UsageFormat.money(month.spent))
                        .font(.system(size: 13, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                }
                LimitBar(spent: month.spent, limit: month.limit)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.primary.opacity(0.06)))
        }
    }

    private var hasRecent: Bool { history.records.contains { !$0.outputText.isEmpty } }

    @ViewBuilder private var warnings: some View {
        let items = setupIssues
        if !items.isEmpty || failedRecord != nil || updater.update != nil {
            VStack(spacing: 8) {
                ForEach(items, id: \.text) { item in
                    Banner(symbol: "exclamationmark.triangle.fill", color: .orange, text: item.text) {
                        Button(L("设置", "Fix")) { close(); openSettings(item.page) }.controlSize(.small)
                    }
                }
                if let failed = failedRecord {
                    Banner(symbol: "arrow.clockwise.circle.fill", color: .red,
                           text: L("上一次转写失败（\(failed.duration.durationLabel) 录音已保存）",
                                   "Last dictation failed (\(failed.duration.durationLabel) recording saved)")) {
                        Button(L("重试", "Retry")) { close(); controller.retry(failed, paste: true) }.controlSize(.small)
                    }
                }
                updateBanner
            }
        }
    }

    /// A downloaded update, or one that has to be downloaded by hand.
    @ViewBuilder private var updateBanner: some View {
        if let update = updater.update {
            let version = update.version.description
            if updater.isReady {
                Banner(symbol: "arrow.down.circle.fill", color: .blue,
                       text: updater.installPending ? L("听写结束后自动更新到 \(version)", "Updates to \(version) after this dictation")
                                                    : L("新版本 \(version) 已就绪", "Version \(version) is ready")) {
                    Button(L("重启更新", "Restart to update")) { updater.installAndRelaunch() }
                        .controlSize(.small)
                        .disabled(updater.installPending)
                }
            } else if updater.phase == .available {
                Banner(symbol: "arrow.down.circle.fill", color: .blue, text: L("有新版本 \(version)", "Version \(version) is available")) {
                    Button(L("下载", "Download")) { close(); NSWorkspace.shared.open(update.pageURL) }.controlSize(.small)
                }
            }
        }
    }

    @ViewBuilder private var recent: some View {
        let records = Array(history.records.filter { !$0.outputText.isEmpty }.prefix(5))
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(L("最近", "Recent")).font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
                Spacer()
                if !records.isEmpty {
                    Text(L("点击复制", "Click to copy")).font(.system(size: 10.5)).foregroundStyle(.tertiary)
                }
            }
            .padding(.horizontal, 4)
            VStack(spacing: 1) {
                ForEach(records) { record in
                    Button { copy(record) } label: { RecentRow(record: record, copied: copiedID == record.id) }
                        .buttonStyle(HoverButtonStyle())
                }
            }
        }
    }

    /// The panel's commands, as rows like a menu-bar extra's (Wi-Fi, Bluetooth): a symbol in a circle and a title.
    private var footer: some View {
        VStack(spacing: 0) {
            MenuRow(symbol: "gearshape.fill", title: L("设置", "Settings")) { close(); openSettings(nil) }
            MenuRow(symbol: "clock.fill", title: L("历史", "History")) { close(); openSettings(.history) }
            pasteLastButton
            Divider().padding(.vertical, 4)
            MenuRow(symbol: "power", title: L("退出 OpenTypeless", "Quit OpenTypeless")) { NSApp.terminate(nil) }
        }
    }

    /// Puts the newest dictation at the cursor of the app that had focus before the panel opened (see
    /// `SessionController.pasteLastDictation`); its tooltip shows the shortcut, when there is one.
    private var pasteLastButton: some View {
        let action = ShortcutAction.pasteLastDictation
        return MenuRow(symbol: action.symbol, title: action.title, shortcut: settings.shortcut(for: action)?.displayName) {
            close()
            controller.pasteLastDictation(releasing: nil)
        }
        .disabled(controller.state != .idle || history.records.isEmpty)
    }

    // MARK: Helpers

    private struct Issue { let text: String; let page: SettingsPage }

    private var setupIssues: [Issue] {
        var issues: [Issue] = []
        if !settings.speechReady {
            issues.append(Issue(text: L("还没有配置 \(settings.sttProvider.displayName) 的 API Key",
                                        "No API key for \(settings.sttProvider.displayName)"), page: .providers))
        }
        if !permissions.accessibilityGranted {
            issues.append(Issue(text: L("需要辅助功能权限", "Accessibility permission needed"), page: .general))
        }
        if !permissions.microphoneGranted {
            issues.append(Issue(text: L("需要麦克风权限", "Microphone permission needed"), page: .general))
        }
        return issues
    }

    /// The last dictation, when it failed. A transcribed file is retried from History instead: its text is never pasted.
    private var failedRecord: DictationRecord? {
        // An edit of a selection can't be run again without its selection.
        guard controller.state == .idle, let last = history.records.first, !last.isFile,
              last.status == .failed, last.hasAudio, !last.isCommand else { return nil }
        return last
    }

    private func copy(_ record: DictationRecord) {
        TextInserter.copyToClipboard(record.outputText)
        copiedID = record.id
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
            if copiedID == record.id { copiedID = nil }
        }
    }
}

private struct RecentRow: View {
    let record: DictationRecord
    let copied: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(record.outputText)
                    .font(.system(size: 12.5))
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .foregroundStyle(.primary)
                Text("\(record.date.shortStamp) · \(record.duration.durationLabel)")
                    .font(.system(size: 10.5))
                    .foregroundStyle(.tertiary)
            }
            Spacer(minLength: 4)
            Image(systemName: copied ? "checkmark.circle.fill" : "doc.on.doc")
                .font(.system(size: 11))
                .foregroundStyle(copied ? Color.green : Color.secondary.opacity(0.6))
                .padding(.top, 2)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A command in the panel: a symbol in a grey circle, its title, and its shortcut at the end, highlighted under the pointer.
private struct MenuRow: View {
    let symbol: String
    let title: String
    var shortcut: String? = nil
    let action: () -> Void
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: symbol)
                    .font(.system(size: 11.5, weight: .semibold))
                    .frame(width: 24, height: 24)
                    .background(Circle().fill(Color.primary.opacity(0.09)))
                Text(title).font(.system(size: 13))
                Spacer(minLength: 8)
                if let shortcut {
                    Text(shortcut).font(.system(size: 12)).foregroundStyle(.tertiary)
                }
            }
            .opacity(isEnabled ? 1 : 0.45)
            .padding(.horizontal, 6)
            .padding(.vertical, 4)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .buttonStyle(HoverButtonStyle(cornerRadius: 7))
    }
}
