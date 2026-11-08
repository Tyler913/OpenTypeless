import CryptoKit
import Foundation
import Testing
@testable import TypelessCore

/// Learning the speaker's writing style: which texts it learns from, the sample sent, the guard on the answer, how the
/// note joins the clean-up's preferences, and when it runs on its own.
@Suite struct StyleLearningTests {
    /// testdata/style-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/style-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    private static func edits(_ value: Any?) -> [StyleLearning.Edit] {
        (value as? [[String: String]] ?? []).map {
            // Kept as the file holds them, unusable ones included: the sample has to skip those itself.
            try! JSONDecoder().decode(StyleLearning.Edit.self, from: JSONSerialization.data(withJSONObject: $0))
        }
    }

    /// The Windows app sends the same prompt: the fixture holds its hash.
    @Test func promptIsTheSameOnBothPlatforms() throws {
        let hash = SHA256.hash(data: Data(StyleLearning.systemPrompt.utf8)).map { String(format: "%02x", $0) }.joined()
        #expect(hash == (try Self.cases())["stylePromptSHA256"] as? String)
    }

    @Test func guardFollowsSharedCases() throws {
        for item in try Self.cases()["guard"] as! [[String: Any]] {
            let id = item["id"] as! String
            let verdict = StyleLearning.check(item["answer"] as! String)
            if let note = item["note"] as? String {
                #expect(verdict == .note(note), "\(id)")
            } else {
                #expect(verdict == .rejected(StyleLearning.Rejection(rawValue: item["reason"] as! String)!), "\(id)")
            }
        }
    }

    @Test func eligibilityFollowsSharedCases() throws {
        for item in try Self.cases()["eligibility"] as! [[String: Any]] {
            let eligible = StyleLearning.isEligible(status: item["status"] as! String, mode: item["mode"] as? String,
                                                    isFile: item["isFile"] as! Bool, text: item["text"] as! String)
            #expect(eligible == item["expect"] as! Bool, "\(item["id"]!)")
        }
    }

    @Test func editsFollowSharedCases() throws {
        for item in try Self.cases()["edits"] as! [[String: Any]] {
            let edit = StyleLearning.Edit(before: item["before"] as! String, after: item["after"] as! String)
            let expected = item["expect"] as? [String: String]
            #expect(edit?.before == expected?["before"], "\(item["id"]!)")
            #expect(edit?.after == expected?["after"], "\(item["id"]!)")
        }
    }

    @Test func samplesFollowSharedCases() throws {
        for item in try Self.cases()["samples"] as! [[String: Any]] {
            let sample = StyleLearning.sample(texts: item["texts"] as! [String], edits: Self.edits(item["edits"]))
            #expect(sample == item["expect"] as? String, "\(item["id"]!)")
        }
    }

    @Test func compositionFollowsSharedCases() throws {
        for item in try Self.cases()["composition"] as! [[String: Any]] {
            let composed = StyleLearning.compose(item["global"] as! String, note: item["note"] as! String)
            #expect(composed == item["expect"] as! String, "\(item["id"]!)")
        }
    }

    @Test func autoLearningFollowsSharedCases() throws {
        for item in try Self.cases()["due"] as! [[String: Any]] {
            let due = StyleLearning.isDue(newDictations: item["newDictations"] as! Int, note: item["note"] as! String,
                                          learnedNote: item["learnedNote"] as! String)
            #expect(due == item["expect"] as! Bool, "\(item["id"]!)")
        }
    }

    /// The note sits between the global preferences and an app profile's, which has the last word.
    @Test func anAppProfileStillComesLast() {
        let global = DictationSettings(polishEnabled: true, pressEnter: false, language: "",
                                       extraInstructions: StyleLearning.compose("Never use bullet lists", note: "- Casual tone."))
        let profile = AppProfile(id: "A", app: "com.apple.mail", name: "Mail", instructions: "Formal tone")
        #expect(global.applying(profile).extraInstructions
                == "Never use bullet lists\n" + StyleLearning.preferencesLead + "\n- Casual tone.\nFormal tone")
    }

    /// The tuned prompt is untouched: the note only goes into the speaker's own section after it.
    @Test func theNoteGoesIntoTheSpeakersOwnSection() {
        let prompt = Prompts.polishSystemPrompt(vocabulary: [], extraInstructions: StyleLearning.compose("", note: "- Casual tone."))
        #expect(prompt.hasPrefix(Prompts.basePolishPrompt))
        #expect(prompt.hasSuffix("## Additional preferences from the speaker\n" + StyleLearning.preferencesLead + "\n- Casual tone."))
    }

    @Test func newestCorrectionsAreKept() {
        var edits: [StyleLearning.Edit] = []
        for i in 0..<(StyleLearning.maxEdits + 5) {
            edits = StyleLearning.adding(StyleLearning.Edit(before: "before \(i)", after: "after \(i)")!, to: edits)
        }
        #expect(edits.count == StyleLearning.maxEdits)
        #expect(edits.first?.after == "after \(StyleLearning.maxEdits + 4)")
        #expect(edits.last?.after == "after 5")
        // Saved and read back; a damaged file is no corrections at all.
        #expect(StyleLearning.Edit.list(from: try! JSONEncoder().encode(edits)) == edits)
        #expect(StyleLearning.Edit.list(from: Data("not json".utf8)).isEmpty)
    }

    /// One request, not streamed, with a clean-up's tuning but the note's messages and room.
    @Test func requestBody() throws {
        let endpoint = ProviderEndpoint(id: .openai, baseURL: URL(string: "https://api.openai.com/v1")!, apiKey: "k")
        let body = APIClient(endpoint: endpoint).styleBody(sample: "<texts>\n<text>Hi</text>\n</texts>",
                                                           options: PolishOptions(model: "gpt-5-mini"), includeOptional: true)
        let messages = body["messages"] as! [[String: String]]
        #expect(messages == [["role": "system", "content": StyleLearning.systemPrompt],
                             ["role": "user", "content": "<texts>\n<text>Hi</text>\n</texts>"]])
        #expect(body["stream"] as? Bool == false)
        #expect(body["stream_options"] == nil)
        #expect(body["model"] as? String == "gpt-5-mini")

        let anthropic = ProviderEndpoint(id: .anthropic, baseURL: URL(string: "https://api.anthropic.com/v1")!, apiKey: "k")
        let bare = APIClient(endpoint: anthropic).styleBody(sample: "x", options: PolishOptions(model: "claude"), includeOptional: false)
        #expect(bare["max_tokens"] as? Int == StyleLearning.maxTokens)
    }
}
