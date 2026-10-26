import Foundation

/// Comma-separated values as RFC 4180 has them: a field holding a comma, a quote or a line break is quoted, with its
/// quotes doubled.
public enum CSV {
    /// One field, quoted only when it has to be.
    public static func field(_ text: String) -> String {
        guard text.unicodeScalars.contains(where: { $0 == "," || $0 == "\"" || $0 == "\r" || $0 == "\n" }) else { return text }
        return "\"" + text.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    /// The rows of a file. Lenient about what spreadsheets write: CRLF, LF or CR line ends, a stray quote inside a field
    /// kept as it is, an unclosed quote running to the end, and a semicolon or tab as the separator when the first line
    /// uses more of those than commas (Excel in German, French or Russian saves "CSV" with semicolons).
    public static func rows(_ text: String) -> [[String]] {
        let scalars = Array(text.unicodeScalars)
        let separator = mostUsedSeparator(scalars)
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
