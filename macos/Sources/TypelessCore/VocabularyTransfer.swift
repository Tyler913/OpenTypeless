import Foundation

/// The vocabulary in and out of a file. Exported as CSV (each term with what recognition heard instead, when it was
/// learned from a correction, for reference) or as plain text, a term per line; imported from either, or from a list
/// another app exported, and merged into the vocabulary without duplicates. Only terms are imported, never misheard
/// forms: those make clean-up replace words in every dictation, so they come only from the user's own corrections.
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
    static let headers: Set<String> = ["term", "terms", "word", "words", "phrase", "phrases", "vocabulary", "词", "词汇", "术语"]

    /// The files the import takes: a CSV or tab-separated table (its first column), or text.
    public static let importExtensions = ["csv", "tsv", "txt"]

    /// Every term goes into every clean-up request, so an import leaves out any longer than this, in UTF-16 code units
    /// (which both apps count the same way): that's a sentence or a pasted paragraph, not a name.
    public static let maxTermLength = 64

    /// Nor does an import take the vocabulary past this many terms (a few thousand tokens in every request).
    public static let maxTerms = 1000

    /// What importing a file does to the vocabulary.
    public struct ImportPlan: Sendable, Equatable {
        /// The new terms, each once and spelled as it first appears, in the file's order.
        public var added: [String] = []
        /// Terms already in the vocabulary.
        public var known = 0
        /// Terms left out for being longer than `maxTermLength`.
        public var tooLong = 0
        /// New terms left out because the vocabulary would hold more than `maxTerms`.
        public var overLimit = 0

        public init(added: [String] = [], known: Int = 0, tooLong: Int = 0, overLimit: Int = 0) {
            self.added = added
            self.known = known
            self.tooLong = tooLong
            self.overLimit = overLimit
        }
    }

    /// "term,heardAs" and a line per term, misheard forms separated by "; ", CRLF line ends.
    public static func csv(_ terms: [Term]) -> String {
        var out = "term,heardAs\r\n"
        for term in terms {
            out += CSV.spreadsheetField(term.term) + "," + CSV.spreadsheetField(term.heardAs.joined(separator: "; ")) + "\r\n"
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

    /// A file's terms, read the way its extension says: a table's first column, or text.
    public static func terms(in text: String, fileExtension: String) -> [String] {
        ["csv", "tsv"].contains(fileExtension.lowercased()) ? terms(fromCSV: text) : terms(fromText: text)
    }

    /// An imported file's text: UTF-8 with or without a byte-order mark, or UTF-16 with one (Excel's "Unicode text").
    /// Nil for anything else, such as a CSV Excel saved in the system's legacy encoding: guessing that would import
    /// garbled terms.
    public static func decode(_ data: Data) -> String? {
        if data.starts(with: [0xEF, 0xBB, 0xBF]) { return String(data: data.dropFirst(3), encoding: .utf8) }
        // An odd byte at the end is half a character: refused, as Windows does, rather than dropped.
        if data.starts(with: [0xFF, 0xFE]) { return data.count % 2 == 0 ? String(data: data.dropFirst(2), encoding: .utf16LittleEndian) : nil }
        if data.starts(with: [0xFE, 0xFF]) { return data.count % 2 == 0 ? String(data: data.dropFirst(2), encoding: .utf16BigEndian) : nil }
        return String(data: data, encoding: .utf8)
    }

    /// The terms in a CSV: the first column of each row (the others, like the export's misheard forms, aren't terms),
    /// after a header row.
    public static func terms(fromCSV text: String) -> [String] {
        terms(fromTable: CSV.rows(text))
    }

    /// The terms in a text file: a term per line, or separated by commas the way the vocabulary field takes them. A
    /// file whose first line holds a tab is a table (Excel saves "Unicode Text" as a .txt with tabs between the columns),
    /// so its first column is read.
    public static func terms(fromText text: String) -> [String] {
        let firstLine = text.unicodeScalars.prefix(while: { $0 != "\n" && $0 != "\r" })
        return firstLine.contains("\t") ? terms(fromTable: CSV.rows(text, separator: "\t")) : split(text)
    }

    /// What importing `imported` into a vocabulary holding `existing` does. Terms are compared without case
    /// (`lowercased()` here, `ToLowerInvariant()` on Windows: they differ only for a few letters such as Turkish İ,
    /// the same as everywhere else the vocabulary is compared).
    public static func plan(_ imported: [String], existing: [String], limit: Int = maxTerms) -> ImportPlan {
        let known = Set(existing.map { $0.lowercased() })
        var seen = Set<String>()
        var plan = ImportPlan()
        for term in imported {
            let key = term.lowercased()
            guard seen.insert(key).inserted else { continue }
            if known.contains(key) {
                plan.known += 1
            } else if term.utf16.count > maxTermLength {
                plan.tooLong += 1
            } else if existing.count + plan.added.count >= limit {
                plan.overLimit += 1
            } else {
                plan.added.append(term)
            }
        }
        return plan
    }

    /// The vocabulary field's text with `terms` added after what's there, separated by commas.
    public static func merged(_ vocabulary: String, adding terms: [String]) -> String {
        guard !terms.isEmpty else { return vocabulary }
        let current = vocabulary.trimmingCharacters(in: .whitespacesAndNewlines)
        let joined = terms.joined(separator: ", ")
        return current.isEmpty ? joined : current + ", " + joined
    }

    private static func terms(fromTable rows: [[String]]) -> [String] {
        var table = rows
        if let first = table.first?.first, headers.contains(first.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()) {
            table.removeFirst()
        }
        return table.flatMap { split(CSV.unprotected($0.first ?? "")) }
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
