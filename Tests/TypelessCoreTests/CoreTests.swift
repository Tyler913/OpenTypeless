import Foundation
import Testing
@testable import TypelessCore

struct WAVTests {
    @Test func testRoundTrip() {
        let samples: [Int16] = [0, 1, -1, 32767, -32768, 1234]
        let wav = WAV.encode(samples: samples)
        XCTAssertEqual(wav.count, 44 + samples.count * 2)
        XCTAssertEqual(wav.readLE32(at: 24), 16_000)
        XCTAssertEqual(wav.readLE32(at: 40), UInt32(samples.count * 2))
        XCTAssertEqual(WAV.decodeSamples(wav), samples)
    }

    @Test func testFileWriterProducesValidFile() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
        defer { try? FileManager.default.removeItem(at: url) }
        let writer = try WAVFileWriter(url: url)
        writer.append([1, 2, 3])
        writer.finalize()
        writer.append([4, 5])
        writer.close()
        let data = try Data(contentsOf: url)
        XCTAssertEqual(WAV.decodeSamples(data), [1, 2, 3, 4, 5])
        XCTAssertEqual(data.readLE32(at: 4), UInt32(36 + 10))
    }
}

struct ChunkerTests {
    /// Loud "speech" with a single quiet gap at `gapAt` seconds.
    private func signal(seconds: Double, gapsAt gaps: [Double]) -> [Int16] {
        let n = AudioFormat.sampleCount(forSeconds: seconds)
        var out = [Int16](repeating: 0, count: n)
        for i in 0..<n {
            let t = AudioFormat.seconds(forSampleCount: i)
            let inGap = gaps.contains { abs(t - $0) < 0.25 }
            out[i] = inGap ? 0 : Int16(8000 * sin(Double(i) * 0.3))
        }
        return out
    }

    @Test func testCutsAtQuietestPoint() {
        let chunker = Chunker()
        let audio = signal(seconds: 70, gapsAt: [23.0, 47.5])
        var chunks: [AudioChunk] = []
        // Feed in 100 ms blocks like the audio engine does.
        let block = AudioFormat.sampleCount(forSeconds: 0.1)
        var i = 0
        while i < audio.count {
            chunks += chunker.append(Array(audio[i..<min(i + block, audio.count)]))
            i += block
        }
        if let tail = chunker.finish() { chunks.append(tail) }

        XCTAssertEqual(chunks.count, 3)
        #expect(abs((chunks[0].duration) - (23.0)) < 0.1)
        #expect(abs((chunks[1].startTime) - (23.0)) < 0.1)
        #expect(abs((chunks[1].startTime + chunks[1].duration) - (47.5)) < 0.1)
        XCTAssertEqual(chunks.map(\.index), [0, 1, 2])
        // No audio lost or duplicated.
        XCTAssertEqual(chunks.reduce(0) { $0 + $1.samples.count }, audio.count)
        XCTAssertEqual(chunks.flatMap(\.samples), audio)
    }

    @Test func testNoChunkExceedsMax() {
        let chunker = Chunker()
        let audio = signal(seconds: 125, gapsAt: [])
        var chunks = chunker.append(audio)
        if let tail = chunker.finish() { chunks.append(tail) }
        for chunk in chunks { XCTAssertLessThanOrEqual(chunk.duration, 28.0 + 0.001) }
        XCTAssertEqual(chunks.flatMap(\.samples).count, audio.count)
    }

    @Test func testShortRecordingIsSingleChunk() {
        let chunker = Chunker()
        XCTAssertTrue(chunker.append(signal(seconds: 5, gapsAt: [])).isEmpty)
        #expect(abs((chunker.finish()?.duration ?? 0) - (5)) < 0.01)
        XCTAssertNil(chunker.finish())
    }

    @Test func testSilenceDetection() {
        XCTAssertTrue(AudioLevel.isSilent([Int16](repeating: 20, count: 16000)))
        XCTAssertFalse(AudioLevel.isSilent(signal(seconds: 1, gapsAt: [])))
    }
}

struct JoinerTests {
    @Test func testJoin() {
        XCTAssertEqual(TranscriptJoiner.join(["我想做一个", "语音输入软件。"]), "我想做一个语音输入软件。")
        XCTAssertEqual(TranscriptJoiner.join(["Hello there", "general Kenobi."]), "Hello there general Kenobi.")
        XCTAssertEqual(TranscriptJoiner.join(["用 SwiftUI", "写界面"]), "用 SwiftUI写界面")
        XCTAssertEqual(TranscriptJoiner.join(["done.", " Next"]), "done. Next")
        XCTAssertEqual(TranscriptJoiner.join(["", "  a ", ""]), "a")
    }
}

struct SSEParserTests {
    @Test func testParsesContentAndDone() {
        let p = SSEParser()
        XCTAssertEqual(p.parse(line: ": OPENROUTER PROCESSING"), [])
        XCTAssertEqual(p.parse(line: ""), [])
        XCTAssertEqual(p.parse(line: #"data: {"choices":[{"delta":{"content":"你好"}}]}"#), [.content("你好")])
        XCTAssertEqual(p.parse(line: #"data: {"choices":[{"delta":{},"finish_reason":"length"}]}"#),
                       [.finished(reason: "length")])
        XCTAssertEqual(p.parse(line: "data: [DONE]"), [.done])
        if case .error = p.parse(line: #"data: {"error":{"message":"boom"}}"#).first {} else { XCTFail() }
    }
}

struct RetryTests {
    @Test func testClassification() {
        XCTAssertTrue(APIError.http(status: 429, message: "").isRetryable)
        XCTAssertTrue(APIError.http(status: 503, message: "").isRetryable)
        XCTAssertTrue(APIError.timeout(seconds: 1).isRetryable)
        XCTAssertFalse(APIError.http(status: 401, message: "").isRetryable)
        XCTAssertFalse(APIError.http(status: 402, message: "").isRetryable)
        XCTAssertFalse(APIError.missingAPIKey("x").isRetryable)
    }

    @Test func testRetriesThenSucceeds() async throws {
        let policy = RetryPolicy(maxAttempts: 3, baseDelay: 0.01)
        var calls = 0
        let value = try await policy.run { _ -> Int in
            calls += 1
            if calls < 3 { throw APIError.http(status: 502, message: "bad gateway") }
            return 42
        }
        XCTAssertEqual(value, 42)
        XCTAssertEqual(calls, 3)
    }

    @Test func testDoesNotRetryAuthErrors() async {
        let policy = RetryPolicy(maxAttempts: 3, baseDelay: 0.01)
        var calls = 0
        do {
            _ = try await policy.run { _ -> Int in calls += 1; throw APIError.http(status: 401, message: "") }
            XCTFail()
        } catch {}
        XCTAssertEqual(calls, 1)
    }

    @Test func testTimeout() async {
        do {
            _ = try await withTimeout(0.05) { try await Task.sleep(nanoseconds: 2_000_000_000); return 1 }
            XCTFail()
        } catch let error as APIError {
            XCTAssertEqual(error, .timeout(seconds: 0.05))
        } catch { XCTFail("\(error)") }
    }
}

struct PromptTests {
    @Test func testSanitize() {
        XCTAssertEqual(Prompts.sanitizePolishOutput("```\nhello\n```"), "hello")
        XCTAssertEqual(Prompts.sanitizePolishOutput("<transcript>\nhi\n</transcript>"), "hi")
    }

    @Test func testAnswerDetection() {
        XCTAssertFalse(Prompts.looksLikeAnAnswer(input: String(repeating: "嗯", count: 300), output: String(repeating: "字", count: 250)))
        XCTAssertTrue(Prompts.looksLikeAnAnswer(input: "帮我写一个快速排序", output: String(repeating: "x", count: 800)))
    }

    @Test func testReasoningConfig() {
        let mandatory = APIClient.ModelInfo(id: "g", name: "g", supportedParameters: ["reasoning"],
                                                   reasoningMandatory: true, reasoningEfforts: ["high", "low", "minimal"])
        XCTAssertEqual(ReasoningConfig.body(for: mandatory)?["effort"] as? String, "minimal")
        let optional = APIClient.ModelInfo(id: "q", name: "q", supportedParameters: ["reasoning"],
                                                  reasoningMandatory: false, reasoningEfforts: [])
        XCTAssertEqual(ReasoningConfig.body(for: optional)?["enabled"] as? Bool, false)
        let none = APIClient.ModelInfo(id: "n", name: "n", supportedParameters: [],
                                              reasoningMandatory: false, reasoningEfforts: [])
        XCTAssertNil(ReasoningConfig.body(for: none))
    }
}

// Minimal XCTest-style shims over swift-testing (the Command Line Tools ship Testing, not XCTest).
func XCTAssertEqual<T: Equatable>(_ a: T, _ b: T, sourceLocation: SourceLocation = #_sourceLocation) {
    #expect(a == b, sourceLocation: sourceLocation)
}
func XCTAssertTrue(_ v: Bool, sourceLocation: SourceLocation = #_sourceLocation) { #expect(v, sourceLocation: sourceLocation) }
func XCTAssertFalse(_ v: Bool, sourceLocation: SourceLocation = #_sourceLocation) { #expect(!v, sourceLocation: sourceLocation) }
func XCTAssertNil<T>(_ v: T?, sourceLocation: SourceLocation = #_sourceLocation) { #expect(v == nil, sourceLocation: sourceLocation) }
func XCTAssertLessThanOrEqual<T: Comparable>(_ a: T, _ b: T, sourceLocation: SourceLocation = #_sourceLocation) {
    #expect(a <= b, sourceLocation: sourceLocation)
}
func XCTFail(_ message: String = "", sourceLocation: SourceLocation = #_sourceLocation) {
    Issue.record(Comment(rawValue: message), sourceLocation: sourceLocation)
}
