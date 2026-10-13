import Foundation

/// The vocabulary in and out of a file. Exported as CSV (each term with what recognition heard instead, when it was
/// learned from a correction) or as plain text, a term per line; imported from either, or from a list another app
/// exported, and merged into the vocabulary without duplicates.
public enum VocabularyTransfer {
    /// A vocabulary term, and the misheard forms it was learned from (none for a term the user typed in).
    public struct Term: Sendable, Equatable {
        public var term: String
        public var heardAs: [String]

        public init(term: String, heardAs: [String] = []) {
            self.term = term
            self.heardAs = heardAs
        }
    }

    /// A header with any of these as its first column is a header, not a term ("term" is what the export writes).
    static let headers: Set<String> = ["term", "terms", "vocabulary", "词汇"]

    /// "term,heardAs" and a line per term, misheard forms separated by "; ", CRLF line ends.
    public static func csv(_ terms: [Term]) -> String {
        var out = "term,heardAs\r\n"
        for term in terms {
            out += CSV.field(term.term) + "," + CSV.field(term.heardAs.joined(separator: "; ")) + "\r\n"
        }
        return out
    }

    /// A term per line.
    public static func text(_ terms: [Term]) -> String {
        terms.map { $0.term + "\n" }.joined()
    }

    /// The file's bytes: UTF-8, the CSV with a byte-order mark so Excel reads it as UTF-8.
    public static func data(_ terms: [Term], csv asCSV: Bool) -> Data {
        asCSV ? HistoryExport.byteOrderMark + Data(csv(terms).utf8) : Data(text(terms).utf8)
    }

    /// An imported file's text: UTF-8 with or without a byte-order mark, or UTF-16 with one (Excel's "Unicode text").
    /// Nil for anything else, such as a CSV Excel saved in the system's legacy encoding: guessing that would import
    /// garbled terms.
    public static func decode(_ data: Data) -> String? {
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { return String(data: data.dropFirst(3), encoding: .utf8) }
        if data.starts(with: [0xFF, 0xFE]) { return String(data: data.dropFirst(2), encoding: .utf16LittleEndian) }
        if data.starts(with: [0xFE, 0xFF]) { return String(data: data.dropFirst(2), encoding: .utf16BigEndian) }
        return String(data: data, encoding: .utf8)
    }

    /// The terms in a CSV: the first column of each row (the others, like the export's misheard forms, aren't terms),
    /// after a header row.
    public static func terms(fromCSV text: String) -> [String] {
        var rows = CSV.rows(text)
        if let first = rows.first?.first, headers.contains(first.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()) {
            rows.removeFirst()
        }
        return rows.flatMap { split($0.first ?? "") }
    }

    /// The terms in a text file: a term per line, or separated by commas the way the vocabulary field takes them.
    public static func terms(fromText text: String) -> [String] {
        split(text)
    }

    /// What importing adds: the imported terms not already in the vocabulary, compared without case, each once and
    /// spelled as it first appears, in the file's order.
    public static func newTerms(_ imported: [String], existing: [String]) -> [String] {
        var seen = Set(existing.map { $0.lowercased() })
        return imported.filter { seen.insert($0.lowercased()).inserted }
    }

    /// Splits where the vocabulary field does (commas, full-width commas, line breaks), since a term that held one
    /// would be cut there once it's in the field anyway.
    private static func split(_ text: String) -> [String] {
        text.unicodeScalars
            .split(whereSeparator: { $0 == "," || $0 == "，" || $0 == "\n" || $0 == "\r" })
            .map { String(String.UnicodeScalarView($0)).trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
    }
}
