import Foundation

/// How much structure a clean-up gives the text, by where it goes (see `builtIn(for:)`): a chat message is one block, a
/// prompt for an AI or a document gets paragraphs and lists more readily, an email puts a dictated greeting and sign-off
/// on lines of their own. Only the layout changes, never the words. `.natural` is the clean-up prompt's own layout and
/// adds nothing to it, so the system prompt stays as it was for every app not classified.
public enum TextLayout: String, CaseIterable, Codable, Sendable, Identifiable {
    case natural, chat, structured, email

    public var id: String { rawValue }

    /// Apps whose text has an obvious layout, by `AppProfile.matchKey` of their bundle ID (macOS) or executable
    /// (Windows): chat apps, where a message is one block; AI chats, code editors and note or document apps, where a
    /// dictated prompt or note reads better with paragraphs and lists; and mail apps. An app's profile overrides this, and
    /// any other app gets `.natural`. Browsers aren't here: what's in the tab isn't known. Shared with the Windows app,
    /// which has the same table.
    static let builtInApps: [String: TextLayout] = [
        // Chat
        "com.tencent.xinwechat": .chat,
        "wechat": .chat,
        "weixin": .chat,
        "com.tinyspeck.slackmacgap": .chat,
        "slack": .chat,
        "com.hnc.discord": .chat,
        "discord": .chat,
        "ru.keepcoder.telegram": .chat,
        "org.telegram.desktop": .chat,
        "telegram": .chat,
        "net.whatsapp.whatsapp": .chat,
        "desktop.whatsapp": .chat,
        "whatsapp": .chat,
        "com.apple.mobilesms": .chat,
        "com.microsoft.teams2": .chat,
        "com.microsoft.teams": .chat,
        "ms-teams": .chat,
        "teams": .chat,
        "com.tencent.qq": .chat,
        "qq": .chat,
        "com.alibaba.dingtalkmac": .chat,
        "dingtalk": .chat,
        "com.electron.lark": .chat,
        "feishu": .chat,
        "lark": .chat,
        "jp.naver.line.mac": .chat,
        "line": .chat,
        "com.kakao.kakaotalkmac": .chat,
        "kakaotalk": .chat,
        "org.whispersystems.signal-desktop": .chat,
        "signal": .chat,
        // Prompts for an AI, code editors, notes and documents
        "com.anthropic.claudefordesktop": .structured,
        "claude": .structured,
        "com.openai.chat": .structured,
        "chatgpt": .structured,
        "com.todesktop.230313mzl4w4u92": .structured,
        "cursor": .structured,
        "com.microsoft.vscode": .structured,
        "code": .structured,
        "com.exafunction.windsurf": .structured,
        "windsurf": .structured,
        "dev.zed.zed": .structured,
        "zed": .structured,
        "notion.id": .structured,
        "notion": .structured,
        "md.obsidian": .structured,
        "obsidian": .structured,
        "com.microsoft.word": .structured,
        "winword": .structured,
        "com.apple.iwork.pages": .structured,
        // Mail
        "com.apple.mail": .email,
        "com.microsoft.outlook": .email,
        "outlook": .email,
        "olk": .email,
        "org.mozilla.thunderbird": .email,
        "thunderbird": .email,
    ]

    /// The layout text dictated into `app` gets when its profile doesn't set one; nil when the app has none of its own.
    public static func builtIn(for app: String?) -> TextLayout? {
        guard let app else { return nil }
        return builtInApps[AppProfile.matchKey(app)]
    }
}

/// The language a transcript was spoken in, for the clean-up's examples in that language (see
/// `Prompts.languageExamples`): the speech-to-text language when one is set, otherwise guessed from the transcript's
/// script and its commonest words. Chinese and English have their examples in the prompt itself, so they, a mix the guess
/// can't tell apart, and anything unknown get none.
public enum ExampleLanguage {
    /// The languages with examples of their own, by speech-to-text language code.
    public static let withExamples = ["ja", "ko", "es", "pt", "fr", "de", "ru"]

    /// Words common in one of the Latin-script languages and rare in the others (and in English), lower-case. A word two
    /// of them share ("que", "de", "no", "la") is left out, so a match says something.
    static let commonWords: [(language: String, words: Set<String>)] = [
        ("en", ["the", "and", "is", "are", "to", "of", "it", "that", "this", "you", "we", "with", "for", "have", "was", "will", "can", "but", "not", "what", "so", "um", "uh"]),
        ("es", ["el", "los", "las", "del", "es", "y", "pero", "una", "muy", "eso", "esto", "hay", "cuando", "sabes", "entonces", "bueno", "vale", "sea", "están", "tengo", "espera", "lunes", "martes", "miércoles", "jueves", "viernes", "hoy", "mañana", "sí"]),
        ("pt", ["o", "os", "dos", "é", "não", "você", "mas", "uma", "muito", "isso", "isto", "tem", "então", "tá", "né", "gente", "aí", "pra", "tipo", "pera", "eu", "segunda", "terça", "quarta", "quinta", "sexta", "hoje", "amanhã", "sim"]),
        ("fr", ["le", "les", "des", "est", "et", "je", "tu", "nous", "vous", "pas", "mais", "pour", "avec", "une", "très", "aussi", "ça", "il", "elle", "euh", "quoi", "coup", "fait", "c'est", "n'est", "non", "attends", "oui", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "matin"]),
        ("de", ["der", "die", "das", "und", "ist", "nicht", "ich", "wir", "aber", "für", "mit", "eine", "ein", "sehr", "auch", "noch", "ja", "doch", "ähm", "äh", "wenn", "dann", "bitte", "nee", "nein", "montag", "dienstag", "mittwoch", "donnerstag", "freitag", "heute", "morgen"]),
    ]

    /// The language whose examples go with a clean-up of `transcript`: `hint` (the speech-to-text language, "" to
    /// auto-detect) when set, otherwise the guess. Nil for Chinese, English and anything that can't be told.
    public static func resolve(hint: String?, transcript: String) -> String? {
        let code = (hint ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if !code.isEmpty { return withExamples.contains(code) ? code : nil }
        guard let guess = detect(transcript), withExamples.contains(guess) else { return nil }
        return guess
    }

    /// The language `text` is mostly in: Japanese when it has kana, Korean with Hangul, Russian with Cyrillic, Chinese
    /// with Han characters and none of those, and for Latin script the language with the most of its common words (at
    /// least two, and more than any other's). Nil when it can't be told.
    public static func detect(_ text: String) -> String? {
        var kana = 0, hangul = 0, cyrillic = 0, han = 0, latin = 0
        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x3040...0x30FF: kana += 1
            case 0xAC00...0xD7AF, 0x1100...0x11FF, 0x3130...0x318F: hangul += 1
            case 0x0400...0x04FF: cyrillic += 1
            case 0x4E00...0x9FFF: han += 1
            case 0..<0x0250 where scalar.properties.isAlphabetic: latin += 1
            default: break
            }
        }
        if kana >= 2 { return "ja" }
        if hangul >= 2, hangul >= han { return "ko" }
        if cyrillic >= 4, cyrillic >= latin { return "ru" }
        if han >= 2, han >= latin / 4 { return "zh" }
        let words = Self.words(text)
        var best: String?
        var bestCount = 0, second = 0
        for (language, common) in commonWords {
            let count = words.filter { common.contains($0) }.count
            if count > bestCount {
                second = bestCount
                bestCount = count
                best = language
            } else if count > second {
                second = count
            }
        }
        return bestCount >= 2 && bestCount > second ? best : nil
    }

    /// The words of `text`, lower-case: runs of letters, with an apostrophe inside one kept ("c'est").
    static func words(_ text: String) -> [String] {
        let characters = Array(text.lowercased().replacingOccurrences(of: "’", with: "'"))
        var words: [String] = []
        var current = ""
        for (index, character) in characters.enumerated() {
            if character.isLetter
                || (character == "'" && !current.isEmpty && index + 1 < characters.count && characters[index + 1].isLetter) {
                current.append(character)
                continue
            }
            if !current.isEmpty { words.append(current) }
            current = ""
        }
        if !current.isEmpty { words.append(current) }
        return words
    }
}
