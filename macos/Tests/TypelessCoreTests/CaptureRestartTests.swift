import Foundation
import Testing
@testable import TypelessCore

struct CaptureRestartTests {
    @Test func theChangeAChosenInputPostsAfterStartingIsIgnored() {
        var policy = CaptureRestartPolicy()
        // Every start on a chosen input posts one; none of them may rebuild, or the microphone flickers on and off.
        for second in 0..<20 {
            #expect(policy.decide(isRunning: true, onExpectedInput: true, sameFormat: true, now: Double(second) / 10) == .keep)
        }
    }

    @Test func aRealChangeRebuilds() {
        var policy = CaptureRestartPolicy()
        #expect(policy.decide(isRunning: false, onExpectedInput: true, sameFormat: true, now: 0) == .rebuild)
        #expect(policy.decide(isRunning: true, onExpectedInput: false, sameFormat: true, now: 10) == .rebuild)
        #expect(policy.decide(isRunning: true, onExpectedInput: true, sameFormat: false, now: 20) == .rebuild)
    }

    @Test func aRunOfRebuildsIsCutShort() {
        var policy = CaptureRestartPolicy()
        for index in 0..<CaptureRestartPolicy.maximumRebuilds {
            #expect(policy.decide(isRunning: true, onExpectedInput: false, sameFormat: true, now: Double(index) * 0.2) == .rebuild)
        }
        // Still running: it keeps recording on what it has rather than restarting again.
        #expect(policy.decide(isRunning: true, onExpectedInput: false, sameFormat: true, now: 1) == .keep)
        // Stopped: nothing more will come in, so the recording fails instead of looping.
        #expect(policy.decide(isRunning: false, onExpectedInput: true, sameFormat: true, now: 1.2) == .giveUp)
    }

    @Test func rebuildsLongAgoDontCount() {
        var policy = CaptureRestartPolicy()
        for index in 0..<CaptureRestartPolicy.maximumRebuilds {
            _ = policy.decide(isRunning: false, onExpectedInput: true, sameFormat: true, now: Double(index))
        }
        #expect(policy.decide(isRunning: false, onExpectedInput: true, sameFormat: true, now: 2 + CaptureRestartPolicy.window) == .rebuild)
    }

    @Test func aNewRecordingStartsAfresh() {
        var policy = CaptureRestartPolicy()
        for index in 0..<CaptureRestartPolicy.maximumRebuilds {
            _ = policy.decide(isRunning: false, onExpectedInput: true, sameFormat: true, now: Double(index) * 0.1)
        }
        policy.reset()
        #expect(policy.decide(isRunning: false, onExpectedInput: true, sameFormat: true, now: 0.5) == .rebuild)
    }
}
