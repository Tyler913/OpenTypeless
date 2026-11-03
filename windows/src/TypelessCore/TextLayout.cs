using System.Text;

namespace TypelessCore;

/// <summary>
/// How much structure a clean-up gives the text, by where it goes (see <see cref="TextLayouts.BuiltIn"/>): a chat message
/// is one block, a prompt for an AI or a document gets paragraphs and lists more readily, an email puts a dictated
/// greeting and sign-off on lines of their own. Only the layout changes, never the words. <c>Natural</c> is the clean-up
/// prompt's own layout and adds nothing to it, so the system prompt stays as it was for every app not classified.
/// </summary>
public enum TextLayout
{
    Natural,
    Chat,
    Structured,
    Email,
}

public static class TextLayouts
{
    /// <summary>Every layout, in menu order.</summary>
    public static readonly TextLayout[] All = [TextLayout.Natural, TextLayout.Chat, TextLayout.Structured, TextLayout.Email];

    /// <summary>The name it's saved by, the same as the macOS app's.</summary>
    public static string Id(this TextLayout layout) => layout switch
    {
        TextLayout.Chat => "chat",
        TextLayout.Structured => "structured",
        TextLayout.Email => "email",
        _ => "natural",
    };

    /// <summary>The layout saved as <paramref name="id"/>; null for none, or for one this version doesn't know.</summary>
    public static TextLayout? From(string? id) => id switch
    {
        "natural" => TextLayout.Natural,
        "chat" => TextLayout.Chat,
        "structured" => TextLayout.Structured,
        "email" => TextLayout.Email,
        _ => null,
    };

    /// <summary>
    /// Apps whose text has an obvious layout, by <see cref="AppProfile.MatchKey"/> of their bundle ID (macOS) or
    /// executable (Windows): chat apps, where a message is one block; AI chats, code editors and note or document apps,
    /// where a dictated prompt or note reads better with paragraphs and lists; and mail apps. An app's profile overrides
    /// this, and any other app gets <see cref="TextLayout.Natural"/>. Browsers aren't here: what's in the tab isn't known.
    /// Shared with the macOS app, which has the same table.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, TextLayout> BuiltInApps = new Dictionary<string, TextLayout>
    {
        // Chat
        ["com.tencent.xinwechat"] = TextLayout.Chat,
        ["wechat"] = TextLayout.Chat,
        ["weixin"] = TextLayout.Chat,
        ["com.tinyspeck.slackmacgap"] = TextLayout.Chat,
        ["slack"] = TextLayout.Chat,
        ["com.hnc.discord"] = TextLayout.Chat,
        ["discord"] = TextLayout.Chat,
        ["ru.keepcoder.telegram"] = TextLayout.Chat,
        ["org.telegram.desktop"] = TextLayout.Chat,
        ["telegram"] = TextLayout.Chat,
        ["net.whatsapp.whatsapp"] = TextLayout.Chat,
        ["desktop.whatsapp"] = TextLayout.Chat,
        ["whatsapp"] = TextLayout.Chat,
        ["com.apple.mobilesms"] = TextLayout.Chat,
        ["com.microsoft.teams2"] = TextLayout.Chat,
        ["com.microsoft.teams"] = TextLayout.Chat,
        ["ms-teams"] = TextLayout.Chat,
        ["teams"] = TextLayout.Chat,
        ["com.tencent.qq"] = TextLayout.Chat,
        ["qq"] = TextLayout.Chat,
        ["com.alibaba.dingtalkmac"] = TextLayout.Chat,
        ["dingtalk"] = TextLayout.Chat,
        ["com.electron.lark"] = TextLayout.Chat,
        ["feishu"] = TextLayout.Chat,
        ["lark"] = TextLayout.Chat,
        ["jp.naver.line.mac"] = TextLayout.Chat,
        ["line"] = TextLayout.Chat,
        ["com.kakao.kakaotalkmac"] = TextLayout.Chat,
        ["kakaotalk"] = TextLayout.Chat,
        ["org.whispersystems.signal-desktop"] = TextLayout.Chat,
        ["signal"] = TextLayout.Chat,
        // Prompts for an AI, code editors, notes and documents
        ["com.anthropic.claudefordesktop"] = TextLayout.Structured,
        ["claude"] = TextLayout.Structured,
        ["com.openai.chat"] = TextLayout.Structured,
        ["chatgpt"] = TextLayout.Structured,
        ["com.todesktop.230313mzl4w4u92"] = TextLayout.Structured,
        ["cursor"] = TextLayout.Structured,
        ["com.microsoft.vscode"] = TextLayout.Structured,
        ["code"] = TextLayout.Structured,
        ["com.exafunction.windsurf"] = TextLayout.Structured,
        ["windsurf"] = TextLayout.Structured,
        ["dev.zed.zed"] = TextLayout.Structured,
        ["zed"] = TextLayout.Structured,
        ["notion.id"] = TextLayout.Structured,
        ["notion"] = TextLayout.Structured,
        ["md.obsidian"] = TextLayout.Structured,
        ["obsidian"] = TextLayout.Structured,
        ["com.microsoft.word"] = TextLayout.Structured,
        ["winword"] = TextLayout.Structured,
        ["com.apple.iwork.pages"] = TextLayout.Structured,
        // Mail
        ["com.apple.mail"] = TextLayout.Email,
        ["com.microsoft.outlook"] = TextLayout.Email,
        ["outlook"] = TextLayout.Email,
        ["olk"] = TextLayout.Email,
        ["org.mozilla.thunderbird"] = TextLayout.Email,
        ["thunderbird"] = TextLayout.Email,
    };

    /// <summary>The layout text dictated into <paramref name="app"/> gets when its profile doesn't set one; null when the app has none of its own.</summary>
    public static TextLayout? BuiltIn(string? app) =>
        app != null && BuiltInApps.TryGetValue(AppProfile.MatchKey(app), out var layout) ? layout : null;
}

/// <summary>
/// The language a transcript was spoken in, for the clean-up's examples in that language (see
/// <see cref="Prompts.LanguageExamples"/>): the speech-to-text language when one is set, otherwise guessed from the
/// transcript's script and its commonest words. Chinese and English have their examples in the prompt itself, so they,
/// a mix the guess can't tell apart, and anything unknown get none.
/// </summary>
public static class ExampleLanguage
{
    /// <summary>The languages with examples of their own, by speech-to-text language code.</summary>
    public static readonly string[] WithExamples = ["ja", "ko", "es", "pt", "fr", "de", "ru"];

    /// <summary>
    /// Words common in one of the Latin-script languages and rare in the others (and in English), lower-case. A word two
    /// of them share ("que", "de", "no", "la") is left out, so a match says something.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, HashSet<string>> CommonWords = new Dictionary<string, HashSet<string>>
    {
        ["en"] = ["the", "and", "is", "are", "to", "of", "it", "that", "this", "you", "we", "with", "for", "have", "was", "will", "can", "but", "not", "what", "so", "um", "uh"],
        ["es"] = ["el", "los", "las", "del", "es", "y", "pero", "una", "muy", "eso", "esto", "hay", "cuando", "sabes", "entonces", "bueno", "vale", "sea", "están", "tengo", "espera"],
        ["pt"] = ["o", "os", "dos", "é", "não", "você", "mas", "uma", "muito", "isso", "isto", "tem", "então", "tá", "né", "gente", "aí", "pra", "tipo", "pera", "eu"],
        ["fr"] = ["le", "les", "des", "est", "et", "je", "tu", "nous", "vous", "pas", "mais", "pour", "avec", "une", "très", "aussi", "ça", "il", "elle", "euh", "quoi", "coup", "fait", "c'est", "n'est"],
        ["de"] = ["der", "die", "das", "und", "ist", "nicht", "ich", "wir", "aber", "für", "mit", "eine", "ein", "sehr", "auch", "noch", "ja", "doch", "ähm", "äh", "wenn", "dann", "bitte", "nee"],
    };

    /// <summary>
    /// The language whose examples go with a clean-up of <paramref name="transcript"/>: <paramref name="hint"/> (the
    /// speech-to-text language, "" to auto-detect) when set, otherwise the guess. Null for Chinese, English and anything
    /// that can't be told.
    /// </summary>
    public static string? Resolve(string? hint, string transcript)
    {
        var code = (hint ?? "").Trim().ToLowerInvariant();
        if (code.Length > 0) return WithExamples.Contains(code) ? code : null;
        var guess = Detect(transcript);
        return guess != null && WithExamples.Contains(guess) ? guess : null;
    }

    /// <summary>
    /// The language <paramref name="text"/> is mostly in: Japanese when it has kana, Korean with Hangul, Russian with
    /// Cyrillic, Chinese with Han characters and none of those, and for Latin script the language with the most of its
    /// common words (at least two, and more than any other's). Null when it can't be told.
    /// </summary>
    public static string? Detect(string text)
    {
        int kana = 0, hangul = 0, cyrillic = 0, han = 0, latin = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x3040 and <= 0x30FF) kana++;
            else if (v is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) hangul++;
            else if (v is >= 0x0400 and <= 0x04FF) cyrillic++;
            else if (v is >= 0x4E00 and <= 0x9FFF) han++;
            else if (Rune.IsLetter(rune) && v < 0x0250) latin++;
        }
        if (kana >= 2) return "ja";
        if (hangul >= 2 && hangul >= han) return "ko";
        if (cyrillic >= 4 && cyrillic >= latin) return "ru";
        if (han >= 2 && han >= latin / 4) return "zh";
        var words = Words(text);
        string? best = null;
        int bestCount = 0, second = 0;
        foreach (var (language, common) in CommonWords)
        {
            var count = words.Count(common.Contains);
            if (count > bestCount)
            {
                second = bestCount;
                bestCount = count;
                best = language;
            }
            else if (count > second)
            {
                second = count;
            }
        }
        return bestCount >= 2 && bestCount > second ? best : null;
    }

    /// <summary>The words of <paramref name="text"/>, lower-case: runs of letters, with an apostrophe inside one kept ("c'est").</summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var lower = text.ToLowerInvariant().Replace('’', '\'');
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            if (char.IsLetter(c) || (c == '\'' && current.Length > 0 && i + 1 < lower.Length && char.IsLetter(lower[i + 1])))
            {
                current.Append(c);
                continue;
            }
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }
}
