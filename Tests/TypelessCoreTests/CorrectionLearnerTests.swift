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
