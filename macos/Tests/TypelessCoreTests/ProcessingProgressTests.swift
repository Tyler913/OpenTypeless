import Foundation
import Testing
@testable import TypelessCore

struct ProcessingProgressTests {
    /// Runs the animation for `seconds` at 60 frames a second.
    private func run(_ progress: inout ProcessingProgress, _ seconds: Double) -> Double {
        for _ in 0..<Int(seconds * 60) { progress.tick(1.0 / 60) }
        return progress.shown
    }

    @Test func startsSlightlyFilledAndCreepsWithoutReachingTheTranscript() {
        var progress = ProcessingProgress(polishes: true)
        let early = run(&progress, 0.5)
        #expect(early >= ProcessingProgress.startsAt * 0.9)
        let late = run(&progress, 30)
        #expect(late > early)
        // However long speech-to-text takes, the bar waits for it.
        #expect(late < ProcessingProgress.transcribedAt)
    }

    @Test func followsTheMilestonesInOrder() {
        var progress = ProcessingProgress(polishes: true)
        progress.reach(.transcribing(done: 1, total: 2))
        let half = run(&progress, 1)
        #expect(half > 0.2 && half < ProcessingProgress.transcribedAt)
        progress.reach(.transcribed)
        #expect(run(&progress, 1) >= ProcessingProgress.transcribedAt * 0.98)
        progress.reach(.polishing(received: 5, expected: 10))
        let streaming = run(&progress, 1)
        #expect(streaming > ProcessingProgress.polishStartsAt && streaming < ProcessingProgress.polishEndsAt)
        progress.reach(.delivered)
        #expect(run(&progress, 1) > 0.99)
    }

    @Test func neverMovesBack() {
        var progress = ProcessingProgress(polishes: true)
        progress.reach(.transcribing(done: 3, total: 4))
        let before = run(&progress, 2)
        // A failed chunk sent again, or a second clean-up attempt.
        progress.reach(.transcribing(done: 2, total: 4))
        progress.reach(.polishing(received: 0, expected: 10))
        var last = before
        for _ in 0..<120 {
            let now = progress.tick(1.0 / 60)
            #expect(now >= last)
            last = now
        }
    }

    @Test func withoutCleanUpTheTranscriptFillsMostOfTheBar() {
        var progress = ProcessingProgress(polishes: false)
        progress.reach(.transcribed)
        let shown = run(&progress, 1)
        #expect(shown > 0.85 && shown < 1)
    }

    @Test func oddInputsStayInRange() {
        var progress = ProcessingProgress(polishes: true)
        progress.reach(.transcribing(done: 7, total: 0))
        progress.reach(.polishing(received: 500, expected: 3))
        #expect(run(&progress, 5) <= ProcessingProgress.polishEndsAt)
        progress.tick(.nan)
        progress.tick(-1)
        progress.reach(.delivered)
        #expect(run(&progress, 5) <= 1)
    }
}
