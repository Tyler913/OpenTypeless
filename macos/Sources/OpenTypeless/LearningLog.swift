import Foundation
import TypelessCore

/// A local record of what learning from edits did after each paste: which app, whether its field could be read,
/// why the watch ended, and each change found with the reason it was or wasn't learned. It is how a "nothing ever
/// gets learned" report can be looked into. One JSON object per line in `learning-log.jsonl`, next to the history;
/// never sent anywhere, and cut back to the newest half once it passes 512 KB.
enum LearningLog {
    static var url: URL { AppPaths.support.appendingPathComponent("learning-log.jsonl") }
    private static let limit = 512 * 1024
    private static let queue = DispatchQueue(label: "OpenTypeless.LearningLog")

    static func write(_ event: String, app: String?, _ fields: [String: Any]) {
        var entry = fields
        entry["time"] = ISO8601DateFormatter().string(from: Date())
        entry["event"] = event
        if let app { entry["app"] = app }
        guard var line = try? JSONSerialization.data(withJSONObject: entry, options: [.sortedKeys, .withoutEscapingSlashes])
        else { return }
        line.append(0x0A)
        queue.async {
            let url = url
            if let handle = try? FileHandle(forWritingTo: url) {
                handle.seekToEndOfFile()
                handle.write(line)
                try? handle.close()
            } else {
                try? line.write(to: url)
            }
            trimIfNeeded(url)
        }
    }

    static func review(_ review: CorrectionLearner.Review) -> [String: Any] {
        var fields: [String: Any] = [:]
        if let skipped = review.skipped { fields["skipped"] = skipped }
        fields["changes"] = review.changes.map { change -> [String: Any] in
            var item: [String: Any] = ["heard": change.correction.heard, "corrected": change.correction.corrected]
            item["rejected"] = change.rejected ?? NSNull()
            return item
        }
        return fields
    }

    private static func trimIfNeeded(_ url: URL) {
        guard let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int, size > limit,
              let data = try? Data(contentsOf: url) else { return }
        let tail = data.suffix(limit / 2)
        guard let newline = tail.firstIndex(of: 0x0A) else { return }
        try? Data(tail[(newline + 1)...]).write(to: url, options: .atomic)
    }
}
