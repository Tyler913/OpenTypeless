import Foundation
import Testing
@testable import TypelessCore

/// Spoken "new line" / "new paragraph": a command said on its own becomes line breaks; the same words within a sentence stay.
@Suite struct SpokenLayoutTests {
    private struct LayoutCase: Decodable {
        let id: String
        let input: String
        let output: String?
    }

    private struct EdgeCase: Decodable {
        let id: String
        let input: String
        let leading: String
        let body: String
        let trailing: String
    }

    private struct CaseFile: Decodable {
        let cases: [LayoutCase]
        let edges: [EdgeCase]
    }

    /// testdata/spoken-layout-cases.json, shared with the Windows tests so both apps lay a dictation out the same.
    private func caseFile() throws -> CaseFile {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/spoken-layout-cases.json"))
        return try JSONDecoder().decode(CaseFile.self, from: data)
    }

    @Test func sharedCases() throws {
        let cases = try caseFile().cases
        #expect(cases.count > 30)
        for c in cases {
            #expect(SpokenLayout.apply(c.input) == (c.output ?? c.input), "\(c.id)")
        }
    }

    @Test func sharedEdges() throws {
        for c in try caseFile().edges {
            let edges = SpokenLayout.edges(c.input)
            #expect(edges.leading == c.leading, "\(c.id)")
            #expect(edges.body == c.body, "\(c.id)")
            #expect(edges.trailing == c.trailing, "\(c.id)")
        }
    }

    /// Put back together, the edges are the laid-out text again, trimmed only where there were breaks.
    @Test func edgesPutBackTogether() {
        for text in ["\n\nSecond point.\n", "Hello.", "A.\nB.", "\n"] {
            let edges = SpokenLayout.edges(text)
            #expect(edges.leading + edges.body + edges.trailing == text)
        }
    }

    /// Every command of the list is recognised said on its own, and none is recognised inside a longer clause.
    @Test func everyCommandOnItsOwnAndNotWithinASentence() {
        for (key, breaks) in SpokenLayout.commands {
            #expect(SpokenLayout.apply("A. \(key). B.") == "A.\(breaks)B.", "\(key)")
            #expect(SpokenLayout.apply("A \(key) B.") == "A \(key) B.", "\(key)")
        }
    }

    /// Every language the app speaks has both commands.
    @Test func bothCommandsInEveryLanguage() {
        #expect(Set(SpokenLayout.commands.values) == ["\n", "\n\n"])
        #expect(SpokenLayout.commands.count == 33) // as on Windows
    }
}
