import Foundation
import TypelessCore

struct DictationRecord: Codable, Identifiable, Equatable {
    enum Status: String, Codable {
        case recording, processing, done, polishFailed, failed
        /// Stopped with Esc after `CancelPolicy.keepAfterSeconds`: transcribed but not inserted, kept for a day.
        case cancelled

        var pruningKind: HistoryPruning.Kind {
            switch self {
            case .recording, .processing: return .inProgress
            case .done, .polishFailed: return .finished
            case .failed: return .failed
            case .cancelled: return .cancelled
            }
        }
    }

    let id: String
    let date: Date
    var duration: Double = 0
    var status: Status = .recording
    var rawText: String = ""
    var polishedText: String?
    /// What went in when snippets changed the final text (see SnippetExpander); nil when the final text went in as it was.
    var insertedText: String?
    var error: String?
    /// Per-chunk transcripts, so a retry only re-sends the chunks that failed.
    var chunkTexts: [Int: String] = [:]
    /// How the recording was cut into those chunks (`Chunker.layout`; nil before it was saved): a retry that cuts it
    /// differently can't reuse them by index.
    var chunkLayout: Int?
    /// The language hint those chunks were transcribed with ("" auto-detect; nil before it was saved): a retry with
    /// another one sends them again (see `DictationSettings.reusesTranscripts`).
    var chunkLanguage: String?
    var timing: Timing?
    /// What processing it cost so far in USD, retries included (nil when nothing could be priced).
    var cost: Double?
    /// Its words are in the usage totals (so re-transcribing it doesn't count them twice).
    var counted: Bool?
    /// The app profile it was made with (see AppProfile), so a retry uses that profile again while it exists; nil when
    /// none applied.
    var profileID: String?
    /// That profile's name, for History.
    var profileName: String?

    /// Where the wait after the key was released went, in seconds.
    struct Timing: Codable, Equatable {
        /// Release → complete raw transcript (only the last chunk is usually left by then).
        var transcription: Double?
        /// Chunks the backup speech-to-text route answered because the main one was late or failed.
        var transcriptionBackupChunks: Int?
        var polishFirstToken: Double?
        var polish: Double?
        var polishModel: String?
        var usedBackup: Bool?
        /// The last segment's transcript came from a speculative tail, sent while the speaker paused before letting go.
        var speculativeTail: Bool?
        /// The speech-to-text route that answered the last segment transcribed: "primary" or "backup".
        var lastChunkRoute: String?
        /// How long the clean-up taken over at release had been running by then (started while the speaker paused).
        var polishHeadStart: Double?
        /// Release → the text ready and handed over for delivery (focus is checked, then it's pasted or copied at once).
        var delivered: Double?
        /// "pasted"; "copied" (focus wasn't a text field); "failed" (the paste didn't land, or wasn't allowed; the text
        /// is on the clipboard).
        var delivery: String?

        /// "转写 0.8 秒 · 整理 1.2 秒（首字 0.4 秒，备用 deepseek/…）"
        var summary: String? {
            func secs(_ value: Double) -> String { String(format: "%.1f", value) + L(" 秒", " s") }
            var parts: [String] = []
            if let transcription {
                let backup = transcriptionBackupChunks.map { $0 == 1 ? L("（备用转写 1 段）", " (backup for 1 segment)") : L("（备用转写 \($0) 段）", " (backup for \($0) segments)") } ?? ""
                parts.append(L("转写 ", "Transcription ") + secs(transcription) + backup)
            }
            if let polish {
                var detail: [String] = []
                if let polishFirstToken { detail.append(L("首字 ", "first token ") + secs(polishFirstToken)) }
                if let polishModel { detail.append((usedBackup == true ? L("备用 ", "backup ") : "") + polishModel) }
                parts.append(L("整理 ", "Clean-up ") + secs(polish)
                             + (detail.isEmpty ? "" : L("（", " (") + detail.joined(separator: L("，", ", ")) + L("）", ")")))
            }
            return parts.isEmpty ? nil : parts.joined(separator: " · ")
        }
    }

    /// What the speaker said: cleaned up, or as transcribed. Word counts go by it, not by what snippets put in.
    var finalText: String { polishedText ?? rawText }
    /// What was (or would be) inserted: the final text with its snippets expanded.
    var outputText: String { insertedText ?? finalText }

    var folder: URL { AppPaths.sessions.appendingPathComponent(id, isDirectory: true) }
    var audioURL: URL { folder.appendingPathComponent("audio.wav") }
    var hasAudio: Bool { FileManager.default.fileExists(atPath: audioURL.path) }
}

/// How long finished dictations are kept. The recording is the big part (about 1.9 MB per minute
/// of 16 kHz WAV); the text is a few KB.
enum HistoryRetention: String, CaseIterable, Identifiable {
    case none, day, week, month, year, forever

    var id: String { rawValue }

    var label: String {
        switch self {
        case .none: return L("不保存录音", "Don't keep recordings")
        case .day: return L("保存 1 天", "1 day")
        case .week: return L("保存 7 天", "7 days")
        case .month: return L("保存 1 个月", "1 month")
        case .year: return L("保存 1 年", "1 year")
        case .forever: return L("永不删除", "Forever")
        }
    }

    /// nil = never expires.
    var maxAge: TimeInterval? {
        let day: TimeInterval = 24 * 60 * 60
        switch self {
        case .none: return 0
        case .day: return day
        case .week: return 7 * day
        case .month: return 30 * day
        case .year: return 365 * day
        case .forever: return nil
        }
    }
}

/// Every dictation is saved (audio + transcripts) so nothing is ever lost to a failure. What expires, by the
/// retention setting and a cap on the recordings' total size, is decided by `HistoryPruning`: finished dictations
/// lose their recording first and their text later; failed ones keep theirs for at least a month, so they can be
/// re-sent.
@MainActor
final class HistoryStore: ObservableObject {
    static let shared = HistoryStore()

    @Published private(set) var records: [DictationRecord] = []
    /// Bumped whenever files are written or removed, so views can refresh the storage figure.
    @Published private(set) var changeCount = 0

    private var retentionTimer: Timer?
    /// Recording sizes by record ID (0 = none), measured once a dictation is over, since its recording doesn't change
    /// after that: the size cap then doesn't look at every file on disk after each dictation.
    private var audioSizes: [String: Int64] = [:]

    private init() {
        load()
        // Expiry is by age, so a menu-bar app that runs for days has to check on its own.
        retentionTimer = Timer.scheduledTimer(withTimeInterval: 60 * 60, repeats: true) { _ in
            Task { @MainActor in HistoryStore.shared.applyRetention() }
        }
    }

    func create() -> DictationRecord {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let id = formatter.string(from: Date()) + "-" + UUID().uuidString.prefix(4)
        var record = DictationRecord(id: String(id), date: Date())
        record.chunkLayout = Chunker.layout
        try? FileManager.default.createDirectory(at: record.folder, withIntermediateDirectories: true)
        records.insert(record, at: 0)
        save(record)
        return record
    }

    func update(_ record: DictationRecord) {
        if let index = records.firstIndex(where: { $0.id == record.id }) {
            records[index] = record
        } else {
            records.insert(record, at: 0)
        }
        save(record)
        changeCount += 1
        if record.status == .done || record.status == .polishFailed { applyRetention() }
    }

    func delete(_ record: DictationRecord) {
        records.removeAll { $0.id == record.id }
        try? FileManager.default.removeItem(at: record.folder)
        audioSizes[record.id] = nil
        changeCount += 1
    }

    private func save(_ record: DictationRecord) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(record) else { return }
        try? data.write(to: record.folder.appendingPathComponent("session.json"), options: .atomic)
    }

    private func load() {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let folders = (try? FileManager.default.contentsOfDirectory(
            at: AppPaths.sessions, includingPropertiesForKeys: nil)) ?? []
        var loaded: [DictationRecord] = folders.compactMap { folder in
            guard let data = try? Data(contentsOf: folder.appendingPathComponent("session.json")) else { return nil }
            return try? decoder.decode(DictationRecord.self, from: data)
        }
        loaded.sort { $0.date > $1.date }
        // A record still "recording"/"processing" at launch means the app quit mid-way; the audio is
        // on disk, so surface it as failed and retryable.
        for index in loaded.indices where loaded[index].status == .recording || loaded[index].status == .processing {
            loaded[index].status = .failed
            loaded[index].error = L("上次运行中断（音频已保存，可重试）", "Interrupted last time (audio saved — you can retry)")
            save(loaded[index])
        }
        records = loaded
        applyRetention()
    }

    /// Deletes what has expired by the retention setting, and the oldest recordings while they take more than
    /// `HistoryPruning.audioBudget` (see HistoryPruning).
    func applyRetention() {
        let entries = records.map { HistoryPruning.Entry(kind: $0.status.pruningKind, date: $0.date, audioBytes: audioBytes(of: $0)) }
        let actions = HistoryPruning.plan(entries, maxAge: AppSettings.shared.historyRetention.maxAge)
        var kept: [DictationRecord] = []
        var changed = false
        for (record, action) in zip(records, actions) {
            switch action {
            case .keep:
                kept.append(record)
            case .removeAudio:
                try? FileManager.default.removeItem(at: record.audioURL)
                audioSizes[record.id] = 0
                kept.append(record)
                changed = true
            case .remove:
                try? FileManager.default.removeItem(at: record.folder)
                audioSizes[record.id] = nil
                changed = true
            }
        }
        // Runs after every dictation and hourly: publishing when nothing expired would redraw History and walk every
        // recording again to measure the storage.
        guard changed else { return }
        records = kept
        changeCount += 1
    }

    private func audioBytes(of record: DictationRecord) -> Int64 {
        if let known = audioSizes[record.id] { return known }
        let size = (try? FileManager.default.attributesOfItem(atPath: record.audioURL.path)[.size] as? NSNumber)?.int64Value ?? 0
        // One still being recorded is measured again next time.
        if record.status.pruningKind != .inProgress { audioSizes[record.id] = size }
        return size
    }

    /// Bytes used by every saved dictation (recordings and transcripts).
    nonisolated static func storageBytes() -> Int64 {
        let keys: Set<URLResourceKey> = [.totalFileAllocatedSizeKey, .isRegularFileKey]
        guard let files = FileManager.default.enumerator(at: AppPaths.sessions, includingPropertiesForKeys: Array(keys))
        else { return 0 }
        var total: Int64 = 0
        for case let url as URL in files {
            guard let values = try? url.resourceValues(forKeys: keys), values.isRegularFile == true else { continue }
            total += Int64(values.totalFileAllocatedSize ?? 0)
        }
        return total
    }
}
