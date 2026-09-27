import Foundation

/// Tiny two-language localisation: every user-facing string is written inline as `L("中文", "English")`.
/// No bundles or string tables, and switching language takes effect immediately.
public enum AppLanguage: String, CaseIterable, Identifiable, Sendable {
    case system, zh, en

    public var id: String { rawValue }

    public static let defaultsKey = "appLanguage"

    public static var current: AppLanguage {
        AppLanguage(rawValue: UserDefaults.standard.string(forKey: defaultsKey) ?? "") ?? .system
    }

    /// The language actually used for display (resolves `.system`).
    public static var resolved: AppLanguage {
        switch current {
        case .zh, .en: return current
        case .system:
            return (Locale.preferredLanguages.first ?? "en").hasPrefix("zh") ? .zh : .en
        }
    }
}

public func L(_ zh: String, _ en: String) -> String {
    AppLanguage.resolved == .zh ? zh : en
}
