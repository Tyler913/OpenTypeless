import Foundation
import Testing
@testable import TypelessCore

/// Fitting dictated text to the text around the cursor.
@Suite struct InsertionJoinerTests {
    /// testdata/insertion-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/insertion-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    @Test func fitsTheSharedCases() throws {
        let cases = try Self.cases()
        let common = Set(cases["commonWords"] as! [String])
        let vocabulary = cases["vocabulary"] as! [String]
        for item in cases["cases"] as! [[String: Any]] {
            let result = InsertionJoiner.fit(item["text"] as! String, before: item["before"] as? String,
                                             after: item["after"] as? String, vocabulary: vocabulary,
                                             isCommonWord: { common.contains($0.lowercased()) })
            let id = item["id"] as! String
            #expect(result.text == item["expect"] as! String, "\(id)")
            #expect(result.changes == item["changes"] as! [String], "\(id)")
        }
    }

    @Test func changesNothingWhenNothingIsKnown() {
        let result = InsertionJoiner.fit("Next sentence.", before: nil, after: "and more", isCommonWord: { _ in true })
        #expect(result == InsertionJoiner.Result(text: "Next sentence.", changes: []))
    }

    @Test func dictionaryDecidesLowercasing() {
        // Without the dictionary vouching for the word, a capital mid-sentence stays: it may be a name.
        #expect(InsertionJoiner.fit("Then we left.", before: "and", after: "", isCommonWord: { _ in false }).text == " Then we left.")
        #expect(InsertionJoiner.fit("Then we left.", before: "and", after: "", isCommonWord: { _ in true }).text == " then we left.")
    }

    @Test func finalStopIsOnlyDroppedAfterAWord() {
        #expect(InsertionJoiner.droppingFinalStop("we left.") == "we left")
        #expect(InsertionJoiner.droppingFinalStop("我们走了。") == "我们走了")
        #expect(InsertionJoiner.droppingFinalStop("wait...") == nil)
        #expect(InsertionJoiner.droppingFinalStop("in the U.S.") == nil)
        #expect(InsertionJoiner.droppingFinalStop("signed J.") == nil)
        #expect(InsertionJoiner.droppingFinalStop("(see above).") == nil)
        #expect(InsertionJoiner.droppingFinalStop(".") == nil)
    }
}
