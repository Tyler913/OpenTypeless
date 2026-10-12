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

    /// testdata/snippet-cases.json, shared with the Windows tests so both apps insert the same text.
    @Test func sharedCases() throws {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/snippet-cases.json"))
        let file = try JSONDecoder().decode(CaseFile.self, from: data)
        #expect(file.cases.count >= 20)
        for c in file.cases {
            let snippets = (c.snippets ?? file.snippets).map { Snippet(trigger: $0.trigger, expansion: $0.expansion) }
            #expect(SnippetExpander.expand(c.text, with: snippets) == c.expected, "\(c.id)")
        }
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
    }

    @Test func triggersGoAheadOfTheVocabulary() {
        let snippets = [Snippet(trigger: " PR template ", expansion: "…"), Snippet(trigger: "Claude", expansion: "…"),
                        Snippet(trigger: "  ", expansion: "…")]
        #expect(SnippetExpander.vocabulary(snippets, before: ["claude", "GitHub", "PR Template"])
            == ["PR template", "Claude", "GitHub"])
        // Without snippets the vocabulary is exactly what it was.
        #expect(SnippetExpander.vocabulary([], before: ["Claude", "claude", " x "]) == ["Claude", "claude", " x "])
    }

    @Test func longTextStaysFast() {
        let snippets = (0..<50).map { Snippet(trigger: "trigger number \($0)", expansion: "expansion \($0)") }
        let text = String(repeating: "我们今天讨论了缓存和数据库的性能，then trigger number 7 again. ", count: 400)
        let clock = ContinuousClock()
        let elapsed = clock.measure { _ = SnippetExpander.expand(text, with: snippets) }
        #expect(elapsed < .seconds(1))
        #expect(SnippetExpander.expand(text, with: snippets).components(separatedBy: "expansion 7").count == 401)
    }
}
