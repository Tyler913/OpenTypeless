import AppKit
import Foundation
import Security
import ServiceManagement
import SwiftUI
import TypelessCore

/// User preferences. API keys live in the keychain; everything else in UserDefaults.
final class AppSettings: ObservableObject {
    static let shared = AppSettings()

    @AppStorage(AppLanguage.defaultsKey) var appLanguage: AppLanguage = .system

    @AppStorage("hotkeyV2") private var hotkeyData = Data()
    @AppStorage("sttProvider") var sttProvider: ProviderID = .openrouter
    @AppStorage("sttModel") var sttModel: String = ProviderID.openrouter.defaultSTTModel
    @AppStorage("polishProvider") var polishProvider: ProviderID = .openrouter
    @AppStorage("polishModel") var polishModel: String = ProviderID.openrouter.defaultChatModel
    @AppStorage("polishEnabled") var polishEnabled: Bool = true
    /// A second clean-up model that races the first when it is slow to start (see HedgedPolish).
    @AppStorage("polishBackupEnabled") var polishBackupEnabled: Bool = true
    @AppStorage("polishBackupProvider") var polishBackupProvider: ProviderID = .openrouter
    @AppStorage("polishBackupModel") var polishBackupModel: String = ProviderID.openrouter.defaultBackupChatModel
    /// Spoken-language hint for STT; "" = auto-detect. (Key predates the UI language setting.)
    @AppStorage("language") var sttLanguage: String = ""
    @AppStorage("vocabulary") var vocabulary: String = "Claude, OpenRouter, SwiftUI, GitHub, Typeless"
    @AppStorage("extraInstructions") var extraInstructions: String = ""
    @AppStorage("playSounds") var playSounds: Bool = true
    @AppStorage("restoreClipboard") var restoreClipboard: Bool = true
    @AppStorage("maxRecordingMinutes") var maxRecordingMinutes: Int = 20
    @AppStorage("historyRetention") var historyRetention: HistoryRetention = .month
    /// Look for a new release once a day and download it in the background (see Updater).
    @AppStorage("autoCheckUpdates") var autoCheckUpdates: Bool = true
    /// The microphone to record from, by CoreAudio UID; "" follows the system default input.
    @AppStorage("microphone") var microphoneUID: String = ""
    /// Keep the microphone running between dictations, so recording starts at once with the moment before the key.
    @AppStorage("keepMicrophoneWarm") var keepMicrophoneWarm: Bool = false
    /// Open the Home page when the app starts at login (a manual launch always shows it).
    @AppStorage("showHomeAtLogin") var showHomeAtLogin: Bool = false
    /// The typing speed "time saved" on the Home page is measured against.
    @AppStorage("typingWordsPerMinute") var typingWordsPerMinute: Int = UsageTotals.defaultTypingWordsPerMinute

    /// Learn vocabulary from the fixes the user makes to dictated text (see EditWatcher).
    @AppStorage("learnFromEdits") var learnFromEdits: Bool = true
    @Published private(set) var learnedTerms: [LearnedTerm]
    /// Learned terms the user removed; never learned again (lowercased).
    private var forgottenTerms: Set<String>

    @Published private(set) var apiKeys: [String: String]
    @Published private(set) var baseURLs: [String: String]
    /// Prices the user entered for models of providers other than OpenRouter (whose prices are live), by `ModelPrice.key`.
    @Published private(set) var modelPrices: [String: ModelPrice]

    private init() {
        // In CLI mode with OPENROUTER_API_KEY set, don't touch the keychain (avoids an access prompt).
        let cli = !(ProcessInfo.processInfo.environment["OPENROUTER_API_KEY"] ?? "").isEmpty
        apiKeys = cli ? [:] : Credentials.load()
        baseURLs = (UserDefaults.standard.dictionary(forKey: "baseURLs") as? [String: String]) ?? [:]
        learnedTerms = UserDefaults.standard.data(forKey: "learnedVocabulary")
            .flatMap { try? JSONDecoder().decode([LearnedTerm].self, from: $0) } ?? []
        forgottenTerms = Set(UserDefaults.standard.stringArray(forKey: "forgottenVocabulary") ?? [])
        modelPrices = UserDefaults.standard.data(forKey: "modelPrices")
            .flatMap { try? JSONDecoder().decode([String: ModelPrice].self, from: $0) } ?? [:]
    }

    // MARK: Hotkey

    var hotkey: Hotkey {
        get {
            if let decoded = try? JSONDecoder().decode(Hotkey.self, from: hotkeyData) { return decoded }
            // v0.1 stored one of four preset names.
            switch UserDefaults.standard.string(forKey: "hotkey") {
            case "rightOption": return .rightOption
            case "rightCommand": return .rightCommand
            case "rightControl": return .rightControl
            default: return .fn
            }
        }
        set { hotkeyData = (try? JSONEncoder().encode(newValue)) ?? Data() }
    }

    // MARK: Providers

    func apiKey(for id: ProviderID) -> String {
        if id == .openrouter, let env = ProcessInfo.processInfo.environment["OPENROUTER_API_KEY"], !env.isEmpty {
            return env
        }
        return apiKeys[id.rawValue] ?? ""
    }

    func setAPIKey(_ key: String, for id: ProviderID) {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard apiKeys[id.rawValue, default: ""] != trimmed else { return }
        apiKeys[id.rawValue] = trimmed.isEmpty ? nil : trimmed
        Credentials.save(apiKeys)
    }

    func baseURL(for id: ProviderID) -> String {
        baseURLs[id.rawValue] ?? id.defaultBaseURL
    }

    func setBaseURL(_ url: String, for id: ProviderID) {
        let trimmed = url.trimmingCharacters(in: .whitespacesAndNewlines)
        baseURLs[id.rawValue] = (trimmed.isEmpty || trimmed == id.defaultBaseURL) ? nil : trimmed
        UserDefaults.standard.set(baseURLs, forKey: "baseURLs")
    }

    func endpoint(for id: ProviderID) -> ProviderEndpoint? {
        let raw = baseURL(for: id).trimmingCharacters(in: CharacterSet(charactersIn: "/ "))
        guard let url = URL(string: raw), let scheme = url.scheme, scheme.hasPrefix("http"), url.host != nil else {
            return nil
        }
        return ProviderEndpoint(id: id, baseURL: url, apiKey: apiKey(for: id))
    }

    func isConfigured(_ id: ProviderID) -> Bool {
        endpoint(for: id) != nil && (!id.requiresKey || !apiKey(for: id).isEmpty)
    }

    var sttEndpoint: ProviderEndpoint? { endpoint(for: sttProvider) }
    var polishEndpoint: ProviderEndpoint? { endpoint(for: polishProvider) }

    /// The backup route, when switched on, configured, and actually different from the primary.
    var polishBackupEndpoint: ProviderEndpoint? {
        let model = polishBackupModel.trimmingCharacters(in: .whitespaces)
        guard polishBackupEnabled, !model.isEmpty, isConfigured(polishBackupProvider),
              polishBackupProvider != polishProvider || model != polishModel else { return nil }
        return endpoint(for: polishBackupProvider)
    }

    /// Switching provider swaps in that provider's default model, since model IDs aren't portable.
    func selectSTTProvider(_ id: ProviderID) {
        guard id != sttProvider else { return }
        sttProvider = id
        sttModel = id.defaultSTTModel
    }

    func selectPolishProvider(_ id: ProviderID) {
        guard id != polishProvider else { return }
        polishProvider = id
        polishModel = id.defaultChatModel
    }

    func selectPolishBackupProvider(_ id: ProviderID) {
        guard id != polishBackupProvider else { return }
        polishBackupProvider = id
        polishBackupModel = id.defaultBackupChatModel
    }

    // MARK: Model prices

    func customPrice(_ provider: ProviderID, _ model: String) -> ModelPrice? {
        modelPrices[ModelPrice.key(provider, model)]
    }

    func setCustomPrice(_ provider: ProviderID, _ model: String, _ price: ModelPrice?) {
        guard !model.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        let key = ModelPrice.key(provider, model)
        if let price, !price.isEmpty { modelPrices[key] = price } else { modelPrices[key] = nil }
        UserDefaults.standard.set(try? JSONEncoder().encode(modelPrices), forKey: "modelPrices")
    }

    // MARK: Learned vocabulary

    /// Adds what the user's corrections taught to the vocabulary. Returns the terms that are new.
    @discardableResult
    func learn(_ corrections: [Correction]) -> [String] {
        var added: [String] = []
        for correction in corrections {
            let term = correction.corrected.trimmingCharacters(in: .whitespacesAndNewlines)
            let key = term.lowercased()
            guard !forgottenTerms.contains(key) else { continue }
            if let index = learnedTerms.firstIndex(where: { $0.term.lowercased() == key }) {
                if !learnedTerms[index].heardAs.contains(correction.heard) { learnedTerms[index].heardAs.append(correction.heard) }
                continue
            }
            learnedTerms.append(LearnedTerm(term: term, heardAs: [correction.heard], date: Date()))
            if !vocabularyList.contains(where: { $0.lowercased() == key }) {
                let current = vocabulary.trimmingCharacters(in: .whitespacesAndNewlines)
                vocabulary = current.isEmpty ? term : current + ", " + term
                added.append(term)
            }
        }
        saveLearnedTerms()
        return added
    }

    /// Removes a learned term from the vocabulary and makes sure it isn't learned again.
    func forget(_ learned: LearnedTerm) {
        let key = learned.term.lowercased()
        learnedTerms.removeAll { $0.term.lowercased() == key }
        forgottenTerms.insert(key)
        UserDefaults.standard.set(Array(forgottenTerms), forKey: "forgottenVocabulary")
        if vocabularyList.contains(where: { $0.lowercased() == key }) {
            vocabulary = vocabularyList.filter { $0.lowercased() != key }.joined(separator: ", ")
        }
        saveLearnedTerms()
    }

    /// Misheard forms of vocabulary terms, newest first, for the clean-up prompt.
    var misheardHints: [Correction] {
        let terms = Set(vocabularyList.map { $0.lowercased() })
        return learnedTerms.reversed()
            .filter { terms.contains($0.term.lowercased()) }
            .flatMap { learned in learned.heardAs.map { Correction(heard: $0, corrected: learned.term) } }
            .prefix(40).map { $0 }
    }

    private func saveLearnedTerms() {
        UserDefaults.standard.set(try? JSONEncoder().encode(learnedTerms), forKey: "learnedVocabulary")
    }

    var vocabularyList: [String] {
        vocabulary.split(whereSeparator: { $0 == "," || $0 == "，" || $0 == "\n" })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }
}

/// A vocabulary term learned from the user fixing a dictation, with what recognition heard instead.
struct LearnedTerm: Codable, Identifiable, Equatable {
    var term: String
    var heardAs: [String]
    var date: Date
    var id: String { term }
}

enum LaunchAtLogin {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }
    static var needsApproval: Bool { SMAppService.mainApp.status == .requiresApproval }

    /// Whether this launch came from the login item rather than the user. macOS marks login-item launches in the
    /// open-application Apple event; as a fallback, a launch within two minutes of the user logging in (when the
    /// Dock started) counts too. Call it while the app is finishing launching, when that event is current.
    static func wasLaunchedAtLogin() -> Bool {
        // 'oapp' event, 'prdt' parameter, 'lgit' value (kAEOpenApplication, keyAEPropData, keyAELaunchedAsLogInItem).
        if let event = NSAppleEventManager.shared().currentAppleEvent, event.eventID == 0x6F61_7070,
           event.paramDescriptor(forKeyword: 0x7072_6474)?.enumCodeValue == 0x6C67_6974 {
            return true
        }
        guard isEnabled else { return false }
        let loggedIn = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.dock").first?.launchDate
        return (loggedIn.map { Date().timeIntervalSince($0) } ?? ProcessInfo.processInfo.systemUptime) < 120
    }

    static func set(_ enabled: Bool) throws {
        if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
    }

    /// Turns it on once, the first time the installed app runs (the user asked for auto-start).
    static func enableByDefaultOnce() {
        let flag = "didEnableLaunchAtLoginByDefault"
        guard !UserDefaults.standard.bool(forKey: flag),
              Bundle.main.bundlePath.hasPrefix("/Applications/") else { return }
        UserDefaults.standard.set(true, forKey: flag)
        try? set(true)
    }
}

/// All provider keys in one keychain item, so the app touches the keychain once at launch.
enum Credentials {
    private static let service = "OpenTypeless"
    private static let account = "credentials"
    private static let legacyAccount = "openrouter"

    static func load() -> [String: String] {
        if let data = read(account), let keys = try? JSONDecoder().decode([String: String].self, from: data) {
            return keys
        }
        // v0.1 stored a single OpenRouter key.
        if let data = read(legacyAccount), let key = String(data: data, encoding: .utf8), !key.isEmpty {
            let keys = ["openrouter": key]
            save(keys)
            delete(legacyAccount)
            return keys
        }
        return [:]
    }

    static func save(_ keys: [String: String]) {
        delete(account)
        guard !keys.isEmpty, let data = try? JSONEncoder().encode(keys) else { return }
        let add: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock,
        ]
        SecItemAdd(add as CFDictionary, nil)
    }

    private static func read(_ account: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else { return nil }
        return item as? Data
    }

    private static func delete(_ account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}

enum AppPaths {
    static var support: URL {
        // OPENTYPELESS_SUPPORT_DIR points history elsewhere, e.g. at sample data for screenshots.
        let url = ProcessInfo.processInfo.environment["OPENTYPELESS_SUPPORT_DIR"].map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OpenTypeless", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static var sessions: URL {
        let url = support.appendingPathComponent("Sessions", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}

extension Notification.Name {
    /// userInfo["page"]: a `SettingsPage` raw value to open at.
    static let openSettings = Notification.Name("OpenTypeless.openSettings")
}
