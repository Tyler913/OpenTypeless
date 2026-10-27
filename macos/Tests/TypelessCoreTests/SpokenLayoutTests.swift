import Foundation
import Testing
@testable import TypelessCore

/// Spoken "new line" / "new paragraph": a command said on its own becomes line breaks; the same words within a sentence stay.
@Suite struct SpokenLayoutTests {
    private struct LayoutCase: Decodable {
        let id: String
        let input: String
        let output: String?
        let limitation: String?
    }

    private struct EdgeCase: Decodable {
        let id: String
        let input: String
        let leading: String
        let body: String
        let trailing: String
    }

    private struct LayoutOfCase: Decodable {
        let id: String
        let input: String
        let enabled: Bool
        let leading: String
        let body: String
        let trailing: String
        let dictatedBreaks: Bool
    }

    private struct CaseFile: Decodable {
        let cases: [LayoutCase]
        let edges: [EdgeCase]
        let layouts: [LayoutOfCase]
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

    /// What a dictation uses: off, or without a command, the transcript exactly as it came (its own line breaks included).
    @Test func sharedLayouts() throws {
        for c in try caseFile().layouts {
            let expected = SpokenLayout.LaidOut(leading: c.leading, body: c.body, trailing: c.trailing, dictatedBreaks: c.dictatedBreaks)
            #expect(SpokenLayout.layout(c.input, enabled: c.enabled) == expected, "\(c.id)")
        }
        let laidOut = SpokenLayout.layout("New line. Hello. New paragraph.", enabled: true)
        #expect(laidOut.text == "\nHello.\n\n")
        #expect(laidOut.around("Hello!") == "\nHello!\n\n")
    }

    /// The known false positives (a command word said as its own clause in a list, or as an answer) stay pinned.
    @Test func limitationsArePinned() throws {
        #expect(try caseFile().cases.filter { $0.limitation != nil }.count >= 7)
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

    /// The clean-up's prompt is the one ordinary dictations always had unless the transcript's breaks were dictated, and
    /// then only for a transcript (or part) that has any.
    @Test func ordinaryDictationsKeepTheirPrompt() {
        let plain = Prompts.polishSystemPrompt(vocabulary: [], extraInstructions: "")
        #expect(plain == Prompts.basePolishPrompt)
        #expect(!plain.contains("Line breaks"))
        #expect(Prompts.polishSystemPrompt(vocabulary: [], extraInstructions: "", dictatedLineBreaks: true)
                == Prompts.basePolishPrompt + "\n\n" + Prompts.lineBreaksSection)
        let dictated = PolishOptions(model: "m", dictatedLineBreaks: true)
        #expect(APIClient.polishMessages(transcript: "Hi,\nthere.", options: dictated)[0].contains("## Line breaks"))
        #expect(!APIClient.polishMessages(transcript: "Hi there.", options: dictated)[0].contains("## Line breaks"))
        // Speech recognition's own breaks: nothing said about them, as before.
        #expect(!APIClient.polishMessages(transcript: "Hi,\nthere.", options: PolishOptions(model: "m"))[0].contains("## Line breaks"))
    }

    /// The section's example is a clean-up, and keeps its lines.
    @Test func theLineBreaksExampleIsACleanUp() {
        let examples = PolishGuardTests.examples(in: Prompts.lineBreaksSection)
        #expect(examples.count == 1)
        for (input, output) in examples {
            #expect(input.contains("\n\n") && output.contains("\n\n"))
            #expect(Prompts.fallback(input: input, output: output, truncated: false) == nil)
        }
    }

    /// Every language the app speaks has both commands.
    @Test func bothCommandsInEveryLanguage() {
        #expect(Set(SpokenLayout.commands.values) == ["\n", "\n\n"])
        #expect(SpokenLayout.commands.count == 33) // as on Windows
    }
}
