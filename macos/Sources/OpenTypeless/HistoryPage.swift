import AppKit
import SwiftUI
import TypelessCore

/// Every dictation, newest first and grouped by day, with search; the selected one's text, details and actions
/// on the right. How long recordings are kept sits in the toolbar, next to transcribing a file (also by dropping one
/// on the page).
struct HistoryPage: View {
    @ObservedObject var history: HistoryStore
    @ObservedObject var settings: AppSettings
    let controller: SessionController
    @ObservedObject var files: FileTranscriber
    @State private var selection: DictationRecord.ID?
    @State private var storageBytes: Int64?
    @State private var query = ""
    @State private var picking = false
    @State private var dropTargeted = false

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
                    Text(L("也可以把音频或视频文件拖到这里转写。", "You can also drop audio or video files here to transcribe them."))
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
                        HistoryDetail(record: record, controller: controller, history: history, files: files)
                            .id(record.id)
                    } else {
                        Card { Color.clear.frame(maxWidth: .infinity, maxHeight: .infinity) }
                    }
                }
                .frame(maxHeight: .infinity)
            }
        }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
                    .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.accentColor.opacity(0.06)))
                    .overlay(Label(L("松开即可转写音频或视频文件", "Drop audio or video files to transcribe them"), systemImage: "waveform")
                        .font(.system(size: 14, weight: .medium)).foregroundStyle(Color.accentColor))
                    .padding(12)
                    .allowsHitTesting(false)
            }
        }
        .dropDestination(for: URL.self) { urls, _ in
            let media = urls.filter(FileTranscriber.isMedia)
            files.offer(media)
            return !media.isEmpty
        } isTargeted: { dropTargeted = $0 }
        .fileImporter(isPresented: $picking, allowedContentTypes: FileTranscriber.contentTypes, allowsMultipleSelection: true) { result in
            if case let .success(urls) = result { files.offer(urls) }
        }
        .sheet(isPresented: Binding(get: { !files.offered.isEmpty }, set: { if !$0 { files.offered = [] } })) {
            FileImportSheet(urls: files.offered, settings: settings) {
                files.transcribe(files.offered, cleanUp: settings.fileCleanUp)
                // The newest record is the last file's: show it.
                selection = nil
            } cancel: {
                files.offered = []
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

            Button { picking = true } label: {
                Label(L("转写文件…", "Transcribe File…"), systemImage: "doc.badge.plus")
            }
            .help(L("转写语音备忘录、会议录音或视频（也可以直接拖到这里）",
                    "Transcribe a voice memo, meeting recording or video (or drop it here)"))

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
                || $0.sourceName?.localizedCaseInsensitiveContains(needle) == true
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
        let locale = Locale.app
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
                                    HistoryRow(record: record, progress: files.progress[record.id],
                                               selected: record.id == selected) { selection = record.id }
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

/// A list entry: time, duration and a status badge when it isn't simply done, then the start of the text. A file
/// shows its name, and how far it has got while it's being transcribed.
private struct HistoryRow: View {
    let record: DictationRecord
    let progress: FileTranscription.Progress?
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(record.date.appFormatted(time: .shortened))
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
                if let name = record.sourceName, record.isFile {
                    Label(name, systemImage: "doc")
                        .font(.system(size: 11.5, weight: .medium))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .foregroundStyle(.secondary)
                }
                if let progress {
                    Text(progress.label).font(.system(size: 12)).foregroundStyle(.secondary).monospacedDigit()
                    FileProgressBar(progress: progress)
                } else {
                    Text(preview)
                        .font(.system(size: 12.5))
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                        .foregroundStyle(record.finalText.isEmpty ? .secondary : .primary)
                }
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
    @ObservedObject var files: FileTranscriber
    @State private var showRaw = false
    @State private var copied = false
    @State private var confirmingDelete = false

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top, spacing: 10) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(record.date.appFormatted(date: .abbreviated, time: .shortened))
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
                        if let name = record.sourceName, record.isFile {
                            Label(name, systemImage: "doc")
                                .font(.system(size: 12, weight: .medium))
                                .foregroundStyle(.secondary)
                                .textSelection(.enabled)
                        }
                        if let error = record.error {
                            Banner(symbol: "exclamationmark.triangle.fill", color: .orange, text: error) { EmptyView() }
                        }
                        if let progress = files.progress[record.id] {
                            VStack(alignment: .leading, spacing: 8) {
                                Text(progress.label).foregroundStyle(.secondary).monospacedDigit()
                                FileProgressBar(progress: progress)
                            }
                            .frame(maxWidth: .infinity, minHeight: 120)
                        } else if record.finalText.isEmpty && (record.status == .recording || record.status == .processing) {
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
                    if files.isActive(record.id) {
                        Button { files.cancel(record.id) } label: {
                            Label(L("取消", "Cancel"), systemImage: "xmark.circle")
                        }
                    } else if record.hasAudio, record.isFile {
                        // A file goes back through the file queue: a long one mustn't hold up dictating.
                        Button { files.retry(record) } label: {
                            Label(L("重新转写", "Re-transcribe"), systemImage: "arrow.clockwise")
                        }
                        .help(L("用保存的音频重新转写", "Transcribes the saved audio again"))
                    } else if record.hasAudio {
                        Button { controller.retry(record, paste: false) } label: {
                            Label(L("重新转写", "Re-transcribe"), systemImage: "arrow.clockwise")
                        }
                        .help(L("完成后复制到剪贴板", "Copies the result to the clipboard"))
                    }
                    if record.hasAudio {
                        Button { NSWorkspace.shared.activateFileViewerSelecting([record.audioURL]) } label: {
                            Image(systemName: "folder")
                        }
                        .help(L("在访达中显示录音", "Show recording in Finder"))
                    }
                    Spacer()
                    Button(role: .destructive) {
                        // Text alone goes straight away; a recording can't be got back, so that asks first.
                        if record.hasAudio { confirmingDelete = true } else { history.delete(record) }
                    } label: { Image(systemName: "trash") }
                        .disabled(files.isActive(record.id))
                        .help(L("删除", "Delete"))
                        .accessibilityLabel(L("删除", "Delete"))
                        .confirmationDialog(L("删除这条听写？", "Delete this dictation?"), isPresented: $confirmingDelete) {
                            Button(L("删除", "Delete"), role: .destructive) { history.delete(record) }
                            Button(L("取消", "Cancel"), role: .cancel) {}
                        } message: {
                            Text(record.status == .failed
                                 ? L("这次听写还没转写成功，删除后录音也会删掉，无法再重试。",
                                     "It was never transcribed. Its recording is deleted too, so it can't be retried.")
                                 : L("录音也会一起删除，之后无法重新转写。",
                                     "Its recording is deleted too, so it can't be re-transcribed."))
                        }
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

/// How far a file being transcribed has got: a bar when that can be measured, a spinner otherwise.
private struct FileProgressBar: View {
    let progress: FileTranscription.Progress

    var body: some View {
        if let fraction = progress.fraction {
            ProgressView(value: fraction).progressViewStyle(.linear).controlSize(.small)
        } else {
            ProgressView().progressViewStyle(.linear).controlSize(.small)
        }
    }
}

/// Before files are transcribed: which ones and how long, what their speech-to-text should cost when the model has a
/// per-minute price, and whether to clean the text up.
private struct FileImportSheet: View {
    let urls: [URL]
    @ObservedObject var settings: AppSettings
    let start: () -> Void
    let cancel: () -> Void
    /// Each file's length, or why it can't be transcribed, once looked at.
    @State private var probes: [URL: Result<Double, FileTranscriptionError>] = [:]

    private var configured: Bool { settings.sttEndpoint != nil && settings.isConfigured(settings.sttProvider) }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(L("转写文件", "Transcribe Files")).font(.system(size: 17, weight: .semibold))
            Card {
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(Array(urls.enumerated()), id: \.offset) { index, url in
                            if index > 0 { CardDivider() }
                            row(url)
                        }
                    }
                }
                .frame(maxHeight: 200)
                .fixedSize(horizontal: false, vertical: true)
            }
            Text(estimate)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Card {
                CardRow(title: L("整理文字", "Clean up text"),
                        subtitle: settings.polishEnabled
                            ? L("用整理模型分段整理转写结果，另行计费", "Runs the transcript through the clean-up model, in parts (billed on top)")
                            : L("文字整理已在设置里关闭", "Clean-up is turned off in Settings"),
                        isOn: Binding(get: { settings.polishEnabled && settings.fileCleanUp }, set: { settings.fileCleanUp = $0 }))
                    .disabled(!settings.polishEnabled)
            }
            if !configured {
                Banner(symbol: "exclamationmark.triangle.fill", color: .orange,
                       text: L("请先在设置里配置语音转文字服务商（\(settings.sttProvider.displayName)）",
                               "Set up the speech-to-text provider (\(settings.sttProvider.displayName)) in Settings first")) { EmptyView() }
            }
            HStack {
                Text(L("结果保存在历史记录里，不会粘贴。", "The text is kept in History; nothing is pasted."))
                    .font(.system(size: 11.5))
                    .foregroundStyle(.tertiary)
                Spacer()
                Button(L("取消", "Cancel"), role: .cancel, action: cancel).keyboardShortcut(.cancelAction)
                Button(L("开始转写", "Transcribe"), action: start)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!configured)
            }
        }
        .padding(20)
        .frame(width: 480)
        .task(id: urls) {
            for url in urls where probes[url] == nil {
                do {
                    probes[url] = .success(try await AudioFileDecoder.duration(of: url))
                } catch {
                    probes[url] = .failure(FileTranscriptionError.reading(error))
                }
            }
        }
    }

    private func row(_ url: URL) -> some View {
        HStack(spacing: 10) {
            Image(systemName: FileTranscriber.isVideo(url) ? "film" : "waveform").foregroundStyle(.secondary).frame(width: 18)
            Text(url.lastPathComponent).font(.system(size: 13)).lineLimit(1).truncationMode(.middle)
            Spacer(minLength: 8)
            switch probes[url] {
            case nil:
                ProgressView().controlSize(.small)
            case let .success(seconds)?:
                Text(seconds.durationLabel).font(.system(size: 12)).monospacedDigit().foregroundStyle(.secondary)
            case let .failure(error)?:
                Text(error.localizedDescription).font(.system(size: 11.5)).foregroundStyle(.orange).lineLimit(2)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
    }

    /// "1 h 12 min in all; speech-to-text should cost about $0.43 (whisper-1 at $0.006/min). Silence isn't sent."
    private var estimate: String {
        guard probes.count == urls.count else { return L("正在读取文件长度…", "Reading how long the files are…") }
        let seconds = probes.values.reduce(0.0) { sum, probe in
            if case let .success(value) = probe { return sum + value }
            return sum
        }
        let total = UsageFormat.duration(seconds)
        let model = settings.sttModel
        let price = PriceStore.shared.price(settings.sttProvider, settings.sttModel)
        if let cost = FileTranscription.estimatedCost(seconds: seconds, price: price), let perMinute = price?.perMinute {
            let rate = UsageFormat.rate(perMinute)
            return L("共 \(total)，语音转文字预计约 \(UsageFormat.money(cost))（\(model) 每分钟 \(rate)）。静音部分不会发送。",
                     "\(total) in all; speech-to-text should cost about \(UsageFormat.money(cost)) (\(model) at \(rate)/min). Silence isn't sent.")
        }
        return L("共 \(total)。\(model) 没有按分钟的价格，无法预估费用。",
                 "\(total) in all. \(model) has no per-minute price, so the cost can't be estimated.")
    }
}
