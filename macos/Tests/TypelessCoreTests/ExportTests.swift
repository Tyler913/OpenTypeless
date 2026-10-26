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
    }

    @Test func emptyExports() {
        #expect(HistoryExport.csv([]) == "date,durationSeconds,status,finalText,rawText,costUSD,words\r\n")
        #expect(HistoryExport.json([]) == "[]\n")
        #expect(HistoryExport.markdown([]) == "")
        #expect(HistoryExport.seconds(.nan) == "0.0")
        #expect(HistoryExport.usd(.infinity) == "0.000000")
    }

    @Test func thousandsOfDictationsExportQuickly() {
        let entry = HistoryExport.Entry(date: Date(timeIntervalSince1970: 1_791_786_725), utcOffsetMinutes: 480, duration: 42,
                                        status: "done", finalText: String(repeating: "明天下午三点开会，记得带上报告。", count: 20),
                                        rawText: String(repeating: "嗯明天下午三点开会 记得带上那个报告", count: 20), cost: 0.0005)
        let entries = Array(repeating: entry, count: 5000)
        let start = Date()
        for format in HistoryExport.Format.allCases { _ = HistoryExport.data(entries, format: format) }
        #expect(Date().timeIntervalSince(start) < 10)
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
            let input = item["input"] as! String
            let terms = item["kind"] as! String == "csv" ? VocabularyTransfer.terms(fromCSV: input) : VocabularyTransfer.terms(fromText: input)
            #expect(terms == item["terms"] as! [String], "\(input)")
        }
        for item in vocabulary["merges"] as! [[String: Any]] {
            #expect(VocabularyTransfer.newTerms(item["imported"] as! [String], existing: item["existing"] as! [String])
                    == item["added"] as! [String], "\(item)")
        }
        for item in vocabulary["decode"] as! [[String: Any]] {
            let data = Data(base64Encoded: item["base64"] as! String)!
            #expect(VocabularyTransfer.decode(data) == item["text"] as? String, "\(item)")
        }
    }

    @Test func exportedVocabularyImportsBack() {
        let terms = [VocabularyTransfer.Term(term: "Typeless", heardAs: ["TypeList"]), VocabularyTransfer.Term(term: "李明")]
        let csv = VocabularyTransfer.decode(VocabularyTransfer.data(terms, csv: true))!
        #expect(VocabularyTransfer.terms(fromCSV: csv) == ["Typeless", "李明"])
        let text = VocabularyTransfer.decode(VocabularyTransfer.data(terms, csv: false))!
        #expect(VocabularyTransfer.terms(fromText: text) == ["Typeless", "李明"])
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
