import Foundation

/// Comma-separated values as RFC 4180 has them: a field holding a comma, a quote or a line break is quoted, with its
/// quotes doubled.
public enum CSV {
    /// One field, quoted only when it has to be.
    public static func field(_ text: String) -> String {
        guard text.unicodeScalars.contains(where: { $0 == "," || $0 == "\"" || $0 == "\r" || $0 == "\n" }) else { return text }
        return "\"" + text.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    /// A field of a file a spreadsheet will open. One starting with = + - @, a tab or a carriage return gets an
    /// apostrophe in front, so Excel or Numbers shows it as the text it is rather than running it as a formula (OWASP's
    /// advice against CSV injection); `unprotected` takes the apostrophe off again when such a file is imported. Text
    /// that already starts with apostrophes before one of those gets one more, so it comes back as it was.
    public static func spreadsheetField(_ text: String) -> String {
        field(needsApostrophe(text) ? "'" + text : text)
    }

    /// A field as it was before `spreadsheetField` protected it.
    public static func unprotected(_ field: String) -> String {
        guard field.unicodeScalars.first == "'", needsApostrophe(field) else { return field }
        return String(field.unicodeScalars.dropFirst())
    }

    private static func needsApostrophe(_ text: String) -> Bool {
        guard let first = text.unicodeScalars.first(where: { $0 != "'" }) else { return false }
        return first == "=" || first == "+" || first == "-" || first == "@" || first == "\t" || first == "\r"
    }

    /// The rows of a file. Lenient about what spreadsheets write: CRLF, LF or CR line ends, a stray quote inside a field
    /// kept as it is, an unclosed quote running to the end, and a semicolon or tab as the separator when the first line
    /// uses more of those than commas (Excel in German, French or Russian saves "CSV" with semicolons), unless a
    /// `separator` is given.
    public static func rows(_ text: String, separator given: Unicode.Scalar? = nil) -> [[String]] {
        let scalars = Array(text.unicodeScalars)
        let separator = given ?? mostUsedSeparator(scalars)
        var rows: [[String]] = []
        var row: [String] = []
        var field = String.UnicodeScalarView()
        var quoted = false
        var index = 0
        while index < scalars.count {
            let scalar = scalars[index]
            index += 1
            if quoted {
                if scalar != "\"" {
                    field.append(scalar)
                } else if index < scalars.count, scalars[index] == "\"" {
                    field.append(scalar)
                    index += 1
                } else {
                    quoted = false
                }
            } else if scalar == "\"" && field.isEmpty {
                quoted = true
            } else if scalar == separator {
                row.append(String(field))
                field = String.UnicodeScalarView()
            } else if scalar == "\r" || scalar == "\n" {
                if scalar == "\r", index < scalars.count, scalars[index] == "\n" { index += 1 }
                row.append(String(field))
                rows.append(row)
                row = []
                field = String.UnicodeScalarView()
            } else {
                field.append(scalar)
            }
        }
        if !field.isEmpty || !row.isEmpty {
            row.append(String(field))
            rows.append(row)
        }
        return rows
    }

    /// The separator the first line uses most, outside quotes: comma, semicolon or tab (comma when it has none).
    private static func mostUsedSeparator(_ scalars: [Unicode.Scalar]) -> Unicode.Scalar {
        var counts: [Unicode.Scalar: Int] = [",": 0, ";": 0, "\t": 0]
        var quoted = false
        for scalar in scalars {
            if scalar == "\"" { quoted.toggle() }
            if quoted { continue }
            if scalar == "\r" || scalar == "\n" { break }
            if counts[scalar] != nil { counts[scalar]! += 1 }
        }
        let semicolons = counts[";"]!, tabs = counts["\t"]!, commas = counts[","]!
        if semicolons > commas && semicolons >= tabs { return ";" }
        if tabs > commas && tabs > semicolons { return "\t" }
        return ","
    }
}
