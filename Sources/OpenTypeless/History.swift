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

    /// What was (or would be) inserted.
    var finalText: String { polishedText ?? rawText }

    var folder: URL { AppPaths.sessions.appendingPathComponent(id, isDirectory: true) }
    var audioURL: URL { folder.appendingPathComponent("audio.wav") }
    var hasAudio: Bool { FileManager.default.fileExists(atPath: audioURL.path) }
}

/// Every dictation is saved (audio + transcripts) so nothing is ever lost to a failure.
@MainActor
final class HistoryStore: ObservableObject {
    static let shared = HistoryStore()

    @Published private(set) var records: [DictationRecord] = []

    private let keepRecords = 200
    private let keepAudioRecords = 30

    private init() {
        load()
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
    }

    func delete(_ record: DictationRecord) {
        records.removeAll { $0.id == record.id }
        try? FileManager.default.removeItem(at: record.folder)
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
        prune()
    }

    private func prune() {
        for (index, record) in records.enumerated() {
            if index >= keepRecords {
                try? FileManager.default.removeItem(at: record.folder)
            } else if index >= keepAudioRecords, record.status == .done || record.status == .polishFailed {
                try? FileManager.default.removeItem(at: record.audioURL)
            }
        }
        if records.count > keepRecords { records.removeLast(records.count - keepRecords) }
    }
}
