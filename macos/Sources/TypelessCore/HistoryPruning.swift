import Foundation

/// What History deletes. Every dictation is saved with its recording (about 1.9 MB a minute of 16 kHz WAV) so nothing
/// is lost to a failure, which comes to tens of MB a day for someone who dictates all day. The retention setting goes
/// by age; on top of it the recordings together are held under `audioBudget`, so "1 year" or "Forever" can't fill
/// the disk.
///
/// - A finished dictation older than the retention setting loses its recording; its text stays among the newest
///   `minimumTextRecords`, and older ones are removed entirely.
/// - A failed one keeps its recording so it can be re-sent, but not forever: it expires the same way once it is
///   older than the retention setting and at least `failedMinimumAge`.
/// - A cancelled one goes a day after it was made, whatever the setting (see CancelPolicy).
/// - While the recordings still come to more than `audioBudget`, the oldest finished and cancelled dictations lose
///   theirs (the text stays), but never the newest `newestKeptWithAudio` nor one still being recorded or processed.
public enum HistoryPruning {
    /// About nine hours of recordings.
    public static let audioBudget: Int64 = 1_000_000_000
    public static let newestKeptWithAudio = 10
    public static let minimumTextRecords = 200
    public static let failedMinimumAge: TimeInterval = 30 * 24 * 60 * 60

    public enum Kind: Sendable {
        /// Still being recorded or processed (a retry included).
        case inProgress
        /// Done, or inserted without its clean-up.
        case finished
        case failed
        case cancelled
    }

    public struct Entry: Equatable, Sendable {
        public var kind: Kind
        public var date: Date
        /// The size of its recording; 0 when there is none.
        public var audioBytes: Int64

        public init(kind: Kind, date: Date, audioBytes: Int64) {
            self.kind = kind
            self.date = date
            self.audioBytes = audioBytes
        }
    }

    public enum Action: Equatable, Sendable {
        case keep
        /// Delete the recording, keep the text.
        case removeAudio
        /// Delete the whole dictation.
        case remove
    }

    /// How long a failed dictation is kept, given how long the retention setting keeps finished ones (nil = forever).
    public static func failedMaxAge(retention maxAge: TimeInterval?) -> TimeInterval? {
        maxAge.map { max($0, failedMinimumAge) }
    }

    /// What to do with each of `entries` (newest first, as History lists them), in the same order.
    public static func plan(_ entries: [Entry], maxAge: TimeInterval?, now: Date = Date(),
                            audioBudget: Int64 = audioBudget) -> [Action] {
        var actions = [Action](repeating: .keep, count: entries.count)
        for (index, entry) in entries.enumerated() {
            let limit: TimeInterval?
            switch entry.kind {
            case .inProgress:
                continue
            case .cancelled:
                if CancelPolicy.isExpired(recordedAt: entry.date, now: now) { actions[index] = .remove }
                continue
            case .finished:
                limit = maxAge
            case .failed:
                limit = failedMaxAge(retention: maxAge)
            }
            guard let limit, now.timeIntervalSince(entry.date) >= limit else { continue }
            if index >= minimumTextRecords {
                actions[index] = .remove
            } else if entry.audioBytes > 0 {
                actions[index] = .removeAudio
            }
        }

        // Then the size cap, oldest first, over the recordings still kept.
        var total = zip(entries, actions).reduce(Int64(0)) { sum, pair in pair.1 == .keep ? sum + pair.0.audioBytes : sum }
        var index = entries.count - 1
        while total > audioBudget, index >= newestKeptWithAudio {
            let entry = entries[index]
            if actions[index] == .keep, entry.audioBytes > 0, entry.kind == .finished || entry.kind == .cancelled {
                actions[index] = .removeAudio
                total -= entry.audioBytes
            }
            index -= 1
        }
        return actions
    }
}
