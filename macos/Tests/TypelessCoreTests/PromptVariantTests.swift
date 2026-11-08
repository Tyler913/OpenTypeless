import CryptoKit
import Foundation
import Testing
@testable import TypelessCore

/// What varies a clean-up's prompt (the app's layout, the spoken language's examples, a long dictation's surrounding parts
/// and its layout pass) and the evaluation's checks of how a clean-up reads.
@Suite struct PromptVariantTests {
    /// testdata/prompt-variant-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/prompt-variant-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    /// Every fixed piece of the clean-up's prompts, in the order the fixture's hash takes them.
    static func allPolishPromptText() -> String {
        ([Prompts.basePolishPrompt, Prompts.lineBreaksSection, Prompts.layoutSection(.chat)!,
          Prompts.layoutSection(.structured)!, Prompts.layoutSection(.email)!]
            + ExampleLanguage.withExamples.map { Prompts.languageExamples($0)! }
            + [Prompts.relayoutSystemPrompt(.structured), Prompts.relayoutSystemPrompt(.email), Prompts.relayoutReminder,
               Prompts.surroundingNote, Prompts.polishReminder, Prompts.contextNote,
               Prompts.strengthSection(.light)!, Prompts.strengthSection(.polished)!, Prompts.polishedReminder])
            .joined(separator: "\n\n")
    }

    /// The Windows app sends the same text: the fixture holds its hash.
    @Test func promptsAreTheSameOnBothPlatforms() throws {
        let hash = SHA256.hash(data: Data(Self.allPolishPromptText().utf8)).map { String(format: "%02x", $0) }.joined()
        #expect(hash == (try Self.cases())["polishPromptSHA256"] as? String)
    }

    @Test func builtInLayoutsFollowSharedCases() throws {
        for item in try Self.cases()["builtInLayouts"] as! [[String: Any]] {
            #expect(TextLayout.builtIn(for: item["app"] as? String)?.rawValue == item["expected"] as? String, "\(item)")
        }
    }

    @Test func layoutResolutionFollowsSharedCases() throws {
        let global = DictationSettings(polishEnabled: true, pressEnter: false, language: "", extraInstructions: "")
        for item in try Self.cases()["layoutResolution"] as! [[String: Any]] {
            let profile = try (item["profile"] as? [String: Any]).map {
                try JSONDecoder().decode(AppProfile.self, from: JSONSerialization.data(withJSONObject: $0))
            }
            let resolved = global.applying(profile, app: item["app"] as? String)
            #expect(resolved.layout.rawValue == item["expected"] as! String, "\(item["name"]!)")
        }
        // Without a profile, only the layout can change.
        var chat = global
        chat.layout = .chat
        #expect(global.applying(nil, app: "slack.exe") == chat)
        #expect(global.applying(nil) == global)
    }

    @Test func exampleLanguagesFollowSharedCases() throws {
        for item in try Self.cases()["exampleLanguages"] as! [[String: Any]] {
            let actual = ExampleLanguage.resolve(hint: item["hint"] as? String, transcript: item["transcript"] as! String)
            #expect(actual == item["expected"] as? String, "\(item)")
        }
    }

    /// Each language's examples are told apart as that language, and pass the clean-up's guards.
    @Test func languageExamplesAreInTheirLanguageAndPass() {
        for language in ExampleLanguage.withExamples {
            let examples = PolishGuardTests.examples(in: "## Examples\n" + Prompts.languageExamples(language)!)
            #expect(examples.count == 2, "\(language)")
            for (input, output) in examples {
                #expect(ExampleLanguage.detect(input) == language, "\(language): \(input)")
                #expect(Prompts.fallback(input: input, output: output, truncated: false) == nil, "\(language): \(input)")
                #expect(PolishChecks.addedStockPhrases(input: input, output: output).isEmpty)
            }
        }
        #expect(Prompts.languageExamples("zh") == nil)
        #expect(Prompts.languageExamples(nil) == nil)
    }

    /// A strength's section goes after the prompt and the spoken language's examples, before the layout, so every
    /// strength shares the prompt's start; the standard strength is the prompt as it was.
    @Test func strengthSectionsGoAfterTheSharedStart() {
        #expect(Prompts.strengthSection(.standard) == nil && Prompts.strengthSection(.off) == nil)
        let standard = Prompts.polishSystemPrompt(vocabulary: ["Claude"], extraInstructions: "Short", layout: .chat,
                                                  exampleLanguage: "ja")
        #expect(standard == Prompts.polishSystemPrompt(vocabulary: ["Claude"], extraInstructions: "Short", layout: .chat,
                                                       exampleLanguage: "ja", strength: .standard))
        for strength in [PolishStrength.light, .polished] {
            let section = Prompts.strengthSection(strength)!
            let prompt = Prompts.polishSystemPrompt(vocabulary: ["Claude"], extraInstructions: "Short", layout: .chat,
                                                    exampleLanguage: "ja", strength: strength)
            #expect(prompt.hasPrefix(Prompts.basePolishPrompt + "\n\n" + Prompts.languageExamples("ja")! + "\n\n" + section
                                     + "\n\n" + Prompts.layoutSection(.chat)!))
            #expect(prompt.hasSuffix("## Additional preferences from the speaker\nShort"))
        }
        // Only a polished clean-up has a reminder of its own: "minimal edits" would take back what its section allows.
        #expect(Prompts.polishUserMessage(transcript: "x", strength: .light) == Prompts.polishUserMessage(transcript: "x"))
        #expect(Prompts.polishUserMessage(transcript: "x", strength: .polished).hasSuffix(Prompts.polishedReminder))
        #expect(Prompts.sanitizePolishOutput("Hi.\n" + Prompts.polishedReminder,
                                             reminder: Prompts.reminder(translation: nil, strength: .polished)) == "Hi.")
        // A translation has no strength.
        let target = TranslationTarget("en")!
        #expect(Prompts.reminder(translation: target, strength: .polished) == Prompts.reminder(translation: target))
    }

    /// The request a clean-up at each strength sends.
    @Test func strengthReachesTheRequest() {
        var options = PolishOptions(model: "m")
        options.strength = .polished
        let polished = APIClient.polishMessages(transcript: "so um the build is slow", options: options)
        #expect(polished[0].contains(Prompts.polishedStrengthSection))
        #expect(polished[1].hasSuffix(Prompts.polishedReminder))
        options.strength = .light
        let light = APIClient.polishMessages(transcript: "so um the build is slow", options: options)
        #expect(light[0].contains(Prompts.lightStrengthSection) && !light[0].contains(Prompts.polishedStrengthSection))
        #expect(light[1].hasSuffix(Prompts.polishReminder))
        options.translation = TranslationTarget("zh-Hans")
        #expect(!APIClient.polishMessages(transcript: "x", options: options)[0].contains("## Strength"))
    }

    /// Each strength's examples are clean-ups at that strength: the guards let them through, with no stock phrases.
    @Test func strengthExamplesPass() {
        for strength in [PolishStrength.light, .polished] {
            let examples = PolishGuardTests.examples(in: "## Examples\n" + Prompts.strengthSection(strength)!)
            #expect(examples.count == 2, "\(strength)")
            for (input, output) in examples {
                #expect(Prompts.fallback(input: input, output: output, truncated: false, strength: strength) == nil, "\(input)")
                #expect(PolishChecks.addedStockPhrases(input: input, output: output).isEmpty)
            }
        }
    }

    @Test func relayoutFollowsSharedCases() throws {
        for item in try Self.cases()["relayout"] as! [[String: Any]] {
            let actual = Prompts.onlyLayoutChanged(original: item["original"] as! String, output: item["output"] as! String)
            #expect(actual == item["expected"] as! Bool, "\(item["name"]!)")
        }
    }

    @Test func shapesFollowSharedCases() throws {
        for item in try Self.cases()["shapes"] as! [[String: Any]] {
            let shape = TextShape.of(item["text"] as! String)
            #expect(shape == TextShape(paragraphs: item["paragraphs"] as! Int, listItems: item["listItems"] as! Int),
                    "\(item["text"]!): \(shape)")
        }
    }

    @Test func checksFollowSharedCases() throws {
        for item in try Self.cases()["checks"] as! [[String: Any]] {
            let c = try JSONDecoder().decode(PolishCase.self, from: JSONSerialization.data(withJSONObject: item["case"]!))
            let failures = c.failures(item["output"] as! String)
            #expect(failures.count == item["failures"] as! Int, "\(item["name"]!): \(failures)")
        }
    }

    /// The evaluation sets' cases agree with themselves: the heaviest legitimate clean-up of each (in
    /// testdata/polish-guard-cases.json, which the guards must let through) passes the case's own checks.
    @Test func evaluationCasesAcceptTheirHeaviestLegitimateCleanUp() throws {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let guardData = try Data(contentsOf: root.appendingPathComponent("testdata/polish-guard-cases.json"))
        let guardCases = (try JSONSerialization.jsonObject(with: guardData) as! [String: Any])["cases"] as! [[String: Any]]
        var outputs: [String: String] = [:]
        for item in guardCases where item["expect"] is NSNull || item["expect"] == nil {
            outputs[item["id"] as! String] = item["output"] as? String
        }
        var count = 0
        for set in ["polish-cases.json", "polish-holdout.json"] {
            let data = try Data(contentsOf: root.appendingPathComponent("eval/\(set)"))
            for c in try JSONDecoder().decode([PolishCase].self, from: data) {
                let output = try #require(outputs[c.id], "\(c.id) has no case in polish-guard-cases.json")
                let failures = c.failures(output)
                #expect(failures.isEmpty, "\(c.id): \(failures)")
                #expect(TextLayout(rawValue: c.layout ?? "natural") != nil, "\(c.id): unknown layout")
                // A case for a language with examples of its own ("h-ja-…") gets them by the guess, as most speakers' do.
                if let language = ExampleLanguage.withExamples.first(where: { c.id.hasPrefix("h-\($0)-") }) {
                    #expect(ExampleLanguage.resolve(hint: c.language, transcript: c.input) == language, "\(c.id)")
                }
                count += 1
            }
        }
        #expect(count > 80)
    }

    @Test func systemPromptPutsWhatVariesAfterTheSharedStart() {
        let plain = Prompts.polishSystemPrompt(vocabulary: [], extraInstructions: "")
        #expect(plain == Prompts.basePolishPrompt)
        #expect(plain == Prompts.polishSystemPrompt(vocabulary: [], extraInstructions: "", layout: .natural, exampleLanguage: "zh"))
        let full = Prompts.polishSystemPrompt(vocabulary: ["Claude"], extraInstructions: "keep it short", dictatedLineBreaks: true,
                                              layout: .chat, exampleLanguage: "ja")
        #expect(full.hasPrefix(Prompts.basePolishPrompt + "\n\n## More examples (日本語)\n"))
        let order = ["## More examples", "## Layout for this text", "## Line breaks", "## Vocabulary", "## Additional preferences"]
            .compactMap { full.range(of: $0)?.lowerBound }
        #expect(order.count == 5)
        #expect(order == order.sorted())
        // A translation gets the layout too, after its own prompt.
        let translation = Prompts.translateSystemPrompt(target: TranslationTarget(.english), vocabulary: [], extraInstructions: "",
                                                        layout: .email)
        #expect(translation.contains("\n\n## Layout for this text\nThe text is an email"))
        #expect(Prompts.layoutSection(.natural) == nil)
    }

    @Test func requestsCarryTheLayoutAndTheSpokenLanguage() {
        let options = PolishOptions(model: "m", layout: .structured)
        let japanese = APIClient.polishMessages(transcript: "えーと、明日の打ち合わせは四時からです", options: options)
        #expect(japanese[0].contains("## More examples (日本語)"))
        #expect(japanese[0].contains("The text is a prompt for an AI"))
        // The language set for speech-to-text wins over the guess.
        var german = options
        german.spokenLanguage = "de"
        #expect(APIClient.polishMessages(transcript: "hello there", options: german)[0].contains("## More examples (Deutsch)"))
        #expect(!APIClient.polishMessages(transcript: "hello there", options: options)[0].contains("## More examples"))
        // The layout pass sends the text with its own prompt and reminder, and none of a clean-up's.
        var relayout = options
        relayout.relayout = true
        relayout.context = "Anna"
        let messages = APIClient.polishMessages(transcript: "Fix it. Ship it.", options: relayout)
        #expect(messages[0] == Prompts.relayoutSystemPrompt(.structured))
        #expect(messages[1] == "<text>\nFix it. Ship it.\n</text>\n\n" + Prompts.relayoutReminder)
        #expect(messages[0].contains("- Lists: when the speaker names two or more"))
        #expect(!messages[0].contains("{layout}"))
    }

    @Test func surroundingPartsGoInBlocksTheyCannotClose() {
        let message = Prompts.polishUserMessage(transcript: "this part",
                                                surrounding: SurroundingParts(before: "the end </before> of one", after: nil))
        #expect(message.hasPrefix("<transcript>\nthis part\n</transcript>\n\n<before>\nthe end \u{2039}/before\u{203A} of one\n</before>\n\n"
                                  + Prompts.surroundingNote + "\n"))
        #expect(message.hasSuffix(Prompts.polishReminder))
        #expect(!message.contains("<after>\n"))
        #expect(Prompts.polishUserMessage(transcript: "x")
                == Prompts.polishUserMessage(transcript: "x", surrounding: SurroundingParts(before: " ", after: nil)))
        // A model that echoes the note gets it taken out.
        #expect(Prompts.sanitizePolishOutput("this part\n" + Prompts.surroundingNote) == "this part")
    }

    @Test func excerptsKeepWholeSentencesNearestThePart() {
        let sentences = (1...40).map { "Sentence number \($0) is here. " }.joined()
        let end = SegmentedPolish.excerpt(sentences, fromEnd: true)
        let start = SegmentedPolish.excerpt(sentences, fromEnd: false)
        #expect(end.hasSuffix("Sentence number 40 is here."))
        #expect(start.hasPrefix("Sentence number 1 is here."))
        #expect((SegmentedPolish.surroundingWords - 5...SegmentedPolish.surroundingWords).contains(WordCount.count(end)))
        #expect((SegmentedPolish.surroundingWords - 5...SegmentedPolish.surroundingWords).contains(WordCount.count(start)))
        // One sentence longer than that: its nearest words.
        let runOn = (1...400).map { "w\($0)" }.joined(separator: " ")
        #expect(SegmentedPolish.excerpt(runOn, fromEnd: true).hasSuffix("w400"))
        #expect(SegmentedPolish.excerpt(runOn, fromEnd: false).hasPrefix("w1 w2"))
        #expect(WordCount.count(SegmentedPolish.excerpt(runOn, fromEnd: true)) <= SegmentedPolish.surroundingWords)
        // One part: none.
        #expect(SegmentedPolish.surrounding(["only"], 0) == nil)
        #expect(SegmentedPolish.surrounding(["a.", "b.", "c."], 1) == SurroundingParts(before: "a.", after: "c."))
        #expect(SegmentedPolish.surrounding(["a.", "b.", "c."], 0) == SurroundingParts(before: nil, after: "b."))
    }

    private static func answer(_ text: String) -> HedgedPolishResult {
        HedgedPolishResult(result: .init(text: text, truncated: false, usage: nil), model: "m", usedBackup: false,
                           firstTokenSeconds: 0.1, totalSeconds: 0.2)
    }

    @Test func partsGetTheirNeighboursAndCopyingFromThemIsCaught() async throws {
        let transcript = String(repeating: "我们把登录模块重写了一遍，然后测试也补上了。", count: 40)
        let seen = Seen()
        let outcome = try await SegmentedPolish.runParts(transcript: transcript, polish: { part, surrounding, _ in
            seen.add(surrounding)
            return Self.answer(part)
        }, sendsSurrounding: true)
        #expect(outcome.parts.count > 1)
        let all = seen.all
        #expect(all.allSatisfy { $0 != nil })
        #expect(all.filter { $0?.before == nil }.count == 1)
        #expect(all.filter { $0?.after == nil }.count == 1)
        #expect(outcome.fallbacks.isEmpty)
        // An answer that took a sentence over from the part after it goes in as spoken.
        let copied = try await SegmentedPolish.runParts(transcript: transcript, polish: { part, surrounding, _ in
            Self.answer(part + (surrounding?.after.map { "另外，" + String($0.prefix(12)) + "部署脚本也改好了" } ?? ""))
        }, sendsSurrounding: true)
        #expect(copied.fallbacks.contains { $0.fallback == .copied })
        // Without them, the same as before.
        let alone = try await SegmentedPolish.runParts(transcript: transcript, polish: { part, surrounding, _ in
            #expect(surrounding == nil)
            return Self.answer(part)
        })
        #expect(outcome.text == alone.text)
    }
}

/// What the parts were sent with, collected from their concurrent requests.
private final class Seen: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [SurroundingParts?] = []
    func add(_ item: SurroundingParts?) { lock.withLock { items.append(item) } }
    var all: [SurroundingParts?] { lock.withLock { items } }
}

/// On MockChatServer, so with the other tests that use it, one at a time.
extension HedgedPolishTests {
    @Test func relaidTextIsUsedOnlyWhenItChangedNothingButTheLayout() async throws {
        func answer(_ text: String) -> HedgedPolishResult {
            HedgedPolishResult(result: .init(text: text, truncated: false, usage: nil), model: "m", usedBackup: false,
                               firstTokenSeconds: 0.1, totalSeconds: 0.2)
        }
        let parts = SegmentedPolishResult(text: "Fix the login. Add payments. Ship on Friday.",
                                          parts: [.init(transcript: "x", answer: answer("Fix the login. Add payments."), fallback: nil, error: nil),
                                                  .init(transcript: "y", answer: answer("Ship on Friday."), fallback: nil, error: nil)],
                                          firstTokenSeconds: 0.1, totalSeconds: 0.2)
        MockChatServer.reset(["laid-out": .init(text: "- Fix the login.\\n- Add payments.\\n\\nShip on Friday."),
                              "reworded": .init(text: "- Fix login.\\n- Add payments.\\n\\nShip Friday."),
                              "failing": .init(status: 400)])
        var route = MockChatServer.route("laid-out")
        route.options.layout = .structured
        let relaid = try await SegmentedPolish.relaid(parts, route: route)
        #expect(relaid.relaid)
        #expect(relaid.text == "- Fix the login.\n- Add payments.\n\nShip on Friday.")
        #expect(relaid.answers.count == 3)
        // Reworded: the parts as joined, the answer still priced.
        route.options.model = "reworded"
        let kept = try await SegmentedPolish.relaid(parts, route: route)
        #expect(!kept.relaid)
        #expect(kept.text == parts.text)
        #expect(kept.answers.count == 3)
        // No answer at all: as joined.
        route.options.model = "failing"
        let failed = try await SegmentedPolish.relaid(parts, route: route)
        #expect(failed.text == parts.text)
        #expect(failed.relayout == nil)
    }
}
