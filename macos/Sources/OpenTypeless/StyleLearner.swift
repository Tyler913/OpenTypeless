import Foundation
import os
import TypelessCore

/// Learns how the user writes (see `StyleLearning`) while "Learn my writing style" is on: keeps the corrections made to
/// pasted text, counts the dictations that finish, and asks the clean-up model for a new note on Learn now and after every
/// `StyleLearning.relearnEvery` of them. The note replaces the old one only once it passes the guard, and an automatic run
/// leaves a note edited by hand alone.
///
/// The corrections are kept in `style-edits.json` in the data folder (the newest 30, each side cut to 500 characters),
/// only while the switch is on; switching it off or Clear deletes them. They and History's texts go only to the clean-up
/// provider the user chose, and only when a note is learned.
@MainActor
final class StyleLearner: ObservableObject {
    static let shared = StyleLearner()

    @Published private(set) var isLearning = false
    /// Why the last run gave no new note, shown on the page until the next one; nil when it gave one.
    @Published private(set) var problem: String?
    /// OpenRouter's metadata for a model, so reasoning is switched off as for a clean-up (set by SessionController).
    var modelInfo: ((String) -> APIClient.ModelInfo?)?
    /// Shows a spending reminder the request brought due (set by SessionController).
    var onSpendReminder: ((SpendReminder) -> Void)?

    private let settings = AppSettings.shared
    private let log = Logger(subsystem: "OpenTypeless", category: "style")
    private static var editsURL: URL { AppPaths.support.appendingPathComponent("style-edits.json") }

    private init() {}

    /// A correction the user made to a pasted text (see EditWatcher): kept while the switch is on.
    func recordEdit(inserted: String, edited: String) {
        guard settings.learnStyle, let edit = StyleLearning.Edit(before: inserted, after: edited) else { return }
        saveEdits(StyleLearning.adding(edit, to: loadEdits()))
    }

    /// A dictation was counted once it finished: one more toward the next automatic run, if it's one to learn from.
    func dictationFinished(_ record: DictationRecord) {
        guard settings.learnStyle, Self.isEligible(record) else { return }
        settings.styleNewDictations += 1
        if StyleLearning.isDue(newDictations: settings.styleNewDictations, note: settings.styleNote,
                               learnedNote: settings.styleLearnedNote) {
            learn(automatic: true)
        }
    }

    func learnNow() { learn(automatic: false) }

    /// Empties the note and forgets the corrections kept; automatic runs start over.
    func clear() {
        settings.styleNote = ""
        settings.styleLearnedNote = ""
        settings.styleNewDictations = 0
        problem = nil
        deleteEdits()
    }

    /// The switch went off: nothing more is learned or used, and the corrections kept for it are deleted. The note stays,
    /// for when it's switched on again (Clear empties it).
    func switchedOff() {
        problem = nil
        deleteEdits()
    }

    private static func isEligible(_ record: DictationRecord) -> Bool {
        StyleLearning.isEligible(status: record.status.rawValue, mode: record.mode, isFile: record.isFile, text: record.finalText)
    }

    private func learn(automatic: Bool) {
        guard !isLearning else { return }
        // Counted from this run on, whatever comes of it, so a provider that's down isn't asked after every dictation.
        settings.styleNewDictations = 0
        guard let endpoint = settings.polishEndpoint, settings.isConfigured(settings.polishProvider) else {
            problem = APIError.missingAPIKey(settings.polishProvider.displayName).localizedDescription
            return
        }
        let texts = HistoryStore.shared.records.filter(Self.isEligible).map(\.finalText)
        guard let sample = StyleLearning.sample(texts: texts, edits: loadEdits()) else {
            problem = L("可以学习的文字还不够，多听写一些再试", "Not enough texts to learn from yet. Dictate a little more first")
            return
        }
        let provider = settings.polishProvider
        let model = settings.polishModel.trimmingCharacters(in: .whitespaces)
        let options = PolishOptions(model: model, modelInfo: provider == .openrouter ? modelInfo?(model) : nil,
                                    host: provider == .openrouter ? settings.openRouterHost(for: model) : nil)
        let noteBefore = settings.styleNote
        isLearning = true
        problem = nil
        Task {
            defer { isLearning = false }
            do {
                let answer = try await APIClient(endpoint: endpoint).learnStyle(sample: sample, options: options)
                account(answer.usage, provider: provider, model: model)
                // Switched off meanwhile; or, for an automatic run, the note edited while it ran: that edit stands.
                guard settings.learnStyle, !automatic || settings.styleNote.unicodeScalars.elementsEqual(noteBefore.unicodeScalars) else { return }
                let verdict: StyleLearning.Verdict = answer.truncated ? .rejected(.tooLong) : StyleLearning.check(answer.text)
                switch verdict {
                case let .note(note):
                    settings.styleNote = note
                    settings.styleLearnedNote = note
                    log.debug("style note learned: \(note.count) characters")
                case .rejected(.none):
                    problem = L("还看不出明显的写作习惯，原来的说明保持不变", "No clear habits yet. The note is unchanged")
                case let .rejected(reason):
                    log.debug("style note rejected: \(reason.rawValue, privacy: .public)")
                    problem = L("这次没有得到可用的风格说明，原来的保持不变", "No usable note came back. The note is unchanged")
                }
            } catch {
                if APIError.from(error) == .cancelled { return }
                problem = L("未能学习：", "Couldn't learn: ") + error.localizedDescription
            }
        }
    }

    /// Adds the request to the spend, priced like a clean-up.
    private func account(_ usage: RequestUsage?, provider: ProviderID, model: String) {
        guard let usage else { return }
        let cost = CostEstimator.chat(usage, price: PriceStore.shared.price(provider, model), preferReported: provider == .openrouter)
        if let reminder = UsageStore.shared.recordCleanUp(cost: cost ?? 0, unpriced: cost == nil ? 1 : 0) {
            onSpendReminder?(reminder)
        }
    }

    // MARK: The corrections kept

    private func loadEdits() -> [StyleLearning.Edit] {
        (try? Data(contentsOf: Self.editsURL)).map(StyleLearning.Edit.list(from:)) ?? []
    }

    private func saveEdits(_ edits: [StyleLearning.Edit]) {
        guard let data = try? JSONEncoder().encode(edits) else { return }
        try? data.write(to: Self.editsURL, options: .atomic)
    }

    private func deleteEdits() {
        try? FileManager.default.removeItem(at: Self.editsURL)
    }
}
