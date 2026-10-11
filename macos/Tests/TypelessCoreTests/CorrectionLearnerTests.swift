import Foundation
import Testing
@testable import TypelessCore

@Suite struct CorrectionLearnerTests {
    private let common: Set<String> = ["logic", "there", "their", "hello", "send", "sent", "report", "the", "mode"]
    private func learn(_ original: String, _ edited: String) -> [Correction] {
        CorrectionLearner.corrections(original: original, edited: edited) { common.contains($0.lowercased()) }
    }

    @Test func learnsMisspelledProductNames() {
        #expect(learn("我在用 TypeList 写东西", "我在用 Typeless 写东西") == [Correction(heard: "TypeList", corrected: "Typeless")])
        #expect(learn("Logic 鼠标的按键", "Logitech 鼠标的按键") == [Correction(heard: "Logic", corrected: "Logitech")])
        #expect(learn("try Envious Sales today", "try EnviousSales today")
                == [Correction(heard: "Envious Sales", corrected: "EnviousSales")])
    }

    @Test func learnsChineseHomophones() {
        #expect(learn("我用的是逻辑鼠标", "我用的是罗技鼠标") == [Correction(heard: "逻辑", corrected: "罗技")])
    }

    @Test func oneWrongCharacterLearnsTheWholeWord() {
        #expect(learn("我买了一个罗级鼠标", "我买了一个罗技鼠标") == [Correction(heard: "罗级", corrected: "罗技")])
        // Names the segmenter doesn't know come out as single characters (千|问); the neighbours are taken in.
        #expect(learn("这个模型叫做千文", "这个模型叫做千问") == [Correction(heard: "千文", corrected: "千问")])
        #expect(learn("打开非书文档", "打开飞书文档") == [Correction(heard: "非书", corrected: "飞书")])
        // A particle fixed is grammar, not a word.
        #expect(learn("他高兴的跑了", "他高兴地跑了").isEmpty)
    }

    @Test func learnsEnglishThatSoundsAlike() {
        #expect(learn("部署到 Versel 上", "部署到 Vercel 上") == [Correction(heard: "Versel", corrected: "Vercel")])
        #expect(learn("我在用克劳德写代码", "我在用 Claude 写代码") == [Correction(heard: "克劳德", corrected: "Claude")])
        #expect(learn("返回一个杰森", "返回一个 JSON") == [Correction(heard: "杰森", corrected: "JSON")])
        #expect(learn("这个项目的前端我打算用瑞艾克特来写", "这个项目的前端我打算用 React 来写") == [Correction(heard: "瑞艾克特", corrected: "React")])
        #expect(CorrectionLearner.soundsAlike("Cooper Netties", "Kubernetes")) // KPRNTS / KBRNTS
    }

    @Test func learnsFixesThatAreMostOfAShortDictation() {
        // One fixed word is most of a short dictation; that's no rewrite.
        #expect(learn("我用克劳德", "我用 Claude") == [Correction(heard: "克劳德", corrected: "Claude")])
        #expect(learn("逻辑", "罗技") == [Correction(heard: "逻辑", corrected: "罗技")])
        // Two changes that leave little of it are.
        #expect(CorrectionLearner.review(original: "我用克劳德", edited: "你用 Claude 吗").skipped == "rewrite")
    }

    @Test func leavesPunctuationOutOfFixes() {
        #expect(learn("我在用 TypeList。", "我在用 Typeless") == [Correction(heard: "TypeList", corrected: "Typeless")])
        #expect(CorrectionLearner.review(original: "好的，明天见", edited: "好的。明天见").skipped == "punctuation only")
        // Joiners that belong to the word stay.
        #expect(learn("后端用 Note JS。", "后端用 Node.js。") == [Correction(heard: "Note JS", corrected: "Node.js")])
    }

    @Test func learnsEnglishTermsHeardAsChinese() {
        // From a real learning log: Chinese recognition wrote JetBrains as 经解散了, which sounds little like it.
        #expect(learn("我用的是经解散了。", "我用的是 Jetbrains") == [Correction(heard: "经解散了", corrected: "Jetbrains")])
        // Translating into an everyday word is an edit, not a mishearing.
        let review = CorrectionLearner.review(original: "去商店买东西", edited: "去 store 买东西") { $0 == "store" }
        #expect(review.learnable.isEmpty)
        #expect(CorrectionLearner.review(original: "去商店买东西", edited: "去 STORE 买东西") { $0 == "store" }.learnable.isEmpty)
    }

    @Test func ignoresPinyinReadMidComposition() {
        // A field read while the input method still shows the letters being typed.
        #expect(CorrectionLearner.review(original: "我用的是逻辑鼠标", edited: "我用的是luoj鼠标").learnable.isEmpty)
        #expect(learn("用经解散了", "用jetbrains").isEmpty)
    }

    @Test func findsAnEmptiedDictationOnlyWhenAsked() {
        #expect(CorrectionLearner.editedRegion(inserted: "克劳德", before: "克劳德", after: "", allowingBlank: true) == "")
        #expect(CorrectionLearner.editedRegion(inserted: "克劳德", before: "克劳德", after: "") == nil)
    }

    @Test func reviewSaysWhyAChangeWasNotLearned() {
        let review = CorrectionLearner.review(original: "Send the report to Mike", edited: "Send the report to Sarah")
        #expect(review.changes == [.init(correction: Correction(heard: "Mike", corrected: "Sarah"), rejected: "sounds different")])
        #expect(CorrectionLearner.review(original: "Please send the quarterly numbers to the finance team by Friday",
                                         edited: "Can we talk about this tomorrow instead").skipped == "rewrite")
    }

    @Test func learnsDistinctiveCasingOnly() {
        #expect(learn("写 swiftui 页面", "写 SwiftUI 页面") == [Correction(heard: "swiftui", corrected: "SwiftUI")])
        #expect(learn("hello there", "Hello there").isEmpty)
    }

    @Test func ignoresEditsThatAreNotMishearings() {
        #expect(learn("Send the report to Mike", "Send the report to Sarah").isEmpty)   // different name
        #expect(learn("大概 50 个", "大概 60 个").isEmpty)                                // changed number
        #expect(learn("I think their right", "I think there right").isEmpty)            // ordinary words
        #expect(learn("我们周三开会", "我们周四开会").isEmpty)                            // one character
        #expect(learn("这个功能不急", "这个功能不急，下个月再做").isEmpty)                    // pure insertion
        #expect(learn("Please send the quarterly numbers to the finance team by Friday",
                      "Can we talk about this tomorrow instead").isEmpty)              // rewrite
    }

    @Test func findsTheDictatedTextInTheField() {
        let before = "Hi team,\n我在用 TypeList 写东西\nThanks"
        #expect(CorrectionLearner.editedRegion(inserted: "我在用 TypeList 写东西", before: before,
                                               after: "Hi team,\n我在用 Typeless 写东西\nThanks") == "我在用 Typeless 写东西")
        // Sent and cleared, or edited outside the dictated text: can't tell what changed.
        #expect(CorrectionLearner.editedRegion(inserted: "我在用 TypeList 写东西", before: before, after: "") == nil)
        #expect(CorrectionLearner.editedRegion(inserted: "我在用 TypeList 写东西", before: before,
                                               after: "Hello team,\n我在用 TypeList 写东西\nThanks") == nil)
        // A composer that held only the dictation is cleared when it's sent: that is no fix, and must not
        // replace the fixed text seen just before.
        #expect(CorrectionLearner.editedRegion(inserted: "我在用 TypeList 写东西", before: "我在用 TypeList 写东西",
                                               after: "我在用 Typeless 写东西") == "我在用 Typeless 写东西")
        #expect(CorrectionLearner.editedRegion(inserted: "我在用 TypeList 写东西", before: "我在用 TypeList 写东西", after: "") == nil)
        #expect(CorrectionLearner.editedRegion(inserted: "我在用 TypeList 写东西", before: "我在用 TypeList 写东西", after: "\n") == nil)
    }

    @Test func tokenizesTechnicalWords() {
        #expect(CorrectionLearner.tokens("用 Node.js 和 C++ 写").map(\.text) == ["用", "Node.js", "和", "C++", "写"])
    }

    @Test func promptListsMisheardForms() {
        let prompt = Prompts.polishSystemPrompt(vocabulary: ["Typeless"], misheard: [Correction(heard: "TypeList", corrected: "Typeless")],
                                                extraInstructions: "")
        #expect(prompt.contains("spell them exactly like this: Typeless"))
        #expect(prompt.contains("“TypeList” → Typeless"))
    }
}
