import Foundation

/// What the live preview shows above the recording capsule: the end of what's been recognised so far.
public enum LivePreviewText {
    /// The last `limit` characters on one line, starting at a word boundary when one is near, with a leading "…"
    /// when anything was cut. Characters are whole (an emoji or a CJK character is never split).
    public static func tail(_ text: String, limit: Int = 60) -> String {
        let line = text.split(whereSeparator: \.isNewline).joined(separator: " ").trimmingCharacters(in: .whitespaces)
        guard line.count > limit else { return line }
        var tail = String(line.suffix(limit))
        // Latin text: drop the cut-off word at the start, when a space comes early enough.
        if let space = tail.firstIndex(of: " "), tail.distance(from: tail.startIndex, to: space) < 15 {
            tail = String(tail[tail.index(after: space)...])
        }
        return "…" + tail
    }
}
