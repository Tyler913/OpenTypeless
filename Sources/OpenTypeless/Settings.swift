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

    @Published private(set) var apiKeys: [String: String]
    @Published private(set) var baseURLs: [String: String]

    private init() {
        // In CLI mode with OPENROUTER_API_KEY set, don't touch the keychain (avoids an access prompt).
        let cli = !(ProcessInfo.processInfo.environment["OPENROUTER_API_KEY"] ?? "").isEmpty
        apiKeys = cli ? [:] : Credentials.load()
        baseURLs = (UserDefaults.standard.dictionary(forKey: "baseURLs") as? [String: String]) ?? [:]
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

    var vocabularyList: [String] {
        vocabulary.split(whereSeparator: { $0 == "," || $0 == "，" || $0 == "\n" })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }
}

enum LaunchAtLogin {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }
    static var needsApproval: Bool { SMAppService.mainApp.status == .requiresApproval }

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
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
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
