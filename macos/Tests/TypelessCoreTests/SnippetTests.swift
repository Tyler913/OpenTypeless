import Foundation
import Testing
@testable import TypelessCore

@Suite struct SnippetTests {
    private struct Entry: Decodable {
        let trigger: String
        let expansion: String
    }

    private struct SnippetCase: Decodable {
        let id: String
        let snippets: [Entry]?
        let text: String
        let expected: String
    }

    private struct CaseFile: Decodable {
        let snippets: [Entry]
        let cases: [SnippetCase]
    }

    private static func fixture() throws -> Data {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        return try Data(contentsOf: root.appendingPathComponent("testdata/snippet-cases.json"))
    }

    /// testdata/snippet-cases.json, shared with the Windows tests so both apps insert the same text. Compared scalar by
    /// scalar, as Windows compares strings, not by Swift's canonical equivalence.
    @Test func sharedCases() throws {
        let file = try JSONDecoder().decode(CaseFile.self, from: Self.fixture())
        #expect(file.cases.count >= 30)
        for c in file.cases {
            let snippets = (c.snippets ?? file.snippets).map { Snippet(trigger: $0.trigger, expansion: $0.expansion) }
            let expanded = SnippetExpander.expand(c.text, with: snippets)
            #expect(Array(expanded.unicodeScalars) == Array(c.expected.unicodeScalars), "\(c.id): \(expanded)")
        }
    }

    /// The fixture's saved list: unreadable entries are left out, missing, blank and repeated IDs replaced.
    @Test func storedListIsReadEntryByEntry() throws {
        let root = try #require(try JSONSerialization.jsonObject(with: Self.fixture()) as? [String: Any])
        let stored = try #require(root["stored"] as? [String: Any])
        let list = Snippet.list(from: try JSONSerialization.data(withJSONObject: try #require(stored["json"])))
        #expect(list.snippets.map(\.trigger) == stored["triggers"] as? [String])
        #expect(list.repaired == stored["repaired"] as? Bool)
        #expect(list.snippets.first?.id == "a1")
        #expect(Set(list.snippets.map(\.id)).count == list.snippets.count)
        #expect(list.snippets.allSatisfy { !$0.id.trimmingCharacters(in: .whitespaces).isEmpty })
        let clean = Snippet.list(from: try JSONSerialization.data(withJSONObject: try #require(stored["clean"])))
        #expect(clean.snippets.map(\.id) == ["a1"] && !clean.repaired)
        #expect(Snippet.list(from: Data("{}".utf8)).snippets.isEmpty)
    }

    @Test func nothingToExpand() {
        let snippets = [Snippet(trigger: "my email", expansion: "tyler@example.com")]
        #expect(SnippetExpander.expand("", with: snippets) == "")
        #expect(SnippetExpander.expand("my email", with: []) == "my email")
        // Nothing to find in a trigger of spaces and punctuation.
        #expect(SnippetExpander.expand("。", with: [Snippet(trigger: " 。", expansion: "x")]) == "。")
    }

    /// Expansion is a pure function of the text and the snippets, so a retry inserts what the first run did.
    @Test func sameTextSameResult() {
        let snippets = [Snippet(trigger: "我的邮箱", expansion: "tyler@example.com"), Snippet(trigger: "my email", expansion: "x")]
        let once = SnippetExpander.expand("发到我的邮箱，还有 my email。", with: snippets)
        #expect(once == "发到 tyler@example.com，还有 x。")
        #expect(SnippetExpander.expand("发到我的邮箱，还有 my email。", with: snippets) == once)
    }

    @Test func keysTellTriggersApart() {
        #expect(SnippetExpander.key("My Email.") == SnippetExpander.key("my email"))
        #expect(SnippetExpander.key("ＰＲ　Template") == SnippetExpander.key("pr template"))
        #expect(SnippetExpander.key("我的 GitHub") == SnippetExpander.key("我的GitHub"))
        #expect(SnippetExpander.key("C#") != SnippetExpander.key("C"))
        #expect(SnippetExpander.key("  ？ ").isEmpty)
        // Composed and decomposed are the same trigger, scalar for scalar (as Windows compares them).
        #expect(Array(SnippetExpander.key("caf\u{E9}").unicodeScalars) == Array(SnippetExpander.key("cafe\u{301}").unicodeScalars))
        #expect(Array(SnippetExpander.key("\u{D55C}").unicodeScalars) == Array(SnippetExpander.key("\u{1112}\u{1161}\u{11AB}").unicodeScalars))
    }

    @Test func oneClassifierForEveryBlock() {
        #expect(SnippetExpander.isHanOrKana(0x20BB7))   // 𠮷
        #expect(SnippetExpander.isHanOrKana(0x30000))   // Extension G
        #expect(SnippetExpander.isHangul(0x1100) && SnippetExpander.isHangul(0xD7B0))
        #expect(!SnippetExpander.isLatinWord(0x20BB7) && !SnippetExpander.isLatinWord(0x1161))
        #expect(SnippetExpander.isLatinWord(0x65) && SnippetExpander.isLatinWord(0xE9) && SnippetExpander.isLatinWord(0x33))
    }

    @Test func triggersGoAheadOfTheVocabulary() {
        let snippets = [Snippet(trigger: " PR template ", expansion: "…"), Snippet(trigger: "Claude", expansion: "…"),
                        Snippet(trigger: "  ", expansion: "…")]
        #expect(SnippetExpander.vocabulary(snippets, before: ["claude", "GitHub", "PR Template"])
            == ["PR template", "Claude", "GitHub"])
        // Without snippets the vocabulary is exactly what it was.
        #expect(SnippetExpander.vocabulary([], before: ["Claude", "claude", " x "]) == ["Claude", "claude", " x "])
    }

    /// How long this takes is a performance budget (PerformanceTests.snippetsInALongDictation).
    @Test func longTextExpandsEveryTrigger() {
        let snippets = (0..<50).map { Snippet(trigger: "trigger number \($0)", expansion: "expansion \($0)") }
        let text = String(repeating: "我们今天讨论了缓存和数据库的性能，then trigger number 7 again. ", count: 400)
        #expect(SnippetExpander.expand(text, with: snippets).components(separatedBy: "expansion 7").count == 401)
    }
}
