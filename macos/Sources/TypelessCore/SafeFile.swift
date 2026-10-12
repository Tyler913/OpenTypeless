import Foundation

/// The app's own small files (API keys, usage totals) are rewritten whole on every change, so one that can't be read
/// would be replaced by the defaults on the next save. Moving it aside first, as `<name>.corrupt-<yyyyMMdd-HHmmss>`,
/// keeps what was in it.
public enum SafeFile {
    /// Renames a file that couldn't be read or decoded out of the way. Returns where it went, or nil if it couldn't be moved.
    @discardableResult
    public static func moveAside(_ url: URL, now: Date = Date()) -> URL? {
        let destination = asideURL(for: url, at: now)
        guard (try? FileManager.default.moveItem(at: url, to: destination)) != nil else { return nil }
        return destination
    }

    /// `usage.json` → `usage.json.corrupt-20261011-143005` (local time), numbered if one from that second is already there.
    static func asideURL(for url: URL, at date: Date) -> URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let base = url.path + ".corrupt-" + formatter.string(from: date)
        var path = base
        var number = 2
        while FileManager.default.fileExists(atPath: path) {
            path = base + "-\(number)"
            number += 1
        }
        return URL(fileURLWithPath: path)
    }
}
