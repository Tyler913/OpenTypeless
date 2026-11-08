namespace TypelessCore;

/// <summary>
/// How heavily a dictation is cleaned up. <c>Standard</c> is the tuned clean-up prompt as it is (minimal edits), and the
/// default. <c>Light</c> only makes the safest fixes (punctuation, fillers, stutters, sure recognition errors, explicit
/// self-corrections); <c>Polished</c> may also smooth spoken phrasing into written prose, keeping every fact. Each adds a
/// section after the prompt (see <see cref="Prompts.StrengthSection"/>), so the shared start stays the same for all of them.
/// </summary>
public enum PolishStrength
{
    Off,
    Light,
    Standard,
    Polished,
}

public static class PolishStrengths
{
    /// <summary>Every strength, in menu order.</summary>
    public static readonly PolishStrength[] All = [PolishStrength.Off, PolishStrength.Light, PolishStrength.Standard, PolishStrength.Polished];

    /// <summary>The name it's saved by, the same as the macOS app's.</summary>
    public static string Id(this PolishStrength strength) => strength switch
    {
        PolishStrength.Off => "off",
        PolishStrength.Light => "light",
        PolishStrength.Polished => "polished",
        _ => "standard",
    };

    /// <summary>The strength saved as <paramref name="id"/>; null for none, or for one this version doesn't know.</summary>
    public static PolishStrength? From(string? id) => id switch
    {
        "off" => PolishStrength.Off,
        "light" => PolishStrength.Light,
        "standard" => PolishStrength.Standard,
        "polished" => PolishStrength.Polished,
        _ => null,
    };

    /// <summary>Its name in Settings and History.</summary>
    public static string DisplayName(this PolishStrength strength) => strength switch
    {
        PolishStrength.Off => L("关", "Off"),
        PolishStrength.Light => L("轻度", "Light"),
        PolishStrength.Polished => L("润色", "Polished"),
        _ => L("标准", "Standard"),
    };

    /// <summary>
    /// The strength saved as an on/off switch and a level next to it: the switch older versions read and write
    /// (<c>polishEnabled</c>, a profile's <c>polish</c>), the level only this one (<c>polishStrength</c>, a profile's
    /// <c>strength</c>). The switch decides whether: off is <c>Off</c>, on is the level (<c>Standard</c> when there's none,
    /// or one this version doesn't know). Without a switch, the level alone, and null when neither says anything (a profile
    /// that follows the global setting). So an older version turning clean-up off or on is honoured, and its "on" means
    /// what it always did.
    /// </summary>
    public static PolishStrength? Stored(bool? enabled, string? level)
    {
        var known = From(level);
        return enabled switch
        {
            false => PolishStrength.Off,
            true => known is null or PolishStrength.Off ? PolishStrength.Standard : known,
            null => known,
        };
    }
}

/// <summary>Kinds of apps whose text wants a clean-up strength of its own (see <see cref="RecommendedApps"/>).</summary>
public enum AppCategory
{
    /// <summary>Chat apps: a message is said as it would be typed, so only the safest fixes.</summary>
    Chat,
    /// <summary>Mail apps: an email reads better written.</summary>
    Email,
    /// <summary>Terminals and code editors: commands, code and prompts for an AI, where a word changed can change what runs.</summary>
    Code,
}

public static class AppCategories
{
    /// <summary>Every kind, in the order the Apps page lists them.</summary>
    public static readonly AppCategory[] All = [AppCategory.Chat, AppCategory.Email, AppCategory.Code];

    /// <summary>The name it's saved by in History, the same as the macOS app's.</summary>
    public static string Id(this AppCategory category) => category switch
    {
        AppCategory.Email => "email",
        AppCategory.Code => "code",
        _ => "chat",
    };

    /// <summary>The kind saved as <paramref name="id"/>; null for none, or for one this version doesn't know.</summary>
    public static AppCategory? From(string? id) => id switch
    {
        "chat" => AppCategory.Chat,
        "email" => AppCategory.Email,
        "code" => AppCategory.Code,
        _ => null,
    };

    /// <summary>Its name on the Apps page.</summary>
    public static string DisplayName(this AppCategory category) => category switch
    {
        AppCategory.Email => L("邮件", "Email"),
        AppCategory.Code => L("终端和代码编辑器", "Terminals and code editors"),
        _ => L("聊天应用", "Chat apps"),
    };

    /// <summary>The strength dictations into an app of this kind get.</summary>
    public static PolishStrength Strength(this AppCategory category) =>
        category == AppCategory.Email ? PolishStrength.Polished : PolishStrength.Light;
}

/// <summary>
/// A well-known app the recommended settings cover, by <see cref="AppProfile.MatchKey"/> of its bundle IDs (macOS) and
/// executables (Windows).
/// </summary>
/// <param name="ChineseName">Its name in Chinese, where it has one of its own (微信, 飞书); null for the same name.</param>
public sealed record RecommendedApp(string Name, string? ChineseName, AppCategory Category, string[] MacIds, string[] WindowsIds)
{
    /// <summary>Its name in the UI's language.</summary>
    public string DisplayName => Resolved == AppLanguage.Zh ? ChineseName ?? Name : Name;
}

/// <summary>
/// Recommended settings for common apps: a clean-up strength by the kind of app (see <see cref="AppCategory"/>), on unless
/// the user turns it off on the Apps page. Only the strength, never Enter or the language, and only while clean-up is on;
/// the app's own profile, when it sets a strength, wins. Nothing about the app goes to the model: only the strength's
/// section of the prompt changes. Shared with the macOS app, which has the same table.
/// </summary>
public static class RecommendedApps
{
    public static readonly RecommendedApp[] All =
    [
        // Chat
        new("Slack", null, AppCategory.Chat, ["com.tinyspeck.slackmacgap"], ["slack"]),
        new("WeChat", "微信", AppCategory.Chat, ["com.tencent.xinwechat"], ["wechat", "weixin"]),
        new("QQ", null, AppCategory.Chat, ["com.tencent.qq"], ["qq"]),
        new("Discord", null, AppCategory.Chat, ["com.hnc.discord"], ["discord"]),
        new("Telegram", null, AppCategory.Chat, ["ru.keepcoder.telegram", "org.telegram.desktop"], ["telegram"]),
        new("WhatsApp", null, AppCategory.Chat, ["net.whatsapp.whatsapp", "desktop.whatsapp"], ["whatsapp", "whatsapp.root"]),
        new("Messages", "信息", AppCategory.Chat, ["com.apple.mobilesms"], []),
        new("Signal", null, AppCategory.Chat, ["org.whispersystems.signal-desktop"], ["signal"]),
        new("LINE", null, AppCategory.Chat, ["jp.naver.line.mac"], ["line"]),
        new("Microsoft Teams", null, AppCategory.Chat, ["com.microsoft.teams2", "com.microsoft.teams"], ["ms-teams", "teams"]),
        new("Lark", "飞书", AppCategory.Chat, ["com.electron.lark"], ["feishu", "lark"]),
        new("DingTalk", "钉钉", AppCategory.Chat, ["com.alibaba.dingtalkmac"], ["dingtalk"]),
        new("KakaoTalk", null, AppCategory.Chat, ["com.kakao.kakaotalkmac"], ["kakaotalk"]),
        // Mail
        new("Mail", "邮件", AppCategory.Email, ["com.apple.mail"], []),
        new("Outlook", null, AppCategory.Email, ["com.microsoft.outlook"], ["outlook", "olk"]),
        new("Spark", null, AppCategory.Email, ["com.readdle.smartemail-mac", "com.readdle.sparkdesktop"], ["spark"]),
        new("Thunderbird", null, AppCategory.Email, ["org.mozilla.thunderbird"], ["thunderbird"]),
        new("Foxmail", null, AppCategory.Email, ["com.tencent.foxmail"], ["foxmail"]),
        // Terminals and code editors
        new("Terminal", "终端", AppCategory.Code, ["com.apple.terminal"], []),
        new("iTerm2", null, AppCategory.Code, ["com.googlecode.iterm2"], []),
        new("Warp", null, AppCategory.Code, ["dev.warp.warp-stable"], ["warp"]),
        new("Ghostty", null, AppCategory.Code, ["com.mitchellh.ghostty"], ["ghostty"]),
        new("Windows Terminal", null, AppCategory.Code, [], ["windowsterminal"]),
        new("Visual Studio Code", null, AppCategory.Code, ["com.microsoft.vscode"], ["code"]),
        new("Cursor", null, AppCategory.Code, ["com.todesktop.230313mzl4w4u92"], ["cursor"]),
        new("Xcode", null, AppCategory.Code, ["com.apple.dt.xcode"], []),
        new("JetBrains IDEs", null, AppCategory.Code,
            ["com.jetbrains.intellij", "com.jetbrains.intellij.ce", "com.jetbrains.pycharm", "com.jetbrains.pycharm.ce",
             "com.jetbrains.webstorm", "com.jetbrains.goland", "com.jetbrains.clion", "com.jetbrains.rider",
             "com.jetbrains.phpstorm", "com.jetbrains.rubymine", "com.jetbrains.datagrip", "com.jetbrains.rustrover"],
            ["idea64", "pycharm64", "webstorm64", "goland64", "clion64", "rider64", "phpstorm64", "rubymine64",
             "datagrip64", "rustrover64"]),
        new("Zed", null, AppCategory.Code, ["dev.zed.zed"], ["zed"]),
    ];

    /// <summary>Every identifier in the table, by <see cref="AppProfile.MatchKey"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, RecommendedApp> ByKey = BuildKeys();

    private static Dictionary<string, RecommendedApp> BuildKeys()
    {
        var table = new Dictionary<string, RecommendedApp>();
        foreach (var app in All)
        {
            foreach (var id in app.MacIds.Concat(app.WindowsIds)) table[AppProfile.MatchKey(id)] = app;
        }
        return table;
    }

    /// <summary>The table's entry for <paramref name="app"/> (a bundle ID or an executable, compared as profiles are); null when it isn't in it.</summary>
    public static RecommendedApp? Matching(string? app) =>
        app != null && ByKey.TryGetValue(AppProfile.MatchKey(app), out var entry) ? entry : null;

    /// <summary>The kind of app <paramref name="app"/> is, when the table has it.</summary>
    public static AppCategory? CategoryFor(string? app) => Matching(app)?.Category;
}
