import Foundation

/// History as a file the user keeps: CSV for a spreadsheet, JSON for a script, Markdown to read. Every number and date
/// is written by hand rather than by a formatter, so both apps write exactly the same bytes for the same dictations
/// whatever the system's language or region (testdata/export-cases.json holds the expected files).
public enum HistoryExport {
    public enum Format: String, CaseIterable, Sendable {
        case csv, json, markdown

        public var fileExtension: String {
            switch self {
            case .csv: return "csv"
            case .json: return "json"
            case .markdown: return "md"
            }
        }
    }

    /// One dictation as it's exported.
    public struct Entry: Sendable, Equatable {
        public var date: Date
        /// The local time's offset from UTC on that date, in minutes: a dictation from before a daylight-saving change
        /// keeps the offset it was made in.
        public var utcOffsetMinutes: Int
        public var duration: Double
        /// As stored in session.json: "done", "polishFailed", "failed", "cancelled", "recording" or "processing".
        public var status: String
        public var finalText: String
        public var rawText: String
        /// USD; nil when nothing could be priced.
        public var cost: Double?

        public init(date: Date, utcOffsetMinutes: Int, duration: Double, status: String, finalText: String, rawText: String,
                    cost: Double?) {
            self.date = date
            self.utcOffsetMinutes = utcOffsetMinutes
            self.duration = duration
            self.status = status
            self.finalText = finalText
            self.rawText = rawText
            self.cost = cost
        }
    }

    /// The CSV's columns and the JSON's keys, in order.
    public static let fields = ["date", "durationSeconds", "status", "finalText", "rawText", "costUSD", "words"]

    /// Lets Excel open a CSV as UTF-8: without it, Chinese comes out garbled.
    public static let byteOrderMark = Data([0xEF, 0xBB, 0xBF])

    /// The file's bytes: UTF-8, the CSV with a byte-order mark.
    public static func data(_ entries: [Entry], format: Format) -> Data {
        switch format {
        case .csv: return byteOrderMark + Data(csv(entries).utf8)
        case .json: return Data(json(entries).utf8)
        case .markdown: return Data(markdown(entries).utf8)
        }
    }

    /// RFC 4180: a header, then a line per dictation, CRLF line ends, fields quoted when they hold a comma, a quote or a
    /// line break.
    public static func csv(_ entries: [Entry]) -> String {
        var out = fields.joined(separator: ",") + "\r\n"
        for entry in entries {
            let row = [timestamp(entry.date, utcOffsetMinutes: entry.utcOffsetMinutes), seconds(entry.duration), entry.status,
                       entry.finalText, entry.rawText, entry.cost.map(usd) ?? "", String(WordCount.count(entry.finalText))]
            out += row.map(CSV.field).joined(separator: ",") + "\r\n"
        }
        return out
    }

    /// An array of objects with the same fields as the CSV, in the same order, two-space indented.
    public static func json(_ entries: [Entry]) -> String {
        guard !entries.isEmpty else { return "[]\n" }
        let objects = entries.map { entry in
            let values = [jsonString(timestamp(entry.date, utcOffsetMinutes: entry.utcOffsetMinutes)), seconds(entry.duration),
                          jsonString(entry.status), jsonString(entry.finalText), jsonString(entry.rawText),
                          entry.cost.map(usd) ?? "null", String(WordCount.count(entry.finalText))]
            let members = zip(fields, values).map { "    \"\($0)\": \($1)" }
            return "  {\n" + members.joined(separator: ",\n") + "\n  }"
        }
        return "[\n" + objects.joined(separator: ",\n") + "\n]\n"
    }

    /// A heading for each day and each dictation's time under it, then its text, a paragraph per line. Dictations
    /// without text are left out: there's nothing to read.
    public static func markdown(_ entries: [Entry]) -> String {
        var blocks: [String] = []
        var currentDay: String?
        for entry in entries {
            let paragraphs = entry.finalText
                .replacingOccurrences(of: "\r\n", with: "\n")
                .replacingOccurrences(of: "\r", with: "\n")
                .components(separatedBy: "\n")
                .map(trimmingTrailingSpaces)
                .filter { !$0.isEmpty }
            guard !paragraphs.isEmpty else { continue }
            let stamp = timestamp(entry.date, utcOffsetMinutes: entry.utcOffsetMinutes)
            // "2026-10-12T14:32:05+08:00": the local day, then the hour and minute.
            let day = String(stamp.prefix(10))
            if day != currentDay {
                currentDay = day
                blocks.append("# " + day)
            }
            blocks.append("## " + String(stamp.dropFirst(11).prefix(5)))
            blocks.append(contentsOf: paragraphs)
        }
        return blocks.isEmpty ? "" : blocks.joined(separator: "\n\n") + "\n"
    }

    // MARK: Values

    /// ISO 8601 in the local time it was made, with its offset: "2026-10-12T14:32:05+08:00" (whole seconds, cut down).
    static func timestamp(_ date: Date, utcOffsetMinutes: Int) -> String {
        // Every time zone is within 14 hours of UTC (and the Windows app's dates can't go further).
        let utcOffsetMinutes = min(max(utcOffsetMinutes, -14 * 60), 14 * 60)
        let local = Int64(floor(date.timeIntervalSince1970)) + Int64(utcOffsetMinutes) * 60
        let days = local >= 0 ? local / 86_400 : (local - 86_399) / 86_400
        let second = local - days * 86_400
        // Days since 1970-01-01 to a Gregorian date (Howard Hinnant's civil_from_days).
        let z = days + 719_468
        let era = (z >= 0 ? z : z - 146_096) / 146_097
        let dayOfEra = z - era * 146_097
        let yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36_524 - dayOfEra / 146_096) / 365
        let dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100)
        let mp = (5 * dayOfYear + 2) / 153
        let day = dayOfYear - (153 * mp + 2) / 5 + 1
        let month = mp < 10 ? mp + 3 : mp - 9
        let year = yearOfEra + era * 400 + (month <= 2 ? 1 : 0)
        let offset = abs(utcOffsetMinutes)
        return pad(year, 4) + "-" + pad(month, 2) + "-" + pad(day, 2)
            + "T" + pad(second / 3600, 2) + ":" + pad(second / 60 % 60, 2) + ":" + pad(second % 60, 2)
            + (utcOffsetMinutes < 0 ? "-" : "+") + pad(Int64(offset / 60), 2) + ":" + pad(Int64(offset % 60), 2)
    }

    /// Seconds to a tenth, halves rounded up: "42.3".
    static func seconds(_ duration: Double) -> String {
        let tenths = Int64(((duration.isFinite ? max(0, duration) : 0) * 10).rounded())
        return "\(tenths / 10).\(tenths % 10)"
    }

    /// USD to a millionth, halves rounded up: "0.000508", "1.200000".
    static func usd(_ cost: Double) -> String {
        let millionths = Int64(((cost.isFinite ? max(0, cost) : 0) * 1_000_000).rounded())
        return "\(millionths / 1_000_000)." + pad(millionths % 1_000_000, 6)
    }

    /// A JSON string literal; only what JSON requires is escaped, so Chinese stays readable.
    static func jsonString(_ text: String) -> String {
        guard text.unicodeScalars.contains(where: { $0 == "\"" || $0 == "\\" || $0.value < 0x20 }) else { return "\"" + text + "\"" }
        var out = "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case "\u{08}": out += "\\b"
            case "\u{0C}": out += "\\f"
            case _ where scalar.value < 0x20: out += "\\u00" + (scalar.value < 0x10 ? "0" : "") + String(scalar.value, radix: 16)
            default: out.unicodeScalars.append(scalar)
            }
        }
        return out + "\""
    }

    private static func trimmingTrailingSpaces(_ line: String) -> String {
        var scalars = Substring(line).unicodeScalars
        while let last = scalars.last, last == " " || last == "\t" { scalars.removeLast() }
        return String(scalars)
    }

    private static func pad(_ value: Int64, _ width: Int) -> String {
        let digits = String(value)
        return digits.count >= width ? digits : String(repeating: "0", count: width - digits.count) + digits
    }
}
