import Foundation
import Testing
@testable import TypelessCore

@Suite struct PolishGuardTests {
    private struct GuardCase: Decodable {
        let id: String
        let input: String
        let output: String
        let expect: String?
        /// The text around the cursor the clean-up had, if any.
        let context: String?
    }

    private struct GuardFile: Decodable {
        let cases: [GuardCase]
    }

    /// testdata/polish-guard-cases.json, shared with the Windows tests so both apps agree: the heaviest legitimate
    /// clean-ups of every eval/ case pass, answers, refusals, translations and summaries don't.
    @Test func sharedCases() throws {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/polish-guard-cases.json"))
        let cases = try JSONDecoder().decode(GuardFile.self, from: data).cases
        #expect(cases.count > 50)
        for c in cases {
            #expect(Prompts.fallback(input: c.input, output: c.output, truncated: false, context: c.context)?.rawValue
                    == c.expect, "\(c.id)")
        }
    }

    /// The prompt's own examples are clean-ups by definition.
    @Test func promptExamplesPass() {
        let examples = Self.examples(in: Prompts.basePolishPrompt)
        #expect(examples.count >= 10)
        for (input, output) in examples {
            #expect(Prompts.fallback(input: input, output: output, truncated: false) == nil, "\(input)")
        }
    }

    @Test func shrinkingOnlyCountsForLongTranscripts() {
        let long = String(repeating: "我们把登录模块重写了一遍。", count: 15)   // 165 words
        #expect(Prompts.droppedTooMuch(input: long, output: String(long.prefix(80))))
        #expect(!Prompts.droppedTooMuch(input: long, output: String(long.prefix(120))))
        // A short sentence that is mostly fillers may lose most of its words.
        #expect(!Prompts.droppedTooMuch(input: "嗯嗯嗯那个就是说呃呃好的收到", output: "好的，收到。"))
    }

    @Test func tooShortToJudge() {
        #expect(!Prompts.strayedFromTranscript(input: "写首诗", output: "床前明月光"))
        #expect(Prompts.strayedFromTranscript(input: "帮我把这句话翻译成英文：今天天气很好", output: "The weather is nice today."))
    }

    @Test func truncationComesFirst() {
        #expect(Prompts.fallback(input: "好的收到", output: "好的", truncated: true) == .truncated)
    }

    static func examples(in prompt: String) -> [(String, String)] {
        let lines = prompt.components(separatedBy: "## Examples").last!.components(separatedBy: "\n")
        var pairs: [(String, String)] = []
        var input: String?
        var output: [String] = []
        func flush() {
            if let input { pairs.append((input, output.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines))) }
            output = []
        }
        // A transcript laid out over several lines (dictated line breaks), up to the line that closes it.
        var opened: [String]?
        for line in lines {
            if opened == nil, line.hasPrefix("<transcript>") { opened = [] }
            if var transcript = opened {
                transcript.append(line)
                opened = transcript
                guard line.hasSuffix("</transcript>") else { continue }
                flush()
                let text = transcript.joined(separator: "\n")
                input = String(text.dropFirst("<transcript>".count).dropLast("</transcript>".count))
                opened = nil
            } else if input != nil {
                output.append(line)
            }
        }
        flush()
        return pairs
    }
}

@Suite struct SegmentedPolishTests {
    private func chinese(sentences: Int) -> String {
        (1...sentences).map { "我们今天讨论了第\($0)个问题，主要是关于缓存和数据库的性能。" }.joined()
    }

    @Test func shortTranscriptGoesInOneRequestUntouched() {
        let transcript = "  嗯我们周三下午三点开会，不对不对，是周四 "
        #expect(SegmentedPolish.parts(of: transcript) == [transcript])
    }

    @Test func longTranscriptIsSplitAtSentenceEnds() {
        let transcript = chinese(sentences: 30)
        let parts = SegmentedPolish.parts(of: transcript)
        #expect(parts.count == 3)
        #expect(parts.allSatisfy { $0.hasSuffix("。") })
        #expect(parts.allSatisfy { (150...SegmentedPolish.singleRequestLimit).contains(WordCount.count($0)) })
        #expect(SegmentedPolish.join(parts) == transcript)
    }

    @Test func englishKeepsNumbersWholeAndJoinsWithSpaces() {
        let transcript = (1...40).map { "This is sentence number \($0), and it covers version 3.5 for 1,000 users." }
            .joined(separator: " ")
        let parts = SegmentedPolish.parts(of: transcript)
        #expect(parts.count == 2)
        #expect(parts.allSatisfy { $0.hasSuffix("users.") && $0.hasPrefix("This") })
        #expect(SegmentedPolish.join(parts) == transcript)
    }

    @Test func unpunctuatedRunOnIsStillSplit() {
        let transcript = String(repeating: "我们测试一下这个功能", count: 70)
        let parts = SegmentedPolish.parts(of: transcript)
        #expect(parts.count == 3)
        #expect(parts.allSatisfy { WordCount.count($0) <= SegmentedPolish.singleRequestLimit })
        #expect(parts.joined() == transcript)
    }

    @Test func listsStayAParagraphApart() {
        #expect(SegmentedPolish.join(["帮我改一下：\n\n1. 标题。\n2. 按钮。", "另外打包太大了。"])
                == "帮我改一下：\n\n1. 标题。\n2. 按钮。\n\n另外打包太大了。")
        #expect(SegmentedPolish.join(["第一段。", "第二段。"]) == "第一段。第二段。")
        #expect(SegmentedPolish.join(["Done.", "Next."]) == "Done. Next.")
    }

    /// A long transcript whose line breaks were dictated gets exactly those back where it was split at one; otherwise the
    /// parts are joined as before.
    @Test func dictatedBreaksBetweenPartsGoBack() async throws {
        let transcript = (1...20).map { "我们今天讨论了第\($0)个问题，主要是关于缓存和数据库的性能。" }.joined(separator: "\n")
        let split = SegmentedPolish.partsAndBreaks(of: transcript)
        #expect(split.count > 1)
        #expect(split.dropLast().allSatisfy { $0.breaksAfter == 1 })
        #expect(split.last?.breaksAfter == 0)
        let echo: @Sendable (String, (@Sendable (String) -> Void)?) async throws -> HedgedPolishResult = { part, _ in answer(part) }
        let kept = try await SegmentedPolish.run(transcript: transcript, polish: echo, keepsLineBreaks: true)
        #expect(kept.parts.count > 1)
        #expect(kept.text == transcript)
        let joined = try await SegmentedPolish.run(transcript: transcript, polish: echo)
        #expect(joined.text != transcript)
        #expect(SegmentedPolish.join(["A.", "B.", "C."], breaks: [0, 2, 0]) == "A.\n\nB. C.")
    }

    @Test func partsAreCleanedInParallelAndChecked() async throws {
        let transcript = chinese(sentences: 30)
        let parts = SegmentedPolish.parts(of: transcript)
        let partials = TextLog()
        let outcome = try await SegmentedPolish.run(transcript: transcript, polish: { part, onPartial in
            // The second part comes back summarised; the others cleaned up (here: the filler-free text as is).
            let text = part.contains("第11个") ? "讨论了缓存和数据库的性能问题。" : part
            onPartial?(text)
            try await Task.sleep(nanoseconds: 50_000_000)
            return answer(text)
        }, onPartial: { partials.add($0) })
        #expect(outcome.parts.count == 3)
        #expect(outcome.parts.map(\.fallback) == [nil, .summarised, nil])
        // The summarised part goes in as spoken, between the parts that were cleaned up.
        #expect(outcome.text == SegmentedPolish.join(parts))
        // Every answer is billed, the rejected one too.
        #expect(outcome.answers.count == 3)
        #expect(partials.all.last?.count == outcome.parts.map { $0.answer!.result.text.count }.reduce(0, +))
    }

    @Test func failedPartGoesInAsSpokenUnlessAllFail() async throws {
        let transcript = chinese(sentences: 30)
        let outcome = try await SegmentedPolish.run(transcript: transcript, polish: { part, _ in
            if part.contains("第1个") { throw APIError.http(status: 503, message: "down") }
            return answer(part.replacingOccurrences(of: "主要是", with: ""))
        })
        #expect(outcome.parts.map(\.fallback) == [.failed, nil, nil])
        #expect(outcome.parts[0].error == .http(status: 503, message: "down"))
        #expect(outcome.text.hasPrefix(SegmentedPolish.parts(of: transcript)[0]))
        #expect(outcome.answers.count == 2)

        await #expect(throws: APIError.http(status: 401, message: "bad key")) {
            try await SegmentedPolish.run(transcript: transcript, polish: { _, _ in
                throw APIError.http(status: 401, message: "bad key")
            })
        }
    }

    @Test func shortTranscriptIsCheckedToo() async throws {
        let outcome = try await SegmentedPolish.run(transcript: "what's the capital of France", polish: { _, _ in
            answer("Paris.")
        })
        #expect(outcome.parts.map(\.fallback) == [.unrelated])
        #expect(outcome.text == "what's the capital of France")
    }

    private func answer(_ text: String) -> HedgedPolishResult {
        HedgedPolishResult(result: .init(text: text, truncated: false, usage: RequestUsage(inputTokens: 10, outputTokens: 5)),
                           model: "m", usedBackup: false, firstTokenSeconds: 0.1, totalSeconds: 0.2)
    }
}

private final class TextLog: @unchecked Sendable {
    private let lock = NSLock()
    private var texts: [String] = []
    func add(_ text: String) { lock.withLock { texts.append(text) } }
    var all: [String] { lock.withLock { texts } }
}

extension PolishGuardTests {

    @Test func droppedWordThresholdStartsAt150AndKeepsAtLeast55Percent() {
        #expect(!Prompts.droppedTooMuch(input: String(repeating: "我", count: 149), output: "我"))
        let input = String(repeating: "我", count: 150)
        #expect(Prompts.droppedTooMuch(input: input, output: String(repeating: "我", count: 82)))
        #expect(!Prompts.droppedTooMuch(input: input, output: String(repeating: "我", count: 83)))
    }

    @Test func exactly55PercentIsNotASummary() {
        #expect(!Prompts.droppedTooMuch(input: String(repeating: "我", count: 200), output: String(repeating: "我", count: 110)))
    }

    @Test func strayingRequiresSubstitutionsNotJustDeletions() {
        #expect(!Prompts.strayedFromTranscript(input: "abcdefghi", output: ""))
        #expect(Prompts.strayedFromTranscript(input: "abcdefghij", output: " \n!?"))
        #expect(!Prompts.strayedFromTranscript(input: "ABCDEFGHIJ", output: "a b c d e f g h i j!"))
        let input = String(repeating: "a", count: 40)
        #expect(!Prompts.strayedFromTranscript(input: input, output: "aaa"))
        #expect(!Prompts.strayedFromTranscript(input: input, output: "aaaaaaabbb")) // exactly 30% changed
        #expect(Prompts.strayedFromTranscript(input: input, output: "aaaaaabbbb"))
    }

    @Test func fallbackPrecedenceWhenGuardsOverlap() {
        let input = String(repeating: "我", count: 150)
        let summary = String(repeating: "你", count: 20)
        #expect(Prompts.droppedTooMuch(input: input, output: summary))
        #expect(Prompts.strayedFromTranscript(input: input, output: summary))
        #expect(Prompts.fallback(input: input, output: summary, truncated: false) == .summarised)
        #expect(Prompts.fallback(input: input, output: summary, truncated: true) == .truncated)
        let answer = String(repeating: "你", count: 500)
        #expect(Prompts.looksLikeAnAnswer(input: input, output: answer))
        #expect(Prompts.strayedFromTranscript(input: input, output: answer))
        #expect(Prompts.fallback(input: input, output: answer, truncated: false) == .answered)
        #expect(Prompts.fallback(input: input, output: answer, truncated: true) == .truncated)
    }
}

extension SegmentedPolishTests {

    @Test func splitBoundaryAndEmptyInput() {
        #expect(SegmentedPolish.parts(of: "") == [""])
        let single = "  " + String(repeating: "我", count: 400) + "\n"
        #expect(!SegmentedPolish.needsParts(single))
        #expect(SegmentedPolish.parts(of: single) == [single])
        let long = String(repeating: "我", count: 401)
        let parts = SegmentedPolish.parts(of: " \n" + long + "\n ")
        #expect(parts.map { WordCount.count($0) } == [201, 200])
        #expect(parts.joined() == long)
    }

    @Test func splitKeepsClosingPunctuationAndNumericClauses() {
        let sentences = "“第一句！”  （第二句？）\n第三句。"
        #expect(SegmentedPolish.split(sentences, at: SegmentedPolish.isSentenceEnd) == ["“第一句！”  ", "（第二句？）\n", "第三句。"])
        let clauses = "At 10:30, use 1,000 items: version 3.5, please"
        #expect(SegmentedPolish.split(clauses, at: SegmentedPolish.isClauseEnd) == ["At 10:30, ", "use 1,000 items: ", "version 3.5, ", "please"])
    }

    @Test func blankPartsDoNotLoseParagraphBoundaries() {
        #expect(SegmentedPolish.join(["  ", "Intro.", "", "one\ntwo", " \n ", "Next.", "Last."]) == "Intro.\n\none\ntwo\n\nNext. Last.")
        #expect(SegmentedPolish.join(["", " \n"]) == "")
        #expect(SegmentedPolish.join(["오늘은", "좋아요."]) == "오늘은 좋아요.")
    }
}
