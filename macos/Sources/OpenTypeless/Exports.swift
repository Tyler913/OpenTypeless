import AppKit
import OSLog
import TypelessCore
import UniformTypeIdentifiers

/// Saving History, a recording or the vocabulary to a file the user picks, and reading a vocabulary file back. Files
/// are written off the main thread, so thousands of dictations never freeze the window, and atomically (a temporary
/// file that then takes the old one's place), so a failure never leaves half a file. A failure comes back as a
/// sentence to show, never a crash.
enum Exports {
    private static let log = Logger(subsystem: "local.opentypeless.app", category: "export")

    /// Asks where to save, then hands back the place (nothing happens when the user cancels).
    @MainActor
    static func chooseDestination(name: String, type: UTType, completion: @escaping @MainActor (URL) -> Void) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = name
        panel.allowedContentTypes = [type]
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            MainActor.assumeIsolated { completion(url) }
        }
    }

    /// Asks for a file to open.
    @MainActor
    static func chooseFile(types: [UTType], completion: @escaping @MainActor (URL) -> Void) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = types
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            MainActor.assumeIsolated { completion(url) }
        }
    }

    /// "OpenTypeless History 2026-10-12.csv", "OpenTypeless 2026-10-12 14-32-05.wav": the name the save dialog suggests
    /// (dashes in the time, as on Windows: Finder shows a colon as a slash and Windows refuses one).
    static func fileName(_ stem: String, date: Date? = nil, time: Bool = false, extension fileExtension: String) -> String {
        guard let date else { return stem + "." + fileExtension }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = time ? "yyyy-MM-dd HH-mm-ss" : "yyyy-MM-dd"
        return stem + " " + formatter.string(from: date) + "." + fileExtension
    }

    /// Makes the file's contents (off the main thread) and writes them. Nil when it worked, otherwise what to tell the user.
    static func write(to url: URL, _ contents: @escaping @Sendable () -> Data) async -> String? {
        await Task.detached(priority: .userInitiated) { () -> String? in
            do {
                try contents().write(to: url, options: .atomic)
                return nil
            } catch {
                Self.log.error("can't write \(url.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
                return Self.message(for: error)
            }
        }.value
    }

    /// Copies a file, a recording say, to where the user chose. Nil when it worked, otherwise what to tell the user.
    static func copy(_ source: URL, to destination: URL) async -> String? {
        await Task.detached(priority: .userInitiated) { () -> String? in
            let files = FileManager.default
            do {
                // Copied next to the destination first, then moved into place, so a copy that fails half way (the
                // disk filling up) doesn't leave a broken file where the user will look for it.
                let folder = try files.url(for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: destination, create: true)
                defer { try? files.removeItem(at: folder) }
                let temporary = folder.appendingPathComponent(destination.lastPathComponent)
                try files.copyItem(at: source, to: temporary)
                if files.fileExists(atPath: destination.path) {
                    _ = try files.replaceItemAt(destination, withItemAt: temporary)
                } else {
                    try files.moveItem(at: temporary, to: destination)
                }
                return nil
            } catch {
                Self.log.error("can't copy to \(destination.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
                return Self.message(for: error)
            }
        }.value
    }

    /// Reads a vocabulary file (off the main thread): its terms, or what to tell the user.
    static func readVocabulary(_ url: URL) async -> Result<[String], VocabularyFileError> {
        await Task.detached(priority: .userInitiated) { () -> Result<[String], VocabularyFileError> in
            do {
                let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                // A vocabulary is a few KB; anything this big was picked by mistake (a History export, say).
                guard size <= 10_000_000 else { return .failure(VocabularyFileError(message: L("这个文件太大了，不像是词汇表。", "That file is too big to be a vocabulary list."))) }
                guard let text = VocabularyTransfer.decode(try Data(contentsOf: url)) else {
                    return .failure(VocabularyFileError(message: L("这个文件不是 UTF-8 文本。请另存为 UTF-8 格式（Excel 里选择“CSV UTF-8”）后再导入。",
                                                                   "This file isn't UTF-8 text. Save it as UTF-8 (in Excel, choose “CSV UTF-8”) and import it again.")))
                }
                return .success(VocabularyTransfer.terms(in: text, fileExtension: url.pathExtension))
            } catch {
                Self.log.error("can't read \(url.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
                return .failure(VocabularyFileError(message: Self.message(for: error)))
            }
        }.value
    }

    struct VocabularyFileError: Error {
        let message: String
    }

    /// The usual failures in the app's words; anything else as the system describes it.
    static func message(for error: Error) -> String {
        let error = error as NSError
        let underlying = error.userInfo[NSUnderlyingErrorKey] as? NSError
        let posix: Int? = error.domain == NSPOSIXErrorDomain ? error.code : underlying?.domain == NSPOSIXErrorDomain ? underlying?.code : nil
        if error.domain == NSCocoaErrorDomain {
            switch CocoaError.Code(rawValue: error.code) {
            case .fileWriteNoPermission, .fileReadNoPermission: return noPermission
            case .fileWriteOutOfSpace: return outOfSpace
            case .fileWriteVolumeReadOnly: return readOnly
            case .fileNoSuchFile, .fileReadNoSuchFile: return gone
            default: break
            }
        }
        switch posix.flatMap({ POSIXErrorCode(rawValue: Int32($0)) }) {
        case .EACCES?, .EPERM?: return noPermission
        case .ENOSPC?, .EDQUOT?: return outOfSpace
        case .EROFS?: return readOnly
        case .ENOENT?: return gone
        default: return error.localizedDescription
        }
    }

    private static var noPermission: String { L("没有权限使用这个位置，换个文件夹试试。", "No permission to use that location. Try another folder.") }
    private static var outOfSpace: String { L("磁盘空间不足。", "There isn't enough space on the disk.") }
    private static var readOnly: String { L("这个位置是只读的，换个文件夹试试。", "That location is read-only. Try another folder.") }
    private static var gone: String { L("文件已经不在了。", "The file is no longer there.") }
}

extension HistoryExport.Format {
    var contentType: UTType {
        switch self {
        case .csv: return .commaSeparatedText
        case .json: return .json
        case .markdown: return UTType(filenameExtension: "md", conformingTo: .plainText) ?? .plainText
        }
    }
}
