import Foundation
import Testing
@testable import TypelessCore

/// What History deletes: retention by age, and the cap on the recordings' total size.
@Suite struct HistoryPruningTests {
    private let now = Date(timeIntervalSince1970: 1_790_000_000)
    private let day: TimeInterval = 24 * 60 * 60
    private let month: TimeInterval = 30 * 24 * 60 * 60

    private func entry(_ kind: HistoryPruning.Kind, daysAgo: Double, bytes: Int64 = 1_000) -> HistoryPruning.Entry {
        HistoryPruning.Entry(kind: kind, date: now.addingTimeInterval(-daysAgo * day), audioBytes: bytes)
    }

    @Test func finishedDictationsLoseTheRecordingThenTheText() {
        var entries = [entry(.finished, daysAgo: 1), entry(.finished, daysAgo: 31), entry(.finished, daysAgo: 32, bytes: 0)]
        #expect(HistoryPruning.plan(entries, maxAge: month, now: now) == [.keep, .removeAudio, .keep])
        // Past the newest 200 an expired one goes entirely; "Forever" never expires anything by age.
        entries = (0..<201).map { _ in entry(.finished, daysAgo: 40) }
        let actions = HistoryPruning.plan(entries, maxAge: month, now: now)
        #expect(actions[199] == .removeAudio && actions[200] == .remove)
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now).allSatisfy { $0 == .keep })
    }

    @Test func cancelledDictationsGoAfterADayWhateverTheSetting() {
        let entries = [entry(.cancelled, daysAgo: 0.5), entry(.cancelled, daysAgo: 1.1)]
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now) == [.keep, .remove])
    }

    @Test func failedDictationsExpireAfterAtLeastAMonth() {
        #expect(HistoryPruning.failedMaxAge(retention: 0) == HistoryPruning.failedMinimumAge)
        #expect(HistoryPruning.failedMaxAge(retention: 365 * day) == 365 * day)
        #expect(HistoryPruning.failedMaxAge(retention: nil) == nil)
        let entries = [entry(.failed, daysAgo: 29), entry(.failed, daysAgo: 31)]
        // "Don't keep recordings" still leaves a failed one a month to be re-sent.
        #expect(HistoryPruning.plan(entries, maxAge: 0, now: now) == [.keep, .removeAudio])
        #expect(HistoryPruning.plan(entries, maxAge: 365 * day, now: now) == [.keep, .keep])
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now) == [.keep, .keep])
        let old = (0..<201).map { _ in entry(.failed, daysAgo: 40) }
        #expect(HistoryPruning.plan(old, maxAge: month, now: now)[200] == .remove)
    }

    @Test func recordingsOverTheBudgetGoOldestFirst() {
        // 15 dictations of 100 bytes against a 1,000-byte budget: the five oldest go, newest kept.
        let entries = (0..<15).map { entry(.finished, daysAgo: Double($0) * 0.01, bytes: 100) }
        let actions = HistoryPruning.plan(entries, maxAge: nil, now: now, audioBudget: 1_000)
        #expect(actions == Array(repeating: .keep, count: 10) + Array(repeating: .removeAudio, count: 5))
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now, audioBudget: 1_500).allSatisfy { $0 == .keep })
    }

    @Test func budgetSparesTheNewestAndUnfinishedAndFailedDictations() {
        var entries = (0..<12).map { entry(.finished, daysAgo: Double($0) * 0.01, bytes: 500) }
        entries[0].kind = .inProgress
        entries[10].kind = .failed
        entries[11].kind = .inProgress
        // Past the newest ten nothing is finished, so nothing can go, however far over the budget.
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now, audioBudget: 100).allSatisfy { $0 == .keep })

        // A cancelled one can go too; recordings already gone don't count, nor does one that expires by age anyway.
        entries = [entry(.finished, daysAgo: 0)] + (1..<10).map { _ in entry(.finished, daysAgo: 0.1, bytes: 0) }
            + [entry(.cancelled, daysAgo: 0.5, bytes: 600), entry(.finished, daysAgo: 2, bytes: 600), entry(.finished, daysAgo: 40, bytes: 600)]
        func plan(budget: Int64) -> [HistoryPruning.Action] {
            HistoryPruning.plan(entries, maxAge: month, now: now, audioBudget: budget)
        }
        let newest = [HistoryPruning.Action](repeating: .keep, count: 10)
        #expect(plan(budget: 2_200) == newest + [.keep, .keep, .removeAudio])
        #expect(plan(budget: 1_700) == newest + [.keep, .removeAudio, .removeAudio])
        #expect(plan(budget: 1_000) == newest + [.removeAudio, .removeAudio, .removeAudio])
        // The newest ten keep theirs even when they alone are over it.
        #expect(plan(budget: 500) == newest + [.removeAudio, .removeAudio, .removeAudio])
    }
}

extension HistoryPruningTests {

    @Test func defaultGigabyteBudgetIsInclusiveAndUsesWideTotals() {
        var entries = (0..<10).map { _ in entry(.finished, daysAgo: 0, bytes: 90_000_000) }
        entries.append(entry(.finished, daysAgo: 1, bytes: 100_000_000))
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now).allSatisfy { $0 == .keep })
        entries[10].audioBytes += 1
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now) == Array(repeating: .keep, count: 10) + [.removeAudio])
        entries.append(entry(.finished, daysAgo: 2, bytes: 3_000_000_000))
        #expect(HistoryPruning.plan(entries, maxAge: nil, now: now) == Array(repeating: .keep, count: 10) + [.removeAudio, .removeAudio])
    }

    @Test func failedExpiryBoundaryAndMissingAudioStillRespectTextFloor() {
        var entries = (0..<201).map { _ in entry(.failed, daysAgo: 30, bytes: 0) }
        #expect(HistoryPruning.plan(entries, maxAge: 0, now: now) == Array(repeating: .keep, count: 200) + [.remove])
        entries[199].audioBytes = 1
        entries[200].date = now.addingTimeInterval(-month + 1)
        let actions = HistoryPruning.plan(entries, maxAge: 0, now: now)
        #expect(actions[199] == .removeAudio && actions[200] == .keep)
        entries[200] = entry(.inProgress, daysAgo: 365, bytes: 2_000_000_000)
        #expect(HistoryPruning.plan(entries, maxAge: 0, now: now)[200] == .keep)
        #expect(HistoryPruning.plan([], maxAge: 0, now: now).isEmpty)
    }
}
