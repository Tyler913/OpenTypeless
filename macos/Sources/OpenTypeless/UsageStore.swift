import Foundation
import os
import TypelessCore

/// The Home page's numbers: words, speaking time and spend per day, in `usage.json` next to the history. The ledger
/// outlives History (which drops old dictations), so totals keep growing from the day the app was installed.
@MainActor
final class UsageStore: ObservableObject {
    static let shared = UsageStore()

    @Published private(set) var ledger: UsageLedger

    private static var fileURL: URL { AppPaths.support.appendingPathComponent("usage.json") }

    private init() {
        if let loaded = UsageLedger.load(from: Self.fileURL) {
            ledger = loaded
            return
        }
        // First run of a version with the Home page: start from the dictations History still has.
        ledger = UsageLedger()
        let history = HistoryStore.shared
        for saved in history.records {
            var record = saved
            if countIfFinished(&record) { history.update(record) }
        }
        ledger.save(to: Self.fileURL)
    }

    /// Adds a finished dictation's words and speaking time once. Returns whether it was counted now, so the caller
    /// saves the record's `counted` flag and a retry doesn't count it again. A transcribed file isn't counted: its
    /// words weren't dictated, and an hour-long meeting would swamp the words and the time saved over typing. What it
    /// cost is counted all the same (`record`).
    @discardableResult
    func countIfFinished(_ record: inout DictationRecord) -> Bool {
        guard !record.isFile, record.counted != true, record.status == .done || record.status == .polishFailed else { return false }
        let words = WordCount.count(record.finalText)
        guard words > 0 else { return false }
        ledger.addDictation(on: CalendarDay(record.date), words: words, seconds: record.duration)
        record.counted = true
        return true
    }

    /// Records one dictation's processing: its cost today, and its words if it just finished. Returns the spending
    /// reminder that cost brings due, if any (see `SpendLimit`); it's saved as shown, so it comes once a month.
    @discardableResult
    func record(_ record: inout DictationRecord, transcriptionCost: Double, cleanupCost: Double, unpriced: Int) -> SpendReminder? {
        countIfFinished(&record)
        let today = CalendarDay(Date())
        let before = ledger.month(today).cost
        ledger.addCost(on: today, transcription: transcriptionCost, cleanup: cleanupCost, unpriced: unpriced)
        ledger.save(to: Self.fileURL)
        return spendReminder(today: today, spentBefore: before)
    }

    /// This calendar month's spend (what Home shows as "this month") against the monthly limit, while one is set.
    var monthlySpend: (spent: Double, limit: Double)? {
        guard let limit = AppSettings.shared.activeSpendLimit else { return nil }
        return (ledger.month(CalendarDay(Date())).cost, limit)
    }

    private func spendReminder(today: CalendarDay, spentBefore: Double) -> SpendReminder? {
        let settings = AppSettings.shared
        guard let limit = settings.activeSpendLimit else { return nil }
        let spent = ledger.month(today).cost
        let saved = settings.spendReminderState
        let outcome = SpendLimit.check(spentBefore: spentBefore, spent: spent, limit: limit,
                                       month: SpendLimit.monthKey(today), state: saved)
        if outcome.state != saved { settings.spendReminderState = outcome.state }
        return outcome.reminder.map { SpendReminder(level: $0, spent: spent, limit: limit) }
    }
}

/// A spending reminder that came due (see `SpendLimit`), shown in the capsule once the dictation is done.
struct SpendReminder: Equatable {
    let level: SpendLimit.Level
    let spent: Double
    let limit: Double

    var message: String {
        let amount = UsageFormat.money(self.spent), cap = UsageFormat.money(self.limit)
        if level == .reached {
            return L("本月花费已达上限：\(amount) / \(cap)。听写不受影响",
                     "You've reached this month's spending limit: \(amount) of \(cap). Dictation keeps working")
        }
        let percent = Int((SpendLimit.warningFraction * 100).rounded())
        return L("本月已花费 \(amount)，达到上限 \(cap) 的 \(percent)%",
                 "This month's spend is \(amount), \(percent)% of your \(cap) limit")
    }
}

/// OpenRouter's price list, kept fresh: loaded from the cached `openrouter-prices.json` at launch, downloaded again
/// when it's more than six hours old (checked hourly and whenever the Home or Models page opens).
@MainActor
final class PriceStore: ObservableObject {
    static let shared = PriceStore()

    @Published private(set) var catalog: PriceCatalog

    private static var fileURL: URL { AppPaths.support.appendingPathComponent("openrouter-prices.json") }
    private let log = Logger(subsystem: "local.opentypeless.app", category: "prices")
    private var refreshing = false
    private var timer: Timer?

    private init() {
        catalog = (try? Data(contentsOf: Self.fileURL)).flatMap(PriceCatalog.init(jsonData:)) ?? .empty
        timer = Timer.scheduledTimer(withTimeInterval: 60 * 60, repeats: true) { _ in
            Task { @MainActor in PriceStore.shared.refreshIfStale() }
        }
    }

    /// What a model costs: OpenRouter's live price, or the price the user entered for any other provider.
    func price(_ provider: ProviderID, _ model: String) -> ModelPrice? {
        provider == .openrouter ? catalog.price(model) : AppSettings.shared.customPrice(provider, model)
    }

    func refreshIfStale(maxAge: TimeInterval = 6 * 60 * 60) {
        guard !refreshing, catalog.isOlder(than: maxAge) else { return }
        refreshing = true
        Task {
            defer { refreshing = false }
            do {
                let fresh = try await PriceCatalog.fetch(baseURL: AppSettings.shared.endpoint(for: .openrouter)?.baseURL)
                try? fresh.jsonData()?.write(to: Self.fileURL, options: .atomic)
                catalog = fresh
            } catch {
                log.debug("price refresh failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }
}
