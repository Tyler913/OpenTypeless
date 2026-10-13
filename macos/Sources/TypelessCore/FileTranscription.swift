import Foundation

/// Where a History entry's audio came from. Stored as `source` in session.json, only for a file: a record without one
/// (every dictation, and everything saved before files could be transcribed) is a dictation, and a value this version
/// doesn't know reads as a dictation too, rather than losing the whole record.
public enum RecordSource: String, Codable, Sendable {
    case dictation, file

    public init(from decoder: Decoder) throws {
        self = RecordSource(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .dictation
    }
}

/// Transcribing an audio or video file (a voice memo, a meeting, a video) with the pipeline dictation uses: chunked at
/// pauses, each chunk retried and backed up, then optionally cleaned up in parts, never pasted. The file is decoded into
/// the record's recording first, so History's Re-transcribe works as it does for a dictation; then the recording is
/// handed to the pipeline a block at a time from disk, so an hour of it (about 115 MB) is never all in memory.
///
/// A file mustn't get in the way of dictating: it's transcribed with fewer requests at once than a dictation uses, no
/// new audio is handed out while a dictation is under way, and its clean-up sends a few parts at a time.
public enum FileTranscription {
    /// Speech-to-text requests out at once for a file (a dictation has 3 of its own).
    public static let maxConcurrentRequests = 2
    /// Chunks handed to the pipeline and still waiting for their transcript before more audio is read: enough to keep
    /// `maxConcurrentRequests` busy, few enough that only a minute or two of audio sits in memory.
    public static let maxOutstandingChunks = 4
    /// Clean-up parts out at once (a dictation sends all of its parts together; a long file's transcript has dozens).
    public static let cleanUpConcurrency = 2
    /// How much of the recording is read from disk at a time.
    public static let blockSeconds = 10.0
    /// How often the transcripts so far are saved while a file is transcribed, in seconds: if the app quits, a retry
    /// only sends what wasn't transcribed yet.
    public static let saveInterval = 20.0

    /// How many chunks may be out before more audio is handed over: none while a dictation is under way, so a file
    /// never competes with it for more than the requests it already has out.
    public static func outstandingLimit(dictating: Bool) -> Int {
        dictating ? 0 : maxOutstandingChunks
    }

    /// What transcribing `seconds` of audio should cost at `price`, in USD; nil when the model has no per-minute price
    /// (one billed by the token, or no price entered) or the length isn't known. Silence isn't sent, so it's a ceiling.
    public static func estimatedCost(seconds: Double, price: ModelPrice?) -> Double? {
        guard let perMinute = price?.perMinute, seconds > 0 else { return nil }
        return seconds / 60 * perMinute
    }

    /// Where a file being transcribed has got to. Kept in memory only: a file still under way when the app quits shows
    /// as interrupted at the next launch, like a dictation.
    public enum Progress: Equatable, Sendable {
        /// Waiting for the file before it.
        case queued
        /// Being read into the recording; how far, 0…1 (nil when its length isn't known).
        case reading(Double?)
        case transcribing(done: Int, total: Int)
        case cleaningUp

        /// "Reading the file… 40%", "Transcribing 3/12 segments"
        public var label: String {
            switch self {
            case .queued:
                return L("排队中…", "Waiting…")
            case let .reading(fraction?):
                let percent = Int((min(max(fraction, 0), 1) * 100).rounded())
                return L("正在读取文件… \(percent)%", "Reading the file… \(percent)%")
            case .reading(nil):
                return L("正在读取文件…", "Reading the file…")
            case let .transcribing(done, total):
                return L("正在转写 \(done)/\(total) 段", "Transcribing \(done)/\(total) segments")
            case .cleaningUp:
                return L("正在整理…", "Cleaning up…")
            }
        }

        /// How far along a progress bar is, 0…1; nil when there's nothing to measure (it spins instead).
        public var fraction: Double? {
            switch self {
            case .queued, .cleaningUp, .reading(nil): return nil
            case let .reading(fraction?): return min(max(fraction, 0), 1)
            case let .transcribing(done, total): return total > 0 ? min(Double(done) / Double(total), 1) : nil
            }
        }
    }

    /// Whether `error` (or one it wraps) says the disk is full.
    public static func isDiskFull(_ error: Error) -> Bool {
        let error = error as NSError
        if error.domain == NSCocoaErrorDomain, error.code == NSFileWriteOutOfSpaceError { return true }
        if error.domain == NSPOSIXErrorDomain, error.code == Int(ENOSPC) || error.code == Int(EDQUOT) { return true }
        return (error.userInfo[NSUnderlyingErrorKey] as? Error).map(isDiskFull) ?? false
    }
}

/// Why a file couldn't be transcribed, as History shows it on the record.
public enum FileTranscriptionError: Error, LocalizedError, Equatable, Sendable {
    case notFound
    case permissionDenied
    /// Damaged, or in a format this computer can't decode; with what the system said, when that says more.
    case unsupported(String?)
    case noAudioTrack
    case diskFull
    /// The recording couldn't be written for some other reason.
    case writeFailed(String)

    public var errorDescription: String? {
        switch self {
        case .notFound:
            return L("找不到这个文件（可能已被移动或删除）", "The file can't be found (it may have been moved or deleted)")
        case .permissionDenied:
            return L("没有读取这个文件的权限", "No permission to read this file")
        case let .unsupported(detail?):
            return L("无法读取这个文件：已损坏，或是不支持的格式（\(detail)）",
                     "Can't read this file: it's damaged or in a format that isn't supported (\(detail))")
        case .unsupported(nil):
            return L("无法读取这个文件：已损坏，或是不支持的格式", "Can't read this file: it's damaged or in a format that isn't supported")
        case .noAudioTrack:
            return L("这个文件里没有音轨", "This file has no audio track")
        case .diskFull:
            return L("磁盘空间不足，无法保存音频", "Not enough disk space to save the audio")
        case let .writeFailed(message):
            return L("无法保存音频：", "Can't save the audio: ") + message
        }
    }

    /// A failure to write the recording.
    public static func writing(_ error: Error) -> FileTranscriptionError {
        FileTranscription.isDiskFull(error) ? .diskFull : .writeFailed(error.localizedDescription)
    }

    /// A failure to open or decode the file: a missing file and a refused one say so; anything else is taken for a
    /// damaged or unsupported file (AVFoundation's own messages say no more than "The operation could not be completed").
    public static func reading(_ error: Error) -> FileTranscriptionError {
        if let known = error as? FileTranscriptionError { return known }
        let error = error as NSError
        switch (error.domain, error.code) {
        case (NSCocoaErrorDomain, NSFileReadNoSuchFileError), (NSCocoaErrorDomain, NSFileNoSuchFileError),
             (NSPOSIXErrorDomain, Int(ENOENT)):
            return .notFound
        case (NSCocoaErrorDomain, NSFileReadNoPermissionError), (NSPOSIXErrorDomain, Int(EACCES)),
             (NSPOSIXErrorDomain, Int(EPERM)):
            return .permissionDenied
        default:
            if let underlying = error.userInfo[NSUnderlyingErrorKey] as? Error,
               case let known = reading(underlying), known == .notFound || known == .permissionDenied {
                return known
            }
            return .unsupported(nil)
        }
    }
}
