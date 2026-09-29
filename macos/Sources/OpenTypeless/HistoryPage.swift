import AppKit
import SwiftUI
import TypelessCore

/// Every dictation, newest first and grouped by day, with search; the selected one's text, details and actions
/// on the right. How long recordings are kept sits in the toolbar.
struct HistoryPage: View {
    @ObservedObject var history: HistoryStore
    @ObservedObject var settings: AppSettings
    let controller: SessionController
    @State private var selection: DictationRecord.ID?
    @State private var storageBytes: Int64?
    @State private var query = ""

    var body: some View {
        PageScaffold(page: .history, scrolls: false) {
            toolbar
            if history.records.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "waveform.badge.mic").font(.system(size: 36)).foregroundStyle(.tertiary)
                    Text(L("还没有听写记录", "No dictations yet")).font(.system(size: 14, weight: .medium))
                    Text(L("按住 \(settings.hotkey.displayName) 说话，每次听写都会保存在这里。",
                           "Hold \(settings.hotkey.displayName) and talk. Every dictation is kept here."))
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                // Filtered once per update: every row needs the selection, and searching compares every record's text.
                let records = filtered
                let selected = selectedID(in: records)
                HStack(alignment: .top, spacing: 14) {
                    list(records, selected: selected).frame(width: 260)
                    if let record = records.first(where: { $0.id == selected }) {
                        HistoryDetail(record: record, controller: controller, history: history)
                            .id(record.id)
                    } else {
                        Card { Color.clear.frame(maxWidth: .infinity, maxHeight: .infinity) }
                    }
                }
                .frame(maxHeight: .infinity)
            }
        }
        .onChange(of: settings.historyRetention) { history.applyRetention() }
        .task(id: history.changeCount) {
            storageBytes = await Task.detached { HistoryStore.storageBytes() }.value
        }
    }

    // MARK: Toolbar

    private var toolbar: some View {
        HStack(spacing: 12) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField(L("搜索听写内容", "Search dictations"), text: $query)
                    .textFieldStyle(.plain)
                if !query.isEmpty {
                    Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain)
                        .foregroundStyle(.tertiary)
                }
            }
            .font(.system(size: 13))
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(Capsule().fill(Color.primary.opacity(0.06)))
            .frame(maxWidth: 260)

            Spacer(minLength: 8)

            if let storageBytes {
                Label(ByteCountFormatter.string(fromByteCount: storageBytes, countStyle: .file), systemImage: "externaldrive")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .help(L("录音和转写占用的空间。每分钟录音约 1.9 MB；转写失败的录音会一直保留，方便重试。",
                            "Space used by recordings and transcripts. Recordings take about 1.9 MB per minute; failed dictations keep theirs so you can retry."))
            }
            Picker(L("保留录音", "Keep recordings"), selection: $settings.historyRetention) {
                ForEach(HistoryRetention.allCases) { Text($0.label).tag($0) }
            }
            .fixedSize()
        }
    }

    // MARK: List

    private var filtered: [DictationRecord] {
        let needle = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !needle.isEmpty else { return history.records }
        return history.records.filter {
            $0.finalText.localizedCaseInsensitiveContains(needle) || $0.rawText.localizedCaseInsensitiveContains(needle)
        }
    }

    private func selectedID(in records: [DictationRecord]) -> DictationRecord.ID? {
        if let selection, records.contains(where: { $0.id == selection }) { return selection }
        return records.first?.id
    }

    private struct DaySection: Identifiable {
        let title: String
        var records: [DictationRecord]
        var id: String { title }
    }

    /// Records grouped by the day they were made, newest day first ("Today", "Yesterday", "Fri, Sep 26").
    private static func sections(of records: [DictationRecord]) -> [DaySection] {
        let calendar = Calendar.current
        var result: [DaySection] = []
        var currentDay: Date?
        for record in records {
            let day = calendar.startOfDay(for: record.date)
            if day != currentDay {
                currentDay = day
                result.append(DaySection(title: Self.dayTitle(day, calendar: calendar), records: []))
            }
            result[result.count - 1].records.append(record)
        }
        return result
    }

    static func dayTitle(_ day: Date, calendar: Calendar) -> String {
        if calendar.isDateInToday(day) { return L("今天", "Today") }
        if calendar.isDateInYesterday(day) { return L("昨天", "Yesterday") }
        let sameYear = calendar.isDate(day, equalTo: Date(), toGranularity: .year)
        let locale = AppLanguage.resolved.locale
        return sameYear
            ? day.formatted(.dateTime.weekday(.abbreviated).month(.abbreviated).day().locale(locale))
            : day.formatted(.dateTime.year().month(.abbreviated).day().locale(locale))
    }

    private func list(_ records: [DictationRecord], selected: DictationRecord.ID?) -> some View {
        Card {
            let sections = Self.sections(of: records)
            if sections.isEmpty {
                Text(L("没有匹配的听写", "No matching dictations"))
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2, pinnedViews: [.sectionHeaders]) {
                        ForEach(sections) { section in
                            Section {
                                ForEach(section.records) { record in
                                    HistoryRow(record: record, selected: record.id == selected) { selection = record.id }
                                }
                            } header: {
                                Text(section.title)
                                    .font(.system(size: 11, weight: .semibold))
                                    .foregroundStyle(.secondary)
                                    .padding(.horizontal, 10)
                                    .padding(.top, 8)
                                    .padding(.bottom, 4)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .background(Color(nsColor: .controlBackgroundColor))
                            }
                        }
                    }
                    .padding(6)
                }
            }
        }
        .frame(maxHeight: .infinity)
    }
}

/// A list entry: time, duration and a status badge when it isn't simply done, then the start of the text.
private struct HistoryRow: View {
    let record: DictationRecord
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(record.date.formatted(date: .omitted, time: .shortened))
                        .font(.system(size: 11.5, weight: .medium))
                        .foregroundStyle(.secondary)
                    if record.status != .done {
                        Circle().fill(record.status.color).frame(width: 6, height: 6)
                        Text(record.status.label).font(.system(size: 10.5, weight: .medium)).foregroundStyle(record.status.color)
                    }
                    Spacer(minLength: 4)
                    Text(record.duration.durationLabel)
                        .font(.system(size: 11))
                        .monospacedDigit()
                        .foregroundStyle(.tertiary)
                }
                Text(preview)
                    .font(.system(size: 12.5))
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .foregroundStyle(record.finalText.isEmpty ? .secondary : .primary)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(selected ? Color.accentColor.opacity(0.16) : .clear)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(HoverButtonStyle(cornerRadius: 10))
    }

    private var preview: String {
        if !record.finalText.isEmpty { return record.finalText }
        switch record.status {
        case .recording: return L("正在录音…", "Recording…")
        case .processing: return L("正在处理…", "Processing…")
        default: return record.error ?? L("（没有文字）", "(no text)")
        }
    }
}

/// The selected dictation: when, how long, how many words and what it cost; the text itself; the raw transcript
/// behind a disclosure when it was cleaned up; and the actions.
private struct HistoryDetail: View {
    let record: DictationRecord
    let controller: SessionController
    let history: HistoryStore
    @State private var showRaw = false
    @State private var copied = false

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top, spacing: 10) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(record.date.formatted(date: .abbreviated, time: .shortened))
                            .font(.system(size: 14, weight: .semibold))
                        Text(details)
                            .font(.system(size: 11.5))
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                    Spacer(minLength: 8)
                    StatusPill(text: record.status.label, color: record.status.color)
                }
                .padding(16)

                CardDivider(inset: 0)

                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        if record.status == .cancelled {
                            Banner(symbol: "xmark.circle.fill", color: .gray,
                                   text: L("已取消，没有插入。这条会保留到 \(CancelPolicy.expiry(of: record.date).shortStamp)，之前可以复制或重新转写。",
                                           "Cancelled, so nothing was inserted. It's kept until \(CancelPolicy.expiry(of: record.date).shortStamp) to copy or re-transcribe.")) { EmptyView() }
                        }
                        if let error = record.error {
                            Banner(symbol: "exclamationmark.triangle.fill", color: .orange, text: error) { EmptyView() }
                        }
                        if record.finalText.isEmpty && (record.status == .recording || record.status == .processing) {
                            HStack(spacing: 8) {
                                ProgressView().controlSize(.small)
                                Text(record.status == .recording ? L("正在录音…", "Recording…") : L("正在处理…", "Processing…"))
                                    .foregroundStyle(.secondary)
                            }
                            .frame(maxWidth: .infinity, minHeight: 120)
                        } else {
                            Text(record.finalText.isEmpty ? L("（没有文字）", "(no text)") : record.finalText)
                                .font(.system(size: 14))
                                .lineSpacing(3)
                                .foregroundStyle(record.finalText.isEmpty ? .secondary : .primary)
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                            if record.polishedText != nil, !record.rawText.isEmpty {
                                DisclosureGroup(isExpanded: $showRaw) {
                                    Text(record.rawText)
                                        .font(.system(size: 12.5))
                                        .foregroundStyle(.secondary)
                                        .textSelection(.enabled)
                                        .padding(10)
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.04)))
                                        .padding(.top, 6)
                                } label: {
                                    Text(L("原始转写", "Raw transcript"))
                                        .font(.system(size: 12, weight: .medium))
                                        .foregroundStyle(.secondary)
                                }
                            }
                        }
                        if let timing = record.timing?.summary {
                            Label(timing, systemImage: "timer")
                                .font(.system(size: 11))
                                .foregroundStyle(.tertiary)
                                .textSelection(.enabled)
                        }
                    }
                    .padding(16)
                }

                CardDivider(inset: 0)

                HStack(spacing: 8) {
                    Button {
                        TextInserter.copyToClipboard(record.finalText)
                        copied = true
                        Task {
                            try? await Task.sleep(nanoseconds: 1_500_000_000)
                            copied = false
                        }
                    } label: {
                        Label(copied ? L("已复制", "Copied") : L("复制", "Copy"), systemImage: copied ? "checkmark" : "doc.on.doc")
                    }
                    .disabled(record.finalText.isEmpty)
                    if record.hasAudio {
                        Button { controller.retry(record, paste: false) } label: {
                            Label(L("重新转写", "Re-transcribe"), systemImage: "arrow.clockwise")
                        }
                        .help(L("完成后复制到剪贴板", "Copies the result to the clipboard"))
                        Button { NSWorkspace.shared.activateFileViewerSelecting([record.audioURL]) } label: {
                            Image(systemName: "folder")
                        }
                        .help(L("在访达中显示录音", "Show recording in Finder"))
                    }
                    Spacer()
                    Button(role: .destructive) { history.delete(record) } label: { Image(systemName: "trash") }
                        .help(L("删除", "Delete"))
                }
                .controlSize(.small)
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
            }
        }
        .frame(maxHeight: .infinity, alignment: .top)
    }

    /// "0:42 · 123 words · $0.0012"
    private var details: String {
        var parts = [record.duration.durationLabel]
        let words = WordCount.count(record.finalText)
        if words > 0 { parts.append(L("\(UsageFormat.count(words)) 字", "\(UsageFormat.count(words)) words")) }
        if let cost = record.cost { parts.append(UsageFormat.money(cost)) }
        return parts.joined(separator: " · ")
    }
}
