import AppKit
import SwiftUI
import TypelessCore

/// The panel shown when clicking the menu-bar icon.
struct MenuPopoverView: View {
    @ObservedObject var controller: SessionController
    @ObservedObject var settings = AppSettings.shared
    @ObservedObject var history = HistoryStore.shared
    @ObservedObject var updater = Updater.shared
    let openSettings: (SettingsPage?) -> Void
    let close: () -> Void
    @State private var copiedID: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            warnings
            recent
            footer
        }
        .padding(14)
        .frame(width: 340)
        .buttonStyle(.glass)
        .id(settings.appLanguage)
    }

    // MARK: Sections

    private var header: some View {
        HStack(spacing: 10) {
            AppTile(size: 28)
            Text("OpenTypeless").font(.system(size: 14, weight: .semibold))
            Spacer()
            switch controller.state {
            case .idle: StatusPill(text: L("就绪", "Ready"), color: .green)
            case .recording: StatusPill(text: L("录音中", "Recording"), color: .red)
            case .processing: StatusPill(text: L("处理中", "Processing"), color: .blue)
            }
        }
    }

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
        let records = Array(history.records.filter { !$0.finalText.isEmpty }.prefix(5))
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(L("最近", "Recent")).font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
                Spacer()
                if !records.isEmpty {
                    Text(L("点击复制", "Click to copy")).font(.system(size: 10.5)).foregroundStyle(.tertiary)
                }
            }
            .padding(.horizontal, 4)
            if records.isEmpty {
                // Nothing to show yet: how to make the first one.
                VStack(spacing: 8) {
                    HStack(spacing: 6) {
                        Text(L("按住", "Hold")).foregroundStyle(.secondary)
                        KeyCaps(caps: settings.hotkey.keyCaps)
                        Text(L("说话", "and speak")).foregroundStyle(.secondary)
                    }
                    .font(.system(size: 12.5))
                    Text(L("轻点一下进入免手持 · Esc 取消", "Tap once for hands-free · Esc to cancel"))
                        .font(.system(size: 11))
                        .foregroundStyle(.tertiary)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 14)
            } else {
                VStack(spacing: 1) {
                    ForEach(records) { record in
                        Button { copy(record) } label: { RecentRow(record: record, copied: copiedID == record.id) }
                            .buttonStyle(HoverButtonStyle())
                    }
                }
            }
        }
    }

    private var footer: some View {
        GlassEffectContainer(spacing: 8) {
            HStack(spacing: 8) {
                Button { close(); openSettings(nil) } label: { Label(L("设置", "Settings"), systemImage: "gearshape") }
                Button { close(); openSettings(.history) } label: { Label(L("历史", "History"), systemImage: "clock") }
                Spacer()
                Button { NSApp.terminate(nil) } label: { Image(systemName: "power") }
                    .help(L("退出 OpenTypeless", "Quit OpenTypeless"))
            }
        }
        .controlSize(.regular)
        .padding(.top, 2)
    }

    // MARK: Helpers

    private struct Issue { let text: String; let page: SettingsPage }

    private var setupIssues: [Issue] {
        var issues: [Issue] = []
        if !settings.isConfigured(settings.sttProvider) {
            issues.append(Issue(text: L("还没有配置 \(settings.sttProvider.displayName) 的 API Key",
                                        "No API key for \(settings.sttProvider.displayName)"), page: .providers))
        }
        if !Permissions.accessibilityGranted {
            issues.append(Issue(text: L("需要辅助功能权限", "Accessibility permission needed"), page: .general))
        }
        if !Permissions.microphoneGranted {
            issues.append(Issue(text: L("需要麦克风权限", "Microphone permission needed"), page: .general))
        }
        return issues
    }

    private var failedRecord: DictationRecord? {
        guard controller.state == .idle, let last = history.records.first,
              last.status == .failed, last.hasAudio else { return nil }
        return last
    }

    private func copy(_ record: DictationRecord) {
        TextInserter.copyToClipboard(record.finalText)
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
                Text(record.finalText)
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
