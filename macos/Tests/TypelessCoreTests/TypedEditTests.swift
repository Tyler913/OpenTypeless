import Testing
@testable import TypelessCore

struct TypedEditTests {
    private func edit(_ pasted: String, _ keys: [TypedEdit.Key]) -> (text: String, followed: Bool) {
        var edit = TypedEdit(pasted: pasted)
        for key in keys where !edit.apply(key) { return (edit.text, false) }
        return (edit.text, true)
    }

    private func type(_ text: String) -> [TypedEdit.Key] { text.map { .insert(String($0)) } }

    @Test func backspacingTheLastWordAndRetypingIt() {
        let keys = Array(repeating: TypedEdit.Key.deleteBackward(.character), count: 7) + type("Kwazara")
        #expect(edit("deploy it to Quasera", keys) == ("deploy it to Kwazara", true))
    }

    @Test func optionBackspaceDeletesTheLastWord() {
        #expect(edit("deploy it to Quasera", [.deleteBackward(.word)] + type("Kwazara")) == ("deploy it to Kwazara", true))
        // Over the punctuation after it too.
        #expect(edit("ping Dravik.", [.deleteBackward(.word)] + type("Drovik.")) == ("ping Drovik.", true))
    }

    @Test func fixingAWordInTheMiddleWithTheArrows() {
        // "ping Dravik about it": back over " about it", fix the a.
        var keys: [TypedEdit.Key] = [.left(.word, extend: false), .left(.word, extend: false), .left(.character, extend: false)]
        keys += [.left(.character, extend: false), .left(.character, extend: false), .left(.character, extend: false)]
        keys += [.deleteBackward(.character), .insert("o")]
        #expect(edit("ping Dravik about it", keys) == ("ping Drovik about it", true))
    }

    @Test func selectingAWordAndTypingOverIt() {
        // To the start of the word, then select it forwards (from the start of "now", ⌥⇧← would take the space too).
        let keys: [TypedEdit.Key] = [.left(.word, extend: false), .left(.word, extend: false), .right(.word, extend: true)] + type("Drovik")
        #expect(edit("ping Dravik now", keys) == ("ping Drovik now", true))
    }

    @Test func aPasteReplacesTheSelection() {
        #expect(edit("use TypeList", [.left(.word, extend: true), .insert("Typeless")]) == ("use Typeless", true))
    }

    @Test func chineseIsFollowedCharacterByCharacter() {
        let keys: [TypedEdit.Key] = [.left(.character, extend: false), .left(.character, extend: false),
                                     .deleteBackward(.character), .deleteBackward(.character)] + type("罗技")
        #expect(edit("买了逻辑鼠标", keys) == ("买了罗技鼠标", true))
    }

    @Test func whatCantBeFollowedStopsIt() {
        // Past the start of the dictated text: whatever is before it in the field is unknown.
        #expect(edit("ab", [.deleteBackward(.character), .deleteBackward(.character), .deleteBackward(.character)]) == ("", false))
        #expect(edit("ab", [.right(.character, extend: false)]).followed == false)
        // A word jump across Chinese, where macOS uses a dictionary.
        #expect(edit("我们用罗技", [.left(.word, extend: false)]).followed == false)
        // A word that runs to the start of the dictated text may go on before it.
        #expect(edit("Quasera", [.deleteBackward(.word)]).followed == false)
        #expect(edit("x", [.insert("")]).followed == false)
    }

    @Test func theStateBeforeAnUnfollowableKeyIsKept() {
        let result = edit("to Quasera", Array(repeating: .deleteBackward(.character), count: 7) + type("Kwazara") + [.right(.character, extend: false)])
        #expect(result == ("to Kwazara", false))
    }
}
