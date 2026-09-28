import Foundation

/// Localisation without bundles: every user-facing string is written inline with its Chinese and English text,
/// as `L("有新版本 \(version)", "Version \(version) is available")`. The other languages come from
/// i18n/strings.json (embedded at build time), keyed by the English text with its interpolations numbered in
/// order ("Version {0} is available"). A string with no translation shows in English. Switching language takes
/// effect immediately. `python3 i18n/check.py` lists strings that still need translating.
public enum AppLanguage: String, CaseIterable, Identifiable, Sendable {
    case system, zh, en, ja, ko, es, pt, fr, de, ru

    public var id: String { rawValue }

    public static let defaultsKey = "appLanguage"

    /// The languages the UI can be shown in, in menu order.
    public static var supported: [AppLanguage] { allCases.filter { $0 != .system } }

    /// The language's name in itself, for language menus ("" for `.system`, which needs a localised label).
    public var nativeName: String {
        switch self {
        case .system: return ""
        case .en: return "English"
        case .zh: return "简体中文"
        case .ja: return "日本語"
        case .ko: return "한국어"
        case .es: return "Español"
        case .pt: return "Português"
        case .fr: return "Français"
        case .de: return "Deutsch"
        case .ru: return "Русский"
        }
    }

    /// The locale used to format dates in this language.
    public var locale: Locale {
        switch self {
        case .system: return .current
        case .zh: return Locale(identifier: "zh_Hans")
        case .pt: return Locale(identifier: "pt_BR")
        default: return Locale(identifier: rawValue)
        }
    }

    public static var current: AppLanguage {
        AppLanguage(rawValue: UserDefaults.standard.string(forKey: defaultsKey) ?? "") ?? .system
    }

    /// The language actually used for display: the chosen one, or the first system language the app supports.
    public static var resolved: AppLanguage {
        let chosen = current
        return chosen != .system ? chosen : Locale.preferredLanguages.lazy.compactMap(matching).first ?? .en
    }

    /// The supported language for a language identifier such as "pt-BR" or "zh-Hant-TW", if any.
    public static func matching(_ identifier: String) -> AppLanguage? {
        let code = identifier.prefix { $0 != "-" && $0 != "_" }.lowercased()
        return code == AppLanguage.system.rawValue ? nil : AppLanguage(rawValue: code)
    }
}

/// One side of an `L(…)` call: the finished text, plus the template ("Version {0} is available") and the
/// interpolated values that a translation is filled in with.
public struct LocalizedText: ExpressibleByStringInterpolation, Sendable {
    public let text: String
    let key: String
    let arguments: [String]

    public init(stringLiteral value: String) {
        text = value
        key = value
        arguments = []
    }

    public init(stringInterpolation: StringInterpolation) {
        text = stringInterpolation.text
        key = stringInterpolation.key
        arguments = stringInterpolation.arguments
    }

    public struct StringInterpolation: StringInterpolationProtocol {
        var text = "", key = "", arguments: [String] = []

        public init(literalCapacity: Int, interpolationCount: Int) {
            text.reserveCapacity(literalCapacity)
        }

        public mutating func appendLiteral(_ literal: String) {
            text += literal
            key += literal
        }

        public mutating func appendInterpolation<T>(_ value: T) {
            let string = String(describing: value)
            text += string
            key += "{\(arguments.count)}"
            arguments.append(string)
        }
    }
}

public func L(_ zh: LocalizedText, _ en: LocalizedText) -> String {
    localized(zh, en, in: AppLanguage.resolved)
}

func localized(_ zh: LocalizedText, _ en: LocalizedText, in language: AppLanguage) -> String {
    switch language {
    case .zh: return zh.text
    case .en, .system: return en.text
    default:
        guard let template = Translations.table[en.key]?[language.rawValue] else { return en.text }
        return Translations.fill(template, with: en.arguments)
    }
}

enum Translations {
    /// English template → language code → translation, from i18n/strings.json.
    static let table: [String: [String: String]] =
        (try? JSONDecoder().decode([String: [String: String]].self, from: Data(PackageResources.strings_json))) ?? [:]

    /// Replaces each {n} in the template with the n-th argument.
    static func fill(_ template: String, with arguments: [String]) -> String {
        var result = ""
        var rest = template[...]
        while let open = rest.firstIndex(of: "{") {
            result += rest[..<open]
            let afterOpen = rest.index(after: open)
            if let close = rest[afterOpen...].firstIndex(of: "}"), let index = Int(rest[afterOpen..<close]),
               arguments.indices.contains(index) {
                result += arguments[index]
                rest = rest[rest.index(after: close)...]
            } else {
                result += "{"
                rest = rest[afterOpen...]
            }
        }
        return result + rest
    }
}
