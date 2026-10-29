import AppKit
import Foundation
import Security
import ServiceManagement
import SwiftUI
import TypelessCore

/// User preferences. API keys live in a private file (see Credentials); everything else in UserDefaults.
final class AppSettings: ObservableObject {
    static let shared = AppSettings()

    @AppStorage(AppLanguage.defaultsKey) var appLanguage: AppLanguage = .system

    @AppStorage("hotkeyV2") private var hotkeyData = Data()
    @AppStorage("sttProvider") var sttProvider: ProviderID = .openrouter
    @AppStorage("sttModel") var sttModel: String = ProviderID.openrouter.defaultSTTModel
    @AppStorage("polishProvider") var polishProvider: ProviderID = .openrouter
    @AppStorage("polishModel") var polishModel: String = ProviderID.openrouter.defaultChatModel
    @AppStorage("polishEnabled") var polishEnabled: Bool = true
    /// A second speech-to-text route, asked when the first is late for a chunk or fails (see TranscriptionPipeline).
    @AppStorage("sttBackupEnabled") var sttBackupEnabled: Bool = false
    @AppStorage("sttBackupProvider") var sttBackupProvider: ProviderID = .openrouter
    @AppStorage("sttBackupModel") var sttBackupModel: String = ProviderID.openrouter.defaultBackupSTTModel
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
    /// Press Return once the text is pasted, so a chat message or AI prompt is sent without touching the keyboard.
    @AppStorage("pressEnterAfterInsert") var pressEnterAfterInsert: Bool = false
    /// Fit the spacing and capitalisation of pasted text to the text around the cursor (see InsertionJoiner). Safe on by
    /// default: a field that can't be read gets the text as it is.
    @AppStorage("fitToSurroundingText") var fitToSurroundingText: Bool = true
    /// "New line" and "new paragraph" said on their own become line breaks (see `SpokenLayout`). Off until the user turns
    /// it on: a command word said on its own in a list ("spaces, new line, tabs") becomes a break too.
    @AppStorage("spokenLayoutCommands") var spokenLayoutCommands: Bool = false
    /// "Finish and send": the key that, pressed while recording, ends the dictation and sends it once it's pasted (see
    /// `SendKey`). Off until the user picks one; a choice this version can't read is off too.
    @AppStorage("sendKey") var sendKey: SendKey = .off
    /// Mute the speakers while recording (see OutputMute).
    @AppStorage("muteWhileRecording") var muteWhileRecording: Bool = false
    @AppStorage("maxRecordingMinutes") var maxRecordingMinutes: Int = 20
    @AppStorage("historyRetention") var historyRetention: HistoryRetention = .month
    /// Look for a new release once a day and download it in the background (see Updater).
    @AppStorage("autoCheckUpdates") var autoCheckUpdates: Bool = true
    /// The microphone to record from, by CoreAudio UID; "" follows the system default input.
    @AppStorage("microphone") var microphoneUID: String = ""
    /// Keep the microphone running between dictations, so recording starts at once with the moment before the key.
    @AppStorage("keepMicrophoneWarm") var keepMicrophoneWarm: Bool = false
    /// Show what's being said above the capsule while recording, recognised on this Mac (see LivePreview).
    @AppStorage("livePreview") var livePreview: Bool = false
    /// Open the Home page when the app starts at login (a manual launch always shows it).
    @AppStorage("showHomeAtLogin") var showHomeAtLogin: Bool = false
    /// The typing speed "time saved" on the Home page is measured against.
    @AppStorage("typingWordsPerMinute") var typingWordsPerMinute: Int = UsageTotals.defaultTypingWordsPerMinute
    /// Clean up the transcript of a file transcribed from History (the switch in its import sheet), when clean-up is on.
    @AppStorage("fileCleanUp") var fileCleanUp: Bool = true

    /// Translation mode: holding the hotkey and pressing `translationChord` inserts that dictation translated (see
    /// `translationTarget`). Off until the user turns it on and picks a language.
    @AppStorage("translationEnabled") var translationEnabled: Bool = false
    /// A listed language's code (see `TranslationLanguage`), or `otherTranslationLanguage` for the one typed in
    /// `translationOtherLanguage`.
    @AppStorage("translationLanguage") var translationLanguage: String = TranslationLanguage.english.rawValue
    @AppStorage("translationOtherLanguage") var translationOtherLanguage: String = ""
    @AppStorage("translationChord") var translationChord: TranslationChord = .shift

    /// Command mode: holding the hotkey and pressing `commandChord` edits the text selected in the app in front by what
    /// is said (see `SelectionCommand`). Off until the user turns it on: while it's on, the chord's key pressed with
    /// the hotkey is kept from the app in front.
    @AppStorage("commandModeEnabled") var commandModeEnabled: Bool = false
    /// One of the modifiers translation's chord is chosen from (see `TranslationChord`); one translation mode also has
    /// is left to translation (see `SelectionCommand.chordKeys`).
    @AppStorage("commandChord") var commandChord: TranslationChord = .control

    /// Learn vocabulary from the fixes the user makes to dictated text (see EditWatcher).
    @AppStorage("learnFromEdits") var learnFromEdits: Bool = true
    @Published private(set) var learnedTerms: [LearnedTerm]
    /// Send the text around the cursor with the transcript, for the clean-up to spell names by (see CursorContextReader).
    /// Off by default: it goes to the clean-up provider.
    @AppStorage("useCursorContext") var useCursorContext: Bool = false
    /// Learned terms the user removed; never learned again (lowercased).
    private var forgottenTerms: Set<String>
    /// Spoken shortcuts: a trigger said in a dictation is replaced by its expansion (see SnippetExpander). None at first.
    @Published private(set) var snippets: [Snippet]

    /// The saved keys only: OPENROUTER_API_KEY overrides one in `apiKey(for:)` but never lands in here.
    @Published private(set) var apiKeys: [String: String]
    @Published private(set) var baseURLs: [String: String]
    /// Prices the user entered for models of providers other than OpenRouter (whose prices are live), by `ModelPrice.key`.
    @Published private(set) var modelPrices: [String: ModelPrice]
    /// The OpenRouter host the user picked for a clean-up model (endpoint tag, by model ID); none means automatic.
    @Published private(set) var openRouterHosts: [String: String]
    /// Settings for particular apps, by bundle ID, applied to a dictation started in them (see AppProfile). None at first.
    @Published private(set) var appProfiles: [AppProfile]
    /// The shortcuts the user gave to actions besides dictating (see `ShortcutAction`), by the action's name; none by
    /// default.
    @Published private var actionShortcuts: [String: Hotkey]
    /// The same as saved, changed one entry at a time (see `StoredEntries`), so one saved by a newer version, or one
    /// that can't be read, is kept as it is.
    private var storedShortcuts: Data?

    private init() {
        // Loaded even when OPENROUTER_API_KEY is set: it only stands in for the OpenRouter key, and the app launched
        // with it still needs the others (and must not save over them when one is edited).
        apiKeys = Credentials.load()
        baseURLs = (UserDefaults.standard.dictionary(forKey: "baseURLs") as? [String: String]) ?? [:]
        learnedTerms = UserDefaults.standard.data(forKey: "learnedVocabulary")
            .flatMap { try? JSONDecoder().decode([LearnedTerm].self, from: $0) } ?? []
        forgottenTerms = Set(UserDefaults.standard.stringArray(forKey: "forgottenVocabulary") ?? [])
        let savedSnippets = UserDefaults.standard.data(forKey: "snippets").map(Snippet.list(from:))
        snippets = savedSnippets?.snippets ?? []
        modelPrices = UserDefaults.standard.data(forKey: "modelPrices")
            .flatMap { try? JSONDecoder().decode([String: ModelPrice].self, from: $0) } ?? [:]
        openRouterHosts = (UserDefaults.standard.dictionary(forKey: "openRouterHosts") as? [String: String]) ?? [:]
        storedShortcuts = UserDefaults.standard.data(forKey: "shortcuts")
        actionShortcuts = StoredEntries.decode(storedShortcuts, as: Hotkey.self)
        // An entry that isn't a profile is skipped, not the whole list; an ID that's missing or taken is repaired once and
        // saved, so edits, Remove and retries keep finding their profile.
        let savedProfiles = UserDefaults.standard.data(forKey: "appProfiles").flatMap(AppProfile.list(from:)) ?? []
        appProfiles = AppProfile.withUniqueIDs(savedProfiles)
        if appProfiles != savedProfiles { saveAppProfiles() }
        // IDs given to entries that had none (or another's) are kept, so editing or deleting them works next time too.
        if savedSnippets?.repaired == true { saveSnippets() }
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

    // MARK: Action shortcuts

    func shortcut(for action: ShortcutAction) -> Hotkey? {
        actionShortcuts[action.rawValue]
    }

    /// nil clears it.
    func setShortcut(_ hotkey: Hotkey?, for action: ShortcutAction) {
        guard let stored = StoredEntries.replacing(action.rawValue, with: hotkey, in: storedShortcuts) else { return }
        storedShortcuts = stored
        UserDefaults.standard.set(stored, forKey: "shortcuts")
        actionShortcuts = StoredEntries.decode(stored, as: Hotkey.self)
    }

    /// The actions' shortcuts, as the hotkey monitor matches keys against them.
    var shortcutMap: ShortcutMap {
        var chords: [ShortcutAction: KeyChord] = [:]
        for action in ShortcutAction.allCases {
            if let hotkey = shortcut(for: action) { chords[action] = hotkey.chord }
        }
        return ShortcutMap(chords)
    }

    // MARK: Translation

    /// What `translationLanguage` holds when the language is typed in.
    static let otherTranslationLanguage = "other"

    /// What a translation dictation is translated into; nil while translation mode is off, or another language is picked
    /// without a name.
    var translationTarget: TranslationTarget? {
        guard translationEnabled else { return nil }
        return TranslationTarget(translationLanguage == Self.otherTranslationLanguage ? translationOtherLanguage
                                                                                      : translationLanguage)
    }

    /// Whether command mode's chord is the same as translation's, while both are on: then it does nothing.
    var commandChordClashes: Bool {
        commandModeEnabled && translationTarget != nil && commandChord == translationChord
    }

    // MARK: Providers

    /// The key requests use: OPENROUTER_API_KEY, when set, in place of the saved OpenRouter key.
    func apiKey(for id: ProviderID) -> String {
        if id == .openrouter, let env = Self.openRouterKeyFromEnvironment { return env }
        return savedAPIKey(for: id)
    }

    /// The key saved in Settings, which is what a key field shows and edits, so OPENROUTER_API_KEY is never saved.
    func savedAPIKey(for id: ProviderID) -> String {
        apiKeys[id.rawValue] ?? ""
    }

    static var openRouterKeyFromEnvironment: String? {
        ProcessInfo.processInfo.environment["OPENROUTER_API_KEY"].flatMap { $0.isEmpty ? nil : $0 }
    }

    func setAPIKey(_ key: String, for id: ProviderID) {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard apiKeys[id.rawValue, default: ""] != trimmed else { return }
        // On top of what the file holds now, not only what was read at launch, so a key never read here isn't erased.
        var keys = apiKeys.merging(Credentials.saved()) { current, _ in current }
        keys[id.rawValue] = trimmed.isEmpty ? nil : trimmed
        apiKeys = keys
        Credentials.save(keys)
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

    /// The backup speech-to-text route, when switched on, configured, and actually different from the primary.
    var sttBackupEndpoint: ProviderEndpoint? {
        let model = sttBackupModel.trimmingCharacters(in: .whitespaces)
        guard sttBackupEnabled, !model.isEmpty, sttBackupProvider.supportsSTT, isConfigured(sttBackupProvider),
              sttBackupProvider != sttProvider || model != sttModel.trimmingCharacters(in: .whitespaces) else { return nil }
        return endpoint(for: sttBackupProvider)
    }

    func selectSTTBackupProvider(_ id: ProviderID) {
        guard id != sttBackupProvider else { return }
        sttBackupProvider = id
        sttBackupModel = id.defaultBackupSTTModel
    }

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

    // MARK: OpenRouter hosts

    /// The host to ask first for this model, or nil to let OpenRouter pick the fastest.
    func openRouterHost(for model: String) -> String? {
        openRouterHosts[model.trimmingCharacters(in: .whitespaces)]
    }

    func setOpenRouterHost(_ host: String?, for model: String) {
        let model = model.trimmingCharacters(in: .whitespaces)
        guard !model.isEmpty else { return }
        openRouterHosts[model] = host?.isEmpty == false ? host : nil
        UserDefaults.standard.set(openRouterHosts, forKey: "openRouterHosts")
    }

    // MARK: App profiles

    /// What a dictation runs with when no profile applies.
    var globalDictationSettings: DictationSettings {
        DictationSettings(polishEnabled: polishEnabled, pressEnter: pressEnterAfterInsert, language: sttLanguage,
                          extraInstructions: extraInstructions)
    }

    /// What a dictation started in the app with this bundle ID runs with.
    func dictationSettings(forApp bundleID: String?) -> DictationSettings {
        globalDictationSettings.applying(AppProfile.matching(bundleID, in: appProfiles))
    }

    /// What a retry of a dictation made with the profile `id` runs with: that profile's settings while it still exists,
    /// the global ones otherwise.
    func dictationSettings(profileID id: String?) -> DictationSettings {
        globalDictationSettings.applying(appProfiles.first { $0.id == id })
    }

    /// Adds a profile for an app, following the global settings until it's edited; an app that has one already keeps it.
    @discardableResult
    func addAppProfile(bundleID: String, name: String) -> AppProfile {
        if let existing = AppProfile.matching(bundleID, in: appProfiles) { return existing }
        let profile = AppProfile(app: bundleID, name: name)
        appProfiles.append(profile)
        saveAppProfiles()
        return profile
    }

    func updateAppProfile(_ profile: AppProfile) {
        guard let index = appProfiles.firstIndex(where: { $0.id == profile.id }), appProfiles[index] != profile else { return }
        appProfiles[index] = profile
        saveAppProfiles()
    }

    func removeAppProfile(id: String) {
        appProfiles.removeAll { $0.id == id }
        saveAppProfiles()
    }

    /// Set for the UI snapshots: the profiles shown are samples, and nothing about them is saved.
    private var appProfilesAreSamples = false

    /// For the UI snapshots: shows these profiles in place of the saved ones, which stay as they are.
    func showSampleAppProfiles(_ profiles: [AppProfile]) {
        appProfilesAreSamples = true
        appProfiles = profiles
    }

    private func saveAppProfiles() {
        guard !appProfilesAreSamples else { return }
        UserDefaults.standard.set(try? JSONEncoder().encode(appProfiles), forKey: "appProfiles")
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

    /// Adds an imported file's new terms to the vocabulary, leaving out ones already in it (ignoring case) and ones
    /// `VocabularyTransfer.plan` keeps out of every clean-up request. Importing is the user asking for these terms, so
    /// one they once removed from the learned terms may be learned again too. Returns what it did.
    @discardableResult
    func importVocabulary(_ terms: [String]) -> VocabularyTransfer.ImportPlan {
        let plan = VocabularyTransfer.plan(terms, existing: vocabularyList)
        if !plan.added.isEmpty { vocabulary = VocabularyTransfer.merged(vocabulary, adding: plan.added) }
        let kept = Set(vocabularyList.map { $0.lowercased() }).intersection(terms.map { $0.lowercased() })
        if !forgottenTerms.isDisjoint(with: kept) {
            forgottenTerms.subtract(kept)
            UserDefaults.standard.set(Array(forgottenTerms), forKey: "forgottenVocabulary")
        }
        return plan
    }

    /// The vocabulary as it's exported: each term with how recognition misheard it, when it was learned from a fix.
    var vocabularyForExport: [VocabularyTransfer.Term] {
        let learned = Dictionary(learnedTerms.map { ($0.term.lowercased(), $0.heardAs) }, uniquingKeysWith: { first, _ in first })
        return vocabularyList.map { VocabularyTransfer.Term(term: $0, heardAs: learned[$0.lowercased()] ?? []) }
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

    /// What speech-to-text and the clean-up are given as vocabulary: the snippets' triggers ahead of the vocabulary, so a
    /// trigger comes through as written for its snippet to be found.
    var dictationVocabulary: [String] { SnippetExpander.vocabulary(snippets, before: vocabularyList) }

    // MARK: Snippets

    /// Adds the snippet, or replaces the one with its ID.
    func saveSnippet(_ snippet: Snippet) {
        if let index = snippets.firstIndex(where: { $0.id == snippet.id }) {
            snippets[index] = snippet
        } else {
            snippets.append(snippet)
        }
        saveSnippets()
    }

    func deleteSnippet(_ snippet: Snippet) {
        snippets.removeAll { $0.id == snippet.id }
        saveSnippets()
    }

    private func saveSnippets() {
        UserDefaults.standard.set(try? JSONEncoder().encode(snippets), forKey: "snippets")
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

/// All provider keys in one JSON file only this user can read (Application Support/OpenTypeless/credentials.json).
/// Not the keychain: the app is self-signed, so every update has a new signature and macOS would ask for the
/// login password again before letting it read its own keys.
enum Credentials {
    /// The real Application Support folder, not `AppPaths.support`, which OPENTYPELESS_SUPPORT_DIR can move.
    static var file: URL {
        let directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OpenTypeless", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        return directory.appendingPathComponent("credentials.json")
    }

    private static let movedFromKeychainKey = "credentialsMovedFromKeychain"

    static func load() -> [String: String] {
        var keys = saved()
        // Earlier versions kept the keys in the keychain: read them there once (the last prompt) and move them. Only a
        // read that worked, or found nothing, counts: after Deny, or with the keychain still locked at login, it's
        // tried again next launch.
        if !UserDefaults.standard.bool(forKey: movedFromKeychainKey), let legacy = LegacyKeychain.load() {
            if !legacy.isEmpty {
                keys = legacy.merging(keys) { _, saved in saved }
                save(keys)
            }
            UserDefaults.standard.set(true, forKey: movedFromKeychainKey)
        }
        return keys
    }

    /// What the file holds. One that can't be read is moved aside (see SafeFile), so the next save doesn't overwrite it.
    static func saved() -> [String: String] {
        let url = file
        guard FileManager.default.fileExists(atPath: url.path) else { return [:] }
        protect(url)
        if let data = try? Data(contentsOf: url), let keys = try? JSONDecoder().decode([String: String].self, from: data) {
            return keys
        }
        SafeFile.moveAside(url)
        return [:]
    }

    static func save(_ keys: [String: String]) {
        guard !keys.isEmpty, let data = try? JSONEncoder().encode(keys) else {
            try? FileManager.default.removeItem(at: file)
            return
        }
        let url = file
        // There but unreadable: replacing it would lose the keys this run couldn't read.
        if FileManager.default.fileExists(atPath: url.path), (try? Data(contentsOf: url)) == nil { return }
        // Created owner-only (0600) from the start, then swapped in whole.
        let temporary = url.deletingLastPathComponent().appendingPathComponent(".credentials-\(UUID().uuidString)")
        guard FileManager.default.createFile(atPath: temporary.path, contents: data,
                                             attributes: [.posixPermissions: 0o600]) else { return }
        if (try? FileManager.default.replaceItemAt(url, withItemAt: temporary)) == nil {
            try? FileManager.default.removeItem(at: temporary)
        }
        protect(url)
    }

    /// Owner-only again, in case a copy or a restore loosened it, and left out of backups, which would carry the keys
    /// off this Mac.
    private static func protect(_ url: URL) {
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var url = url
        try? url.setResourceValues(values)
    }
}

/// Where earlier versions kept the keys; read once to move them to the file, and left in place, since removing
/// the item could ask for the password a second time.
private enum LegacyKeychain {
    private static let service = "OpenTypeless"

    /// Nil when the keychain couldn't be read (the user clicked Deny, or it's locked); empty when there's nothing in it.
    static func load() -> [String: String]? {
        guard let credentials = read("credentials") else { return nil }
        if case let .found(data) = credentials, let keys = try? JSONDecoder().decode([String: String].self, from: data) {
            return keys
        }
        // v0.1 stored a single OpenRouter key.
        guard let openRouter = read("openrouter") else { return nil }
        if case let .found(data) = openRouter, let key = String(data: data, encoding: .utf8), !key.isEmpty {
            return ["openrouter": key]
        }
        return [:]
    }

    private enum Item {
        case found(Data)
        case missing
    }

    /// Nil when the keychain wouldn't say (any error other than "no such item").
    private static func read(_ account: String) -> Item? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        switch SecItemCopyMatching(query as CFDictionary, &item) {
        case errSecSuccess: return (item as? Data).map(Item.found) ?? .missing
        case errSecItemNotFound: return .missing
        default: return nil
        }
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
