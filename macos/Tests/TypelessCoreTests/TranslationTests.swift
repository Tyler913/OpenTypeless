import CryptoKit
import Foundation
import Testing
@testable import TypelessCore

/// Translation mode: its guards, its prompt, its targets and chord, and the fallback to the clean-up.
@Suite struct TranslationTests {
    private struct GuardCase: Decodable {
        let id: String
        let target: String
        let input: String
        let output: String
        let expect: String?
    }

    private struct GuardFile: Decodable {
        let translatePromptSHA256: String
        let cases: [GuardCase]
    }

    private static func fixture() throws -> GuardFile {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/translate-guard-cases.json"))
        return try JSONDecoder().decode(GuardFile.self, from: data)
    }

    /// testdata/translate-guard-cases.json, shared with the Windows tests so both apps agree.
    @Test func sharedCases() throws {
        let cases = try Self.fixture().cases
        #expect(cases.count >= 15)
        for c in cases {
            let target = try #require(TranslationTarget(c.target))
            #expect(Prompts.translationFallback(input: c.input, output: c.output, truncated: false, target: target)?.rawValue
                    == c.expect, "\(c.id)")
        }
    }

    /// The Windows app sends the same template: the fixture holds its hash.
    @Test func promptIsTheSameOnBothPlatforms() throws {
        let hash = SHA256.hash(data: Data(Prompts.baseTranslatePrompt.utf8)).map { String(format: "%02x", $0) }.joined()
        #expect(hash == (try Self.fixture().translatePromptSHA256))
    }

    /// The prompt's own examples are translations by definition, into the language each group names.
    @Test func promptExamplesPass() throws {
        var count = 0
        for section in Prompts.baseTranslatePrompt.components(separatedBy: "## Examples into ").dropFirst() {
            let lines = section.components(separatedBy: "\n")
            let target = try #require(TranslationTarget(lines[0]))
            #expect(target.language != nil, "\(lines[0])")
            for (input, output) in PolishGuardTests.examples(in: "## Examples\n" + lines.dropFirst().joined(separator: "\n")) {
                #expect(Prompts.translationFallback(input: input, output: output, truncated: false, target: target) == nil,
                        "\(input)")
                count += 1
            }
        }
        #expect(count >= 5)
    }

    @Test func truncationComesFirstAndLengthIsGenerous() {
        let english = TranslationTarget(.english)
        #expect(Prompts.translationFallback(input: "好的收到", output: "OK", truncated: true, target: english) == .truncated)
        // A katakana-heavy Japanese translation is far longer than its English, and still fine.
        #expect(!Prompts.translationLooksLikeAnAnswer(input: "check the login page",
                                                      output: "ログインページをもう一度確認してください"))
        // A short transcript isn't judged for what it drops; a long one kept to a few words was summarised.
        #expect(!Prompts.translationDroppedTooMuch(input: "嗯嗯那个就是说呃好的收到", output: "OK."))
    }

    @Test func scriptShareCountsWordsNotLetters() {
        // A Han character weighs as much as a whole Latin word, so terms kept in a Chinese translation don't outweigh it.
        #expect(ScriptShare.share(of: .han, in: "用 SwiftUI 写一个 macOS app") == 4.0 / 7.0)
        #expect(ScriptShare.share(of: .latin, in: "Tell 张伟 at 3 p.m.") == 4.0 / 6.0)
        #expect(ScriptShare.share(of: .cyrillic, in: "Встреча в 15:00") == 1)
        #expect(ScriptShare.share(of: .hangul, in: "123 — !") == 1) // nothing to count
        // Accents written as their own character stay with their letter.
        #expect(ScriptShare.share(of: .latin, in: "porta\u{301}til") == 1)
        #expect(ScriptShare.isMostly("好的", in: .latin)) // too short to tell
    }

    @Test func promptConstruction() {
        let target = TranslationTarget(.chinese)
        let system = Prompts.translateSystemPrompt(target: target, vocabulary: ["Typeless", " "],
                                                   misheard: [Correction(heard: "type less", corrected: "Typeless")],
                                                   extraInstructions: "Keep it casual")
        #expect(system.hasPrefix("You turn a raw speech-to-text transcript into the text the speaker would have typed, written in Simplified Chinese."))
        #expect(!system.contains(Prompts.languagePlaceholder))
        #expect(system.contains("## Vocabulary\nThese terms may appear; spell them exactly like this: Typeless"))
        #expect(system.contains("“type less” → Typeless"))
        #expect(system.hasSuffix("## Additional preferences from the speaker\nKeep it casual"))

        let user = Prompts.translateUserMessage(transcript: "send it to Sarah", target: target)
        #expect(user.hasPrefix("<transcript>\nsend it to Sarah\n</transcript>\n\n(Translate the transcript above into Simplified Chinese"))
        #expect(user.hasSuffix("Output only the Simplified Chinese text.)"))

        // The same options ask for a translation once they name a target, and for a clean-up without one.
        var options = PolishOptions(model: "m", vocabulary: ["Typeless"])
        #expect(APIClient.polishMessages(transcript: "hi", options: options)
                == [Prompts.polishSystemPrompt(vocabulary: ["Typeless"], extraInstructions: ""),
                    Prompts.polishUserMessage(transcript: "hi")])
        options.translation = TranslationTarget("Italiano")
        let messages = APIClient.polishMessages(transcript: "hi", options: options)
        #expect(messages[0].contains("written in Italiano."))
        #expect(messages[1].hasSuffix("Output only the Italiano text.)"))

        // A reminder echoed back is taken out, whichever was sent.
        let reminder = Prompts.reminder(translation: target)
        #expect(Prompts.sanitizePolishOutput("把报告发给 Sarah。\n\n" + reminder, reminder: reminder) == "把报告发给 Sarah。")
        #expect(Prompts.sanitizePolishOutput("Hi.\n" + Prompts.polishReminder) == "Hi.")
    }

    @Test func targets() {
        #expect(TranslationTarget("  ") == nil)
        #expect(TranslationTarget(" en ")?.language == .english)
        #expect(TranslationTarget("日本語")?.language == .japanese)
        #expect(TranslationTarget("Simplified Chinese")?.badge == "→ ZH")
        #expect(TranslationTarget("Italiano")?.language == nil)
        #expect(TranslationTarget("Italiano")?.badge == "→ Italiano")
        #expect(TranslationTarget("Italiano")?.script == nil)
        #expect(TranslationTarget(.korean).displayName == "한국어")
        #expect(TranslationTarget(.russian).promptName == "Russian")
        #expect(TranslationLanguage.allCases.count == 9)
    }

    /// What History saves with a translation dictation reads back as the same target; a plain dictation (or one saved
    /// before translation existed) saves nothing and reads back as no translation.
    @Test func recordModeRoundTrip() {
        for target in TranslationLanguage.allCases.map({ TranslationTarget($0) }) + [TranslationTarget("Italiano")!] {
            #expect(TranslationTarget(mode: TranslationTarget.recordMode, targetLanguage: target.id) == target)
        }
        #expect(TranslationTarget(mode: nil, targetLanguage: nil) == nil)
        #expect(TranslationTarget(mode: "translate", targetLanguage: nil) == nil)
        #expect(TranslationTarget(mode: "somethingNewer", targetLanguage: "en") == nil)
    }

    private enum Mode: Equatable, Sendable { case dictation, translate }

    /// Holding the hotkey and pressing either Shift switches the recording to translation, for as long as the hotkey is
    /// held; any other key still makes a combination that cancels it.
    @Test func chordSwitchesToTranslationAndOtherKeysStillCancel() {
        #expect(TranslationChord.shift.keyCodes(excludingHotkey: 63) == [56, 60]) // Fn
        #expect(TranslationChord.shift.keyCodes(excludingHotkey: 60) == [56]) // Right ⇧ is the hotkey
        #expect(TranslationChord.alt.keyCodes(excludingHotkey: nil) == [58, 61])
        #expect(TranslationChord.control.keyCodes(excludingHotkey: 62) == [59])

        var chords = ModeChords<Mode>()
        for key in TranslationChord.shift.keyCodes(excludingHotkey: 63) { chords.register(key, mode: .translate) }
        #expect(chords.otherKey(56, pressedAt: 10, at: 10.1, handsFree: false) == .switchMode(.translate))
        #expect(chords.otherKey(60, pressedAt: 10, at: 25, handsFree: false) == .switchMode(.translate))
        #expect(chords.otherKey(122, pressedAt: 10, at: 10.1, handsFree: false) == .cancel) // Fn+F1
        #expect(chords.otherKey(122, pressedAt: 10, at: 12, handsFree: false) == .ignore)
    }

    // MARK: Falling back to the clean-up

    private func result(_ texts: [String], fallbacks: [PolishFallback?]) -> SegmentedPolishResult {
        let parts = zip(texts, fallbacks).map { text, fallback in
            SegmentedPolishResult.Part(transcript: "原文", answer: answer(text), fallback: fallback, error: nil)
        }
        return SegmentedPolishResult(text: SegmentedPolish.join(parts.map(\.text)), parts: parts, firstTokenSeconds: 0.1,
                                     totalSeconds: 0.2)
    }

    private func answer(_ text: String) -> HedgedPolishResult {
        HedgedPolishResult(result: .init(text: text, truncated: false, usage: RequestUsage(inputTokens: 10, outputTokens: 5)),
                           model: "m", usedBackup: false, firstTokenSeconds: 0.1, totalSeconds: 0.2)
    }

    @Test func aUsableTranslationTakesOneRequest() async throws {
        let calls = TextLog()
        let outcome = try await TranslatedPolish.run(translate: {
            calls.add("translate")
            return result(["Hello."], fallbacks: [nil])
        }, cleanUp: {
            calls.add("clean-up")
            return result(["你好。"], fallbacks: [nil])
        })
        #expect(outcome.translated)
        #expect(outcome.translationFallback == nil)
        #expect(outcome.cleanUp == nil)
        #expect(calls.all == ["translate"])
    }

    @Test func anUnusableTranslationFallsBackToTheCleanUp() async throws {
        // One part came back untranslated: the whole translation is set aside, not inserted half translated.
        let partly = try await TranslatedPolish.run(translate: {
            result(["Hello.", "你好。"], fallbacks: [nil, .untranslated])
        }, cleanUp: { result(["你好。"], fallbacks: [nil]) })
        #expect(!partly.translated)
        #expect(partly.translationFallback == .untranslated)
        #expect(partly.translation != nil) // billed all the same
        #expect(partly.cleanUp?.text == "你好。")

        let failed = try await TranslatedPolish.run(translate: { throw APIError.http(status: 503, message: "down") },
                                                    cleanUp: { result(["你好。"], fallbacks: [nil]) })
        #expect(failed.translationFallback == .failed)
        #expect(failed.translationError == .http(status: 503, message: "down"))
        #expect(failed.cleanUp?.text == "你好。")

        // Clean-up off: the transcript goes in as spoken.
        let off = try await TranslatedPolish.run(translate: { result(["你好。"], fallbacks: [.untranslated]) }, cleanUp: nil)
        #expect(!off.translated && off.cleanUp == nil && off.cleanUpError == nil)

        let both = try await TranslatedPolish.run(translate: { throw APIError.timeout(seconds: 10) },
                                                  cleanUp: { throw APIError.http(status: 401, message: "bad key") })
        #expect(both.cleanUp == nil)
        #expect(both.cleanUpError == .http(status: 401, message: "bad key"))
    }

    @Test func cancellationIsPassedOn() async {
        await #expect(throws: APIError.cancelled) {
            try await TranslatedPolish.run(translate: { throw APIError.cancelled }, cleanUp: { result(["x"], fallbacks: [nil]) })
        }
        await #expect(throws: APIError.cancelled) {
            try await TranslatedPolish.run(translate: { result(["x"], fallbacks: [.failed]) }, cleanUp: { throw CancellationError() })
        }
    }

    /// SegmentedPolish checks a translation with the translation's guards: the clean-up's would reject it as unrelated.
    @Test func segmentedPolishChecksATranslationAsOne() async throws {
        let transcript = "嗯我们周三下午三点开会，不对不对，是周四，然后记得带电脑"
        let english = "We're meeting on Thursday at 3 p.m. Remember to bring your laptop."
        #expect(Prompts.fallback(input: transcript, output: english, truncated: false) == .unrelated)
        let translated = try await SegmentedPolish.run(transcript: transcript, translation: TranslationTarget(.english),
                                                       polish: { _, _ in answer(english) })
        #expect(translated.fallbacks.isEmpty)
        #expect(translated.text == english)
        let untranslated = try await SegmentedPolish.run(transcript: transcript, translation: TranslationTarget(.english),
                                                         polish: { _, _ in answer("我们周四下午三点开会，记得带电脑。") })
        #expect(untranslated.parts.map(\.fallback) == [.untranslated])
    }
}

private final class TextLog: @unchecked Sendable {
    private let lock = NSLock()
    private var texts: [String] = []
    func add(_ text: String) { lock.withLock { texts.append(text) } }
    var all: [String] { lock.withLock { texts } }
}
