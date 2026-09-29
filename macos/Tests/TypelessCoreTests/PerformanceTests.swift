import Foundation
import Testing
@testable import TypelessCore

/// Time budgets for the work that runs while the user dictates (on the audio thread, every 40 ms) and for work the app
/// does on the main thread, where anything slow freezes the window, the menu and the recording capsule.
///
/// Off by default: timings only mean something in an optimised build, so `scripts/perf.sh` runs them in release (CI
/// does too, and puts the results on the run's summary page). The inputs are sized so that work growing faster than
/// the input takes seconds instead of milliseconds, and budgets leave a wide margin for slow, shared CI machines.
/// The Windows suite (windows/tests/TypelessCore.Tests/PerformanceTests.cs) checks the same cases with the same numbers.
/// Speech-to-text is answered by `MockOpenRouter`, which the pipeline tests also set: run this suite on its own
/// (`scripts/perf.sh` filters for it).
@Suite(.enabled(if: Performance.isEnabled), .serialized)
struct PerformanceTests {
    /// The audio thread's work for each 40 ms block of a 20-minute dictation: hand the samples on through the sink,
    /// append them to the WAV file and the transcription pipeline, and measure the level for the capsule. The pipeline
    /// cuts chunks, follows the pauses and, since the audio pauses every 2 s, transcribes the tail ahead of time about
    /// 600 times, all answered at once by the mock server while the blocks keep coming.
    @Test func liveAudioKeepsUpWithTheMicrophone() async throws {
        let audio = Performance.speech(minutes: 20)
        MockOpenRouter.handler = { _, _ in (200, Data(#"{"text":"ok"}"#.utf8)) }
        // Twice, keeping the better result: a block that is slow by nature is slow both times, while a hiccup of a
        // shared CI machine rarely hits twice.
        var slowest = Double.infinity, total = Double.infinity
        for _ in 0..<2 {
            let run = try await dictate(audio)
            XCTAssertTrue(run.chunks >= 40)
            slowest = min(slowest, run.slowest)
            total = min(total, run.total)
        }
        // The microphone fills the next block 40 ms later; half of that leaves room for the rest of the thread's work.
        Performance.check("Live audio: slowest 40 ms block", slowest, budget: 20)
        Performance.check("Live audio: 20 minutes, all blocks", total, budget: 1500)

        func dictate(_ audio: [Int16]) async throws -> (slowest: Double, total: Double, chunks: Int) {
            let block = AudioFormat.sampleCount(forSeconds: 0.04)
            let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
            defer { try? FileManager.default.removeItem(at: url) }
            let writer = try WAVFileWriter(url: url)
            let pipeline = TranscriptionPipeline(client: APIClient(endpoint: .openRouter(apiKey: "k"), session: MockOpenRouter.session()),
                                                 options: .init(model: "m"))
            let sink = AudioSink()
            sink.begin { samples in
                writer.append(samples)
                pipeline.append(samples)
            }
            var slowest = 0.0
            let total = Performance.time {
                var offset = 0
                while offset + block <= audio.count {
                    let samples = Array(audio[offset..<(offset + block)])
                    slowest = max(slowest, Performance.time {
                        sink.deliver(samples)
                        _ = AudioLevel.rms(samples)
                    })
                    offset += block
                }
            }
            sink.end()
            writer.close()
            _ = try await pipeline.finish()
            return (slowest, total, pipeline.completedTranscripts().count)
        }
    }

    /// Re-transcribing a saved recording hands all of it to the chunker at once, on the main thread. Cutting every
    /// 0.25–0.5 s instead of 18–28 s makes over 3,000 chunks out of 20 minutes, so any per-chunk work that grows with
    /// the recording (as removing each chunk from the front of the audio did) costs seconds.
    @Test func reTranscribingALongRecordingIsLinear() {
        let audio = Performance.speech(minutes: 20)
        var chunks = 0
        Performance.measure("Re-transcribe: chunk 20 minutes at once", budget: 300) {
            chunks = Chunker(config: .init(minSeconds: 0.25, maxSeconds: 0.5, windowSeconds: 0.05)).append(audio).count
        }
        XCTAssertTrue(chunks >= 2400)
    }

    /// When the user fixes a word in dictated text, the fix is found by comparing the whole dictation before and after,
    /// on the main thread. A 20-minute dictation is about 3,000 English words or 5,000 Chinese characters.
    @Test func learningFromAFixInALongDictation() {
        let words = ["the", "quick", "brown", "fox", "jumps", "over", "a", "lazy", "dog", "while", "we", "talk",
                     "about", "shipping", "the", "next", "release", "on", "time"]
        var english = (0..<3000).map { words[($0 * 7 + $0 / 3) % words.count] }
        english[1500] = "TypeList"
        let inserted = english.joined(separator: " ")
        english[1500] = "Typeless"
        let edited = english.joined(separator: " ")
        var learned: [String] = []
        Performance.measure("Learn from a fix: 20-minute English dictation", budget: 50) {
            learned = CorrectionLearner.review(original: inserted, edited: edited).learnable.map(\.corrected)
        }
        XCTAssertEqual(learned, ["Typeless"])

        let characters = Array("我们今天讨论一下这个项目的进展情况以及下一步的计划安排和具体细节")
        let chinese = String((0..<5000).map { characters[($0 * 7 + $0 / 3) % characters.count] })
        let middle = chinese.index(chinese.startIndex, offsetBy: 2500)
        let heard = String(chinese[..<middle]) + "罗级鼠标" + chinese[middle...]
        let fixed = String(chinese[..<middle]) + "罗技鼠标" + chinese[middle...]
        var skipped: String?
        Performance.measure("Learn from a fix: 20-minute Chinese dictation", budget: 50) {
            skipped = CorrectionLearner.review(original: heard, edited: fixed).skipped
        }
        XCTAssertNil(skipped)
    }

    /// Everything the Home page computes each time it's drawn (on macOS also while the pointer moves over the
    /// activity grid), after five years of dictating every day.
    @Test func homePageStatsAfterYearsOfUse() {
        let today = CalendarDay(year: 2026, month: 9, day: 29)
        var ledger = UsageLedger()
        for day in 0..<(5 * 365) {
            let date = today.adding(days: -day)
            for dictation in 0..<(1 + day % 6) {
                ledger.addDictation(on: date, words: 20 + (day * 13 + dictation * 7) % 400, seconds: 30)
            }
            ledger.addCost(on: date, transcription: 0.001, cleanup: 0.002, unpriced: 0)
        }
        var cells = 0
        Performance.measure("Home page: stats for five years of use", budget: 16) {
            _ = ledger.totals()
            _ = ledger.today(today)
            _ = ledger.month(today)
            _ = ledger.streaks(today: today)
            cells = ledger.heatmap(today: today, weeks: 53, firstWeekday: 0).count * 7
        }
        XCTAssertEqual(cells, 53 * 7)
    }
}

enum Performance {
    static let isEnabled = ProcessInfo.processInfo.environment["OPENTYPELESS_PERF"] == "1"

    /// Runs `work` a few times and checks the fastest run against `budget` (ms): that's what the code costs, the
    /// slower runs are the machine (other jobs on a shared runner, first-run warm-up).
    static func measure(_ name: String, budget: Double, runs: Int = 3, sourceLocation: SourceLocation = #_sourceLocation,
                        _ work: () -> Void) {
        let best = (0..<runs).map { _ in time(work) }.min()!
        check(name, best, budget: budget, sourceLocation: sourceLocation)
    }

    /// Records a timing (ms) for the summary (`OPENTYPELESS_PERF_REPORT`) and fails the test when it's over budget.
    static func check(_ name: String, _ milliseconds: Double, budget: Double, sourceLocation: SourceLocation = #_sourceLocation) {
        if let path = ProcessInfo.processInfo.environment["OPENTYPELESS_PERF_REPORT"], !path.isEmpty,
           let file = FileHandle(forWritingAtPath: path) {
            file.seekToEndOfFile()
            file.write(Data(String(format: "%@\t%.3f\t%.0f\n", name, milliseconds, budget).utf8))
            try? file.close()
        }
        #expect(milliseconds <= budget, "\(name): \(String(format: "%.1f", milliseconds)) ms, budget \(Int(budget)) ms",
                sourceLocation: sourceLocation)
    }

    /// Milliseconds `work` takes.
    static func time(_ work: () -> Void) -> Double {
        let start = DispatchTime.now().uptimeNanoseconds
        work()
        return Double(DispatchTime.now().uptimeNanoseconds - start) / 1_000_000
    }

    /// Speech-like audio: a tone that pauses for 0.4 s every 2 s, so the chunker finds a quiet spot to cut at.
    static func speech(minutes: Int) -> [Int16] {
        let count = AudioFormat.sampleCount(forSeconds: Double(minutes * 60))
        let period = AudioFormat.sampleCount(forSeconds: 2), pause = AudioFormat.sampleCount(forSeconds: 0.4)
        return (0..<count).map { i in i % period < pause ? 0 : Int16(8000 * sin(Double(i) * 0.3)) }
    }
}
