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
    /// saves the record's `counted` flag and a retry doesn't count it again.
    @discardableResult
    func countIfFinished(_ record: inout DictationRecord) -> Bool {
        guard record.counted != true, record.status == .done || record.status == .polishFailed else { return false }
        let words = WordCount.count(record.finalText)
        guard words > 0 else { return false }
        ledger.addDictation(on: CalendarDay(record.date), words: words, seconds: record.duration)
        record.counted = true
        return true
    }

    /// Records one dictation's processing: its cost today, and its words if it just finished.
    func record(_ record: inout DictationRecord, transcriptionCost: Double, cleanupCost: Double, unpriced: Int) {
        countIfFinished(&record)
        ledger.addCost(on: CalendarDay(Date()), transcription: transcriptionCost, cleanup: cleanupCost, unpriced: unpriced)
        ledger.save(to: Self.fileURL)
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
