import Foundation
import TypelessCore

struct DictationRecord: Codable, Identifiable, Equatable {
    enum Status: String, Codable {
        case recording, processing, done, polishFailed, failed
    }

    let id: String
    let date: Date
    var duration: Double = 0
    var status: Status = .recording
    var rawText: String = ""
    var polishedText: String?
    var error: String?
    /// Per-chunk transcripts, so a retry only re-sends the chunks that failed.
    var chunkTexts: [Int: String] = [:]
    var timing: Timing?

    /// Where the wait after the key was released went, in seconds.
    struct Timing: Codable, Equatable {
        /// Release → complete raw transcript (only the last chunk is usually left by then).
        var transcription: Double?
        var polishFirstToken: Double?
        var polish: Double?
        var polishModel: String?
        var usedBackup: Bool?

        /// "转写 0.8 秒 · 整理 1.2 秒（首字 0.4 秒，备用 deepseek/…）"
        var summary: String? {
            func secs(_ value: Double) -> String { String(format: "%.1f", value) + L(" 秒", " s") }
            var parts: [String] = []
            if let transcription { parts.append(L("转写 ", "Transcription ") + secs(transcription)) }
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

    /// What was (or would be) inserted.
    var finalText: String { polishedText ?? rawText }

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

/// Every dictation is saved (audio + transcripts) so nothing is ever lost to a failure. Finished
/// dictations older than the retention setting lose their recording; their text stays in History
/// among the newest `minimumTextRecords`, and older ones are removed entirely. Failed dictations
/// keep everything until retried or deleted, so they can always be re-sent.
@MainActor
final class HistoryStore: ObservableObject {
    static let shared = HistoryStore()

    @Published private(set) var records: [DictationRecord] = []
    /// Bumped whenever files are written or removed, so views can refresh the storage figure.
    @Published private(set) var changeCount = 0

    private let minimumTextRecords = 200
    private var retentionTimer: Timer?

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
        let record = DictationRecord(id: String(id), date: Date())
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

    /// Deletes what the retention setting says has expired.
    func applyRetention() {
        let retention = AppSettings.shared.historyRetention
        let now = Date()
        var kept: [DictationRecord] = []
        for (index, record) in records.enumerated() {
            let finished = record.status == .done || record.status == .polishFailed
            let expired = retention.maxAge.map { now.timeIntervalSince(record.date) >= $0 } ?? false
            guard finished, expired else { kept.append(record); continue }
            if index >= minimumTextRecords {
                try? FileManager.default.removeItem(at: record.folder)
            } else {
                if record.hasAudio { try? FileManager.default.removeItem(at: record.audioURL) }
                kept.append(record)
            }
        }
        records = kept
        changeCount += 1
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
