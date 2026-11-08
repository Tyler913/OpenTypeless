import Foundation

/// How heavily a dictation is cleaned up. `.standard` is the tuned clean-up prompt as it is (minimal edits), and the
/// default. `.light` only makes the safest fixes (punctuation, fillers, stutters, sure recognition errors, explicit
/// self-corrections); `.polished` may also smooth spoken phrasing into written prose, keeping every fact. Each adds a
/// section after the prompt (see `Prompts.strengthSection`), so the shared start stays the same for all of them.
public enum PolishStrength: String, CaseIterable, Codable, Sendable, Identifiable {
    case off, light, standard, polished

    public var id: String { rawValue }

    /// Its name in Settings and History.
    public var displayName: String {
        switch self {
        case .off: return L("关", "Off")
        case .light: return L("轻度", "Light")
        case .standard: return L("标准", "Standard")
        case .polished: return L("润色", "Polished")
        }
    }

    /// The strength saved as an on/off switch and a level next to it: the switch older versions read and write
    /// (`polishEnabled`, a profile's `polish`), the level only this one (`polishStrength`, a profile's `strength`). The
    /// switch decides whether: off is `.off`, on is the level (`.standard` when there's none, or one this version doesn't
    /// know). Without a switch, the level alone, and nil when neither says anything (a profile that follows the global
    /// setting). So an older version turning clean-up off or on is honoured, and its "on" means what it always did.
    public static func stored(enabled: Bool?, level: String?) -> PolishStrength? {
        let known = level.flatMap(PolishStrength.init(rawValue:))
        switch enabled {
        case false?: return .off
        case true?: return known == nil || known == .off ? .standard : known
        case nil: return known
        }
    }
}

/// Kinds of apps whose text wants a clean-up strength of its own (see `RecommendedApps`).
public enum AppCategory: String, CaseIterable, Sendable, Identifiable {
    /// Chat apps: a message is said as it would be typed, so only the safest fixes.
    case chat
    /// Mail apps: an email reads better written.
    case email
    /// Terminals and code editors: commands, code and prompts for an AI, where a word changed can change what runs.
    case code

    public var id: String { rawValue }

    /// Its name on the Apps page.
    public var displayName: String {
        switch self {
        case .chat: return L("聊天应用", "Chat apps")
        case .email: return L("邮件", "Email")
        case .code: return L("终端和代码编辑器", "Terminals and code editors")
        }
    }

    /// The strength dictations into an app of this kind get.
    public var strength: PolishStrength {
        switch self {
        case .chat, .code: return .light
        case .email: return .polished
        }
    }
}

/// A well-known app the recommended settings cover, by `AppProfile.matchKey` of its bundle IDs (macOS) and executables
/// (Windows).
public struct RecommendedApp: Sendable, Identifiable {
    public let name: String
    /// Its name in Chinese, where it has one of its own (微信, 飞书); nil for the same name.
    public let chineseName: String?
    public let category: AppCategory
    public let mac: [String]
    public let windows: [String]

    public var id: String { name }

    /// Its name in the UI's language.
    public var displayName: String { AppLanguage.resolved == .zh ? chineseName ?? name : name }
}

/// Recommended settings for common apps: a clean-up strength by the kind of app (see `AppCategory`), on unless the user
/// turns it off on the Apps page. Only the strength, never Return or the language, and only while clean-up is on; the
/// app's own profile, when it sets a strength, wins. Nothing about the app goes to the model: only the strength's
/// section of the prompt changes. Shared with the Windows app, which has the same table.
public enum RecommendedApps {
    public static let all: [RecommendedApp] = [
        // Chat
        .init(name: "Slack", chineseName: nil, category: .chat, mac: ["com.tinyspeck.slackmacgap"], windows: ["slack"]),
        .init(name: "WeChat", chineseName: "微信", category: .chat, mac: ["com.tencent.xinwechat"], windows: ["wechat", "weixin"]),
        .init(name: "QQ", chineseName: nil, category: .chat, mac: ["com.tencent.qq"], windows: ["qq"]),
        .init(name: "Discord", chineseName: nil, category: .chat, mac: ["com.hnc.discord"], windows: ["discord"]),
        .init(name: "Telegram", chineseName: nil, category: .chat, mac: ["ru.keepcoder.telegram", "org.telegram.desktop"],
              windows: ["telegram"]),
        .init(name: "WhatsApp", chineseName: nil, category: .chat, mac: ["net.whatsapp.whatsapp", "desktop.whatsapp"],
              windows: ["whatsapp", "whatsapp.root"]),
        .init(name: "Messages", chineseName: "信息", category: .chat, mac: ["com.apple.mobilesms"], windows: []),
        .init(name: "Signal", chineseName: nil, category: .chat, mac: ["org.whispersystems.signal-desktop"], windows: ["signal"]),
        .init(name: "LINE", chineseName: nil, category: .chat, mac: ["jp.naver.line.mac"], windows: ["line"]),
        .init(name: "Microsoft Teams", chineseName: nil, category: .chat, mac: ["com.microsoft.teams2", "com.microsoft.teams"],
              windows: ["ms-teams", "teams"]),
        .init(name: "Lark", chineseName: "飞书", category: .chat, mac: ["com.electron.lark"], windows: ["feishu", "lark"]),
        .init(name: "DingTalk", chineseName: "钉钉", category: .chat, mac: ["com.alibaba.dingtalkmac"], windows: ["dingtalk"]),
        .init(name: "KakaoTalk", chineseName: nil, category: .chat, mac: ["com.kakao.kakaotalkmac"], windows: ["kakaotalk"]),
        // Mail
        .init(name: "Mail", chineseName: "邮件", category: .email, mac: ["com.apple.mail"], windows: []),
        .init(name: "Outlook", chineseName: nil, category: .email, mac: ["com.microsoft.outlook"], windows: ["outlook", "olk"]),
        .init(name: "Spark", chineseName: nil, category: .email, mac: ["com.readdle.smartemail-mac", "com.readdle.sparkdesktop"],
              windows: ["spark"]),
        .init(name: "Thunderbird", chineseName: nil, category: .email, mac: ["org.mozilla.thunderbird"], windows: ["thunderbird"]),
        .init(name: "Foxmail", chineseName: nil, category: .email, mac: ["com.tencent.foxmail"], windows: ["foxmail"]),
        // Terminals and code editors
        .init(name: "Terminal", chineseName: "终端", category: .code, mac: ["com.apple.terminal"], windows: []),
        .init(name: "iTerm2", chineseName: nil, category: .code, mac: ["com.googlecode.iterm2"], windows: []),
        .init(name: "Warp", chineseName: nil, category: .code, mac: ["dev.warp.warp-stable"], windows: ["warp"]),
        .init(name: "Ghostty", chineseName: nil, category: .code, mac: ["com.mitchellh.ghostty"], windows: ["ghostty"]),
        .init(name: "Windows Terminal", chineseName: nil, category: .code, mac: [], windows: ["windowsterminal"]),
        .init(name: "Visual Studio Code", chineseName: nil, category: .code, mac: ["com.microsoft.vscode"], windows: ["code"]),
        .init(name: "Cursor", chineseName: nil, category: .code, mac: ["com.todesktop.230313mzl4w4u92"], windows: ["cursor"]),
        .init(name: "Xcode", chineseName: nil, category: .code, mac: ["com.apple.dt.xcode"], windows: []),
        .init(name: "JetBrains IDEs", chineseName: nil, category: .code,
              mac: ["com.jetbrains.intellij", "com.jetbrains.intellij.ce", "com.jetbrains.pycharm", "com.jetbrains.pycharm.ce",
                    "com.jetbrains.webstorm", "com.jetbrains.goland", "com.jetbrains.clion", "com.jetbrains.rider",
                    "com.jetbrains.phpstorm", "com.jetbrains.rubymine", "com.jetbrains.datagrip", "com.jetbrains.rustrover"],
              windows: ["idea64", "pycharm64", "webstorm64", "goland64", "clion64", "rider64", "phpstorm64", "rubymine64",
                        "datagrip64", "rustrover64"]),
        .init(name: "Zed", chineseName: nil, category: .code, mac: ["dev.zed.zed"], windows: ["zed"]),
    ]

    /// Every identifier in the table, by `AppProfile.matchKey`.
    static let byKey: [String: RecommendedApp] = {
        var table: [String: RecommendedApp] = [:]
        for app in all {
            for id in app.mac + app.windows { table[AppProfile.matchKey(id)] = app }
        }
        return table
    }()

    /// The table's entry for `app` (a bundle ID or an executable, compared as profiles are); nil when it isn't in it.
    public static func matching(_ app: String?) -> RecommendedApp? {
        guard let app else { return nil }
        return byKey[AppProfile.matchKey(app)]
    }

    /// The kind of app `app` is, when the table has it.
    public static func category(for app: String?) -> AppCategory? { matching(app)?.category }
}
