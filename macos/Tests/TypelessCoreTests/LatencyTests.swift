import Foundation
import Testing
@testable import TypelessCore

/// Trimming silence and noticing pauses, checked against the cases shared with the Windows tests.
@Suite struct VoiceActivityTests {
    /// testdata/voice-activity-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/voice-activity-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    /// The audio a shared case describes (see the file's comment).
    static func signal(_ segments: [[String: Any]]) -> [Int16] {
        var samples: [Int16] = []
        var state = 1
        for segment in segments {
            let count = AudioFormat.sampleCount(forSeconds: (segment["seconds"] as! NSNumber).doubleValue)
            let amplitude = (segment["amplitude"] as? NSNumber)?.doubleValue ?? 0
            for i in 0..<count {
                switch segment["kind"] as! String {
                case "tone":
                    samples.append(Int16(amplitude * sin(Double(i) * 0.2)))
                case "noise":
                    state = (state * 1_103_515_245 + 12345) % 2_147_483_648
                    samples.append(Int16(amplitude * (2.0 * Double(state) / 2_147_483_648 - 1)))
                default:
                    samples.append(0)
                }
            }
        }
        return samples
    }

    @Test func trimmingMatchesSharedCases() throws {
        for item in try Self.cases()["trim"] as! [[String: Any]] {
            let (start, end) = VoiceActivity.speechBounds(Self.signal(item["segments"] as! [[String: Any]]))
            #expect(start == item["start"] as! Int && end == item["end"] as! Int, "\(item["name"]!): got \(start)…\(end)")
        }
    }

    @Test func pausesMatchSharedCases() throws {
        for item in try Self.cases()["pauses"] as! [[String: Any]] {
            let name = item["name"] as! String
            let tracker = PauseTracker()
            let audio = Self.signal(item["segments"] as! [[String: Any]])
            // Delivered in uneven blocks, as a microphone does.
            for i in stride(from: 0, to: audio.count, by: 1234) { tracker.append(Array(audio[i..<min(i + 1234, audio.count)])) }
            #expect(tracker.sampleCount == audio.count)
            #expect(tracker.lastSpeechEnd() == item["lastSpeechEnd"] as? Int, "\(name)")
            for check in item["speechAfter"] as! [[String: Any]] {
                let actual = tracker.speechAfter(check["sample"] as! Int, includePartial: check["includePartial"] as! Bool)
                #expect(actual == check["expected"] as! Bool, "\(name): speech after \(check["sample"]!)")
            }
        }
    }

    @Test func forgettingCutAudioIgnoresItsSpeech() {
        let tracker = PauseTracker()
        tracker.append(LatencyAudio.tone(1))
        tracker.append(LatencyAudio.silence(1))
        tracker.forget(before: AudioFormat.sampleCount(forSeconds: 1.2))
        #expect(tracker.lastSpeechEnd() == nil)
        #expect(tracker.sampleCount == AudioFormat.sampleCount(forSeconds: 2))
    }

    @Test func backupCleanUpStartsInTheSlowTail() {
        // gemini-3.1-flash-lite's first token: 0.41 s at the median, 0.49 s at p90 (eval/README.md).
        #expect((0.5...0.6).contains(HedgedPolish.defaultHedgeDelay))
    }
}

enum LatencyAudio {
    static func tone(_ seconds: Double) -> [Int16] {
        (0..<AudioFormat.sampleCount(forSeconds: seconds)).map { Int16(6000 * sin(Double($0) * 0.2)) }
    }

    static func silence(_ seconds: Double) -> [Int16] {
        [Int16](repeating: 0, count: AudioFormat.sampleCount(forSeconds: seconds))
    }

    /// Feeds audio in 0.1 s blocks, like the microphone.
    static func feed(_ pipeline: TranscriptionPipeline, _ audio: [Int16]) {
        let block = AudioFormat.sampleCount(forSeconds: 0.1)
        for i in stride(from: 0, to: audio.count, by: block) { pipeline.append(Array(audio[i..<min(i + block, audio.count)])) }
    }
}

/// Thread-safe list for what a callback or the mock server saw.
final class Recorded<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T] = []
    func add(_ item: T) { lock.withLock { items.append(item) } }
    var all: [T] { lock.withLock { items } }
}

/// Resumes a waiting test once, from any thread.
final class Signal<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: T?
    private var waiter: CheckedContinuation<T, Never>?

    func send(_ newValue: T) {
        let waiting = lock.withLock { () -> CheckedContinuation<T, Never>? in
            guard value == nil else { return nil }
            value = newValue
            defer { waiter = nil }
            return waiter
        }
        waiting?.resume(returning: newValue)
    }

    func wait() async -> T {
        await withCheckedContinuation { continuation in
            let ready = lock.withLock { () -> T? in
                if value == nil { waiter = continuation }
                return value
            }
            if let ready { continuation.resume(returning: ready) }
        }
    }
}

// What shortens the wait after the key is released. Shares MockOpenRouter.handler with the pipeline tests, so it runs
// in the same serialized suite.
extension PipelineTests {
    /// Answers each transcription with the number of samples it was sent, and records those numbers.
    private func answerWithSampleCounts(delay: TimeInterval = 0) -> Recorded<Int> {
        let sent = Recorded<Int>()
        MockOpenRouter.handler = { _, body in
            let json = try! JSONSerialization.jsonObject(with: body) as! [String: Any]
            let data = (json["input_audio"] as! [String: String])["data"]!
            let samples = WAV.decodeSamples(Data(base64Encoded: data)!)!
            sent.add(samples.count)
            return (200, try! JSONSerialization.data(withJSONObject: ["text": "[\(samples.count)]"]))
        }
        MockOpenRouter.delay = { _ in delay }
        return sent
    }

    private func client() -> APIClient {
        APIClient(endpoint: .openRouter(apiKey: "k"), session: MockOpenRouter.session())
    }

    @Test func silenceIsTrimmedBeforeSending() async throws {
        let sent = answerWithSampleCounts()
        defer { MockOpenRouter.delay = nil }
        let audio = LatencyAudio.silence(1) + LatencyAudio.tone(2) + LatencyAudio.silence(1)
        let pipeline = TranscriptionPipeline(client: client(), options: .init(model: "m"), speculate: false)
        pipeline.append(audio)
        _ = try await pipeline.finish()
        let (start, end) = VoiceActivity.speechBounds(audio)
        #expect(sent.all == [end - start])
        #expect(end - start < audio.count - AudioFormat.sampleCount(forSeconds: 1))
    }

    @Test func speculativeTailAnswersWhenTheSpeakerPausedBeforeRelease() async throws {
        // Every request takes 0.6 s; the speaker stops talking, waits, then lets go of the key.
        let sent = answerWithSampleCounts(delay: 0.6)
        defer { MockOpenRouter.delay = nil }
        let announced = Signal<String>()
        let pipeline = TranscriptionPipeline(client: client(), options: .init(model: "m"),
                                             onSpeculation: { text in if let text { announced.send(text) } })
        LatencyAudio.feed(pipeline, LatencyAudio.tone(3) + LatencyAudio.silence(0.6))
        let speculative = await announced.wait()

        let start = Date()
        let text = try await pipeline.finish()
        // Nothing was left to send: the text was there at once, not a request (0.6 s) later.
        #expect(Date().timeIntervalSince(start) < 0.4)
        #expect(pipeline.tailWasSpeculative)
        #expect(text == speculative)
        #expect(sent.all.count == 1)
    }

    @Test func releaseWhileTheSpeculationIsOutWaitsForIt() async throws {
        let sent = answerWithSampleCounts(delay: 0.5)
        defer { MockOpenRouter.delay = nil }
        let pipeline = TranscriptionPipeline(client: client(), options: .init(model: "m"))
        LatencyAudio.feed(pipeline, LatencyAudio.tone(3) + LatencyAudio.silence(0.4))
        let text = try await pipeline.finish()
        #expect(pipeline.tailWasSpeculative)
        #expect(sent.all.count == 1)
        #expect(text == "[\(sent.all.first ?? -1)]")
    }

    @Test func speakingAgainDropsTheSpeculation() async throws {
        let sent = answerWithSampleCounts(delay: 0.2)
        defer { MockOpenRouter.delay = nil }
        let announcements = Recorded<String?>()
        let pipeline = TranscriptionPipeline(client: client(), options: .init(model: "m"),
                                             onSpeculation: { announcements.add($0) })
        LatencyAudio.feed(pipeline, LatencyAudio.tone(2) + LatencyAudio.silence(0.5))
        // Long enough for the speculation to come back and be announced.
        try await Task.sleep(nanoseconds: 700_000_000)
        LatencyAudio.feed(pipeline, LatencyAudio.tone(1))
        let text = try await pipeline.finish()

        // The tail was sent again, with the words after the pause.
        let counts = sent.all
        #expect(!pipeline.tailWasSpeculative)
        #expect(counts.count == 2)
        #expect(counts.count == 2 && counts[1] > counts[0] + AudioFormat.sampleCount(forSeconds: 0.9))
        #expect(text == "[\(counts.last ?? -1)]")
        #expect(announcements.all == ["[\(counts.first ?? -1)]", nil])
    }

    @Test func speculationCoversEarlierChunks() async throws {
        _ = answerWithSampleCounts()
        defer { MockOpenRouter.delay = nil }
        let announced = Signal<String>()
        let pipeline = TranscriptionPipeline(client: client(), options: .init(model: "m"),
                                             onSpeculation: { text in if let text, text.contains("][") { announced.send(text) } })
        // 34 s of speech is cut once (at a pause, 18–28 s in); the rest is the tail.
        let speech = (0..<AudioFormat.sampleCount(forSeconds: 34)).map { i -> Int16 in
            AudioFormat.seconds(forSampleCount: i).truncatingRemainder(dividingBy: 7) < 0.2 ? 0 : Int16(6000 * sin(Double(i) * 0.2))
        }
        LatencyAudio.feed(pipeline, speech + LatencyAudio.silence(0.5))
        let speculative = await announced.wait()
        #expect(try await pipeline.finish() == speculative)
        #expect(pipeline.tailWasSpeculative)
    }

    @Test func noSpeculationWithoutSpeech() async throws {
        let sent = answerWithSampleCounts()
        defer { MockOpenRouter.delay = nil }
        let pipeline = TranscriptionPipeline(client: client(), options: .init(model: "m"))
        LatencyAudio.feed(pipeline, LatencyAudio.silence(2))
        #expect(try await pipeline.finish() == "")
        #expect(sent.all.isEmpty)
        #expect(!pipeline.tailWasSpeculative)
    }

    @Test func cancelStopsTheSpeculation() async throws {
        _ = answerWithSampleCounts(delay: 5)
        defer { MockOpenRouter.delay = nil }
        let pipeline = TranscriptionPipeline(client: client(), options: .init(model: "m"))
        LatencyAudio.feed(pipeline, LatencyAudio.tone(2) + LatencyAudio.silence(0.5))
        pipeline.cancel()
        let start = Date()
        await #expect(throws: (any Error).self) { try await pipeline.finish() }
        #expect(Date().timeIntervalSince(start) < 2)
    }

    @Test func preconnectWarmsEachHostOnceWithoutTheKey() async throws {
        let requests = Recorded<URLRequest>()
        MockOpenRouter.handler = { request, _ in
            requests.add(request)
            return (404, Data())
        }
        let now = Recorded<Double>()
        now.add(100)
        let preconnector = Preconnector(minimumInterval: 20, clock: { now.all.reduce(0, +) })
        let session = MockOpenRouter.session()
        let openRouter = APIClient(endpoint: .openRouter(apiKey: "k"), session: session)
        let groq = APIClient(endpoint: ProviderEndpoint(id: .groq, baseURL: URL(string: ProviderID.groq.defaultBaseURL)!, apiKey: "g"),
                             session: session)

        // Speech-to-text and clean-up on the same host share one connection; a missing route is skipped.
        for task in preconnector.warm([openRouter, groq, openRouter, nil]) { await task.value }
        #expect(requests.all.count == 2)
        #expect(requests.all.allSatisfy { $0.httpMethod == "HEAD" && $0.value(forHTTPHeaderField: "Authorization") == nil })
        #expect(requests.all.contains { $0.url?.absoluteString == "https://openrouter.ai/api/v1" })

        now.add(5) // warmed moments ago
        #expect(preconnector.warm([openRouter, groq]).isEmpty)
        now.add(20)
        for task in preconnector.warm([openRouter]) { await task.value }
        #expect(requests.all.count == 3)
    }
}
