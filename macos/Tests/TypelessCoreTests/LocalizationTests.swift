import Foundation
import Testing
@testable import TypelessCore

/// The L(…) strings and the translations in i18n/strings.json.
@Suite struct LocalizationTests {
    @Test func interpolationsBecomeNumberedPlaceholders() {
        let version = "1.2.0", percent = 42
        let text: LocalizedText = "Downloading \(version)… \(percent)%"
        #expect(text.text == "Downloading 1.2.0… 42%")
        #expect(text.key == "Downloading {0}… {1}%")
        #expect(text.arguments == ["1.2.0", "42"])
    }

    @Test func chineseAndEnglishComeFromTheCode() {
        let version = "1.2.0"
        #expect(localized("有新版本 \(version)", "Version \(version) is available", in: .zh) == "有新版本 1.2.0")
        #expect(localized("有新版本 \(version)", "Version \(version) is available", in: .en) == "Version 1.2.0 is available")
    }

    @Test func otherLanguagesComeFromTheTable() {
        let version = "1.2.0"
        #expect(localized("有新版本 \(version)", "Version \(version) is available", in: .ja) == "バージョン 1.2.0 が利用可能です")
        #expect(localized("设置", "Settings", in: .de) == "Einstellungen")
        // A translation may reorder the placeholders.
        #expect(localized("x", "the download is version \("2"), not \("3")", in: .ko) == "다운로드한 버전이 3이 아니라 2입니다")
    }

    @Test func missingTranslationsFallBackToEnglish() {
        #expect(localized("没有翻译", "Not translated \(7)", in: .fr) == "Not translated 7")
    }

    @Test func everyTranslationKeepsThePlaceholders() {
        #expect(Translations.table.count > 200)
        let placeholder = #/\{\d+\}/#
        for (key, translations) in Translations.table {
            let expected = key.matches(of: placeholder).map { String($0.output) }.sorted()
            for language in AppLanguage.supported where language != .zh && language != .en {
                let text = translations[language.rawValue]
                #expect(text != nil, "\(key) has no \(language.rawValue) translation")
                #expect(text?.matches(of: placeholder).map { String($0.output) }.sorted() == expected, "\(key) → \(text ?? "")")
            }
        }
    }

    @Test func systemLanguagesMatchByLanguageCode() {
        #expect(AppLanguage.matching("pt-BR") == .pt)
        #expect(AppLanguage.matching("zh-Hant-TW") == .zh)
        #expect(AppLanguage.matching("ja_JP") == .ja)
        #expect(AppLanguage.matching("it-IT") == nil)
        #expect(AppLanguage.matching("system") == nil)
    }

    @Test func fillLeavesUnknownBracesAlone() {
        #expect(Translations.fill("{0} {x} {5} {", with: ["a"]) == "a {x} {5} {")
    }
}
