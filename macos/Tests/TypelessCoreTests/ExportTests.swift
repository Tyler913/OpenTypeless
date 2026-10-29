import Foundation
import Testing
@testable import TypelessCore

/// History exports and vocabulary files: both apps write the same bytes and read imports the same way.
@Suite struct ExportTests {
    /// testdata/export-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/export-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    private static func date(_ unix: Any?) -> Date {
        Date(timeIntervalSince1970: (unix as! NSNumber).doubleValue)
    }

    private static func exportEntries(_ items: [[String: Any]]) -> [HistoryExport.Entry] {
        items.map { item in
            HistoryExport.Entry(date: date(item["unix"]), utcOffsetMinutes: item["offsetMinutes"] as! Int,
                                duration: (item["duration"] as! NSNumber).doubleValue, status: item["status"] as! String,
                                finalText: item["finalText"] as! String, rawText: item["rawText"] as! String,
                                cost: (item["cost"] as? NSNumber)?.doubleValue)
        }
    }

    @Test func historyFormatsMatchSharedCases() throws {
        let history = try Self.cases()["history"] as! [String: Any]
        let entries = Self.exportEntries(history["entries"] as! [[String: Any]])
        #expect(HistoryExport.csv(entries) == history["csv"] as! String)
        #expect(HistoryExport.json(entries) == history["json"] as! String)
        #expect(HistoryExport.markdown(entries) == history["markdown"] as! String)
        // The CSV file starts with a byte-order mark, so Excel reads it as UTF-8; the others don't.
        #expect(HistoryExport.data(entries, format: .csv) == Data([0xEF, 0xBB, 0xBF]) + Data((history["csv"] as! String).utf8))
        #expect(HistoryExport.data(entries, format: .json) == Data((history["json"] as! String).utf8))
        #expect(HistoryExport.data(entries, format: .markdown) == Data((history["markdown"] as! String).utf8))
    }

    @Test func valuesMatchSharedCases() throws {
        let values = try Self.cases()["values"] as! [String: Any]
        for item in values["timestamps"] as! [[String: Any]] {
            #expect(HistoryExport.timestamp(Self.date(item["unix"]), utcOffsetMinutes: item["offsetMinutes"] as! Int)
                    == item["expected"] as! String, "\(item)")
        }
        for item in values["seconds"] as! [[String: Any]] {
            #expect(HistoryExport.seconds((item["value"] as! NSNumber).doubleValue) == item["expected"] as! String, "\(item)")
        }
        for item in values["usd"] as! [[String: Any]] {
            #expect(HistoryExport.usd((item["value"] as! NSNumber).doubleValue) == item["expected"] as! String, "\(item)")
        }
        for item in values["offsets"] as! [[String: Any]] {
            let zone = try #require(TimeZone(identifier: item["zone"] as! String))
            #expect(HistoryExport.utcOffsetMinutes(at: Self.date(item["unix"]), in: zone) == item["expected"] as! Int, "\(item)")
        }
        for item in values["markdownLines"] as! [[String: Any]] {
            #expect(HistoryExport.markdownLine(item["line"] as! String) == item["expected"] as! String, "\(item)")
        }
        for item in values["spreadsheetFields"] as! [[String: Any]] {
            let text = item["text"] as! String
            #expect(CSV.spreadsheetField(text) == item["expected"] as! String, "\(item)")
            // And back: what an import reads is what was written.
            #expect(CSV.rows(CSV.spreadsheetField(text) + "\r\n", separator: ",").first.map { CSV.unprotected($0[0]) } ?? "" == text, "\(item)")
        }
    }

    /// A damaged record (a date or a number no dictation has) is written within bounds rather than crashing the export.
    @Test func damagedValuesDontCrash() {
        #expect(HistoryExport.timestamp(Date(timeIntervalSince1970: 1e300), utcOffsetMinutes: 480) == "9999-12-31T23:59:59+08:00")
        #expect(HistoryExport.timestamp(Date(timeIntervalSince1970: -1e300), utcOffsetMinutes: 0) == "0001-01-01T00:00:00+00:00")
        #expect(HistoryExport.timestamp(Date(timeIntervalSince1970: .nan), utcOffsetMinutes: 0) == "0001-01-01T00:00:00+00:00")
        #expect(HistoryExport.timestamp(Date(timeIntervalSince1970: 0), utcOffsetMinutes: 100_000) == "1970-01-01T14:00:00+14:00")
        #expect(HistoryExport.seconds(.greatestFiniteMagnitude) == "1000000000000.0")
        #expect(HistoryExport.usd(-.infinity) == "0.000000")
    }

    @Test func emptyExports() {
        #expect(HistoryExport.csv([]) == "date,durationSeconds,status,finalText,rawText,costUSD,words\r\n")
        #expect(HistoryExport.json([]) == "[]\n")
        #expect(HistoryExport.markdown([]) == "")
        #expect(HistoryExport.seconds(.nan) == "0.0")
        #expect(HistoryExport.usd(.infinity) == "0.000000")
    }

    @Test func vocabularyFilesMatchSharedCases() throws {
        let vocabulary = try Self.cases()["vocabulary"] as! [String: Any]
        let terms = (vocabulary["terms"] as! [[String: Any]]).map {
            VocabularyTransfer.Term(term: $0["term"] as! String, heardAs: $0["heardAs"] as! [String])
        }
        #expect(VocabularyTransfer.csv(terms) == vocabulary["csv"] as! String)
        #expect(VocabularyTransfer.text(terms) == vocabulary["text"] as! String)
        #expect(VocabularyTransfer.data(terms, csv: true) == Data([0xEF, 0xBB, 0xBF]) + Data((vocabulary["csv"] as! String).utf8))
        #expect(VocabularyTransfer.data(terms, csv: false) == Data((vocabulary["text"] as! String).utf8))
    }

    @Test func vocabularyImportsMatchSharedCases() throws {
        let vocabulary = try Self.cases()["vocabulary"] as! [String: Any]
        for item in vocabulary["imports"] as! [[String: Any]] {
            // A file's bytes (Excel's UTF-16 "Unicode Text", say), or its text.
            let input = try (item["base64"] as? String).map { try #require(VocabularyTransfer.decode(Data(base64Encoded: $0)!)) }
                ?? item["input"] as! String
            #expect(VocabularyTransfer.terms(in: input, fileExtension: item["kind"] as! String) == item["terms"] as! [String], "\(input)")
        }
        #expect(VocabularyTransfer.maxTermLength == vocabulary["maxTermLength"] as! Int)
        #expect(VocabularyTransfer.maxTerms == vocabulary["maxTerms"] as! Int)
        for item in vocabulary["plans"] as! [[String: Any]] {
            let imported = item["imported"] as! [String], existing = item["existing"] as! [String]
            let plan = VocabularyTransfer.plan(imported, existing: existing, limit: item["limit"] as? Int ?? VocabularyTransfer.maxTerms)
            #expect(plan == VocabularyTransfer.ImportPlan(added: item["added"] as! [String], known: item["known"] as! Int,
                                                          tooLong: item["tooLong"] as! Int, overLimit: item["overLimit"] as! Int), "\(item)")
        }
        for item in vocabulary["merged"] as! [[String: Any]] {
            #expect(VocabularyTransfer.merged(item["vocabulary"] as! String, adding: item["adding"] as! [String])
                    == item["expected"] as! String, "\(item)")
        }
        for item in vocabulary["decode"] as! [[String: Any]] {
            let data = Data(base64Encoded: item["base64"] as! String)!
            #expect(VocabularyTransfer.decode(data) == item["text"] as? String, "\(item)")
        }
    }

    @Test func exportedVocabularyImportsBack() {
        let terms = [VocabularyTransfer.Term(term: "Typeless", heardAs: ["TypeList"]), VocabularyTransfer.Term(term: "李明"),
                     VocabularyTransfer.Term(term: "-ish", heardAs: ["=ish"])]
        let csv = VocabularyTransfer.decode(VocabularyTransfer.data(terms, csv: true))!
        #expect(VocabularyTransfer.terms(in: csv, fileExtension: "csv") == ["Typeless", "李明", "-ish"])
        let text = VocabularyTransfer.decode(VocabularyTransfer.data(terms, csv: false))!
        #expect(VocabularyTransfer.terms(in: text, fileExtension: "txt") == ["Typeless", "李明", "-ish"])
    }

    /// However big the file, an import never takes the vocabulary past its limit, so clean-up requests stay small.
    @Test func importStopsAtTheLimit() {
        let existing = (1...990).map { "term\($0)" }
        let imported = (1...5000).map { "new\($0)" }
        let plan = VocabularyTransfer.plan(imported, existing: existing)
        #expect(plan.added == (1...10).map { "new\($0)" })
        #expect(plan.overLimit == 4990)
        #expect(VocabularyTransfer.plan(["one more"], existing: (1...1200).map { "t\($0)" }).overLimit == 1)
    }

    @Test func csvRowsAreParsedLikeRFC4180() {
        #expect(CSV.rows("a,\"b,c\",\"d\"\"e\"\r\nf,\"g\nh\"\n") == [["a", "b,c", "d\"e"], ["f", "g\nh"]])
        #expect(CSV.rows("a;b;c\n1;2;3") == [["a", "b", "c"], ["1", "2", "3"]])
        #expect(CSV.rows("a\tb\n1\t2\r3") == [["a", "b"], ["1", "2"], ["3"]])
        #expect(CSV.rows("\"open, never closed\nstill open") == [["open, never closed\nstill open"]])
        #expect(CSV.rows("") == [])
        #expect(CSV.field("plain") == "plain")
        #expect(CSV.field("a,b") == "\"a,b\"")
        #expect(CSV.field("say \"hi\"") == "\"say \"\"hi\"\"\"")
        #expect(CSV.field("two\nlines") == "\"two\nlines\"")
    }
}
