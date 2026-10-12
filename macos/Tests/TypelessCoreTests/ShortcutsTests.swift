import Foundation
import Testing
@testable import TypelessCore

/// Action shortcuts: which action a key fires, which shortcuts are refused, and what "Paste last dictation" pastes.
@Suite struct ShortcutsTests {
    // macOS codes: V is 9, Esc 53, Space 49; ⌃ 0x40000, ⌥ 0x80000, ⌘ 0x100000.
    private let control: UInt64 = 0x40000, option: UInt64 = 0x80000, command: UInt64 = 0x100000
    private var pasteChord: KeyChord { KeyChord(keyCode: 9, modifiers: control | command) }
    private let fn = KeyChord(keyCode: 63, isModifierOnly: true)

    @Test func aKeyFiresTheActionWithExactlyItsModifiers() {
        let map = ShortcutMap([.pasteLastDictation: pasteChord])
        #expect(map.action(keyCode: 9, modifiers: control | command) == .pasteLastDictation)
        #expect(map.action(keyCode: 9, modifiers: command) == nil) // ⌘V stays the app's own paste
        #expect(map.action(keyCode: 9, modifiers: control | option | command) == nil)
        #expect(map.action(keyCode: 8, modifiers: control | command) == nil)
        #expect(ShortcutMap().action(keyCode: 9, modifiers: control | command) == nil)
    }

    @Test func aFunctionKeyAloneCanBeAShortcut() {
        let map = ShortcutMap([.pasteLastDictation: KeyChord(keyCode: 97)]) // F6
        #expect(map.action(keyCode: 97, modifiers: 0) == .pasteLastDictation)
        #expect(map.action(keyCode: 97, modifiers: command) == nil)
    }

    @Test func aModifierOnItsOwnNeverFires() {
        let map = ShortcutMap([.pasteLastDictation: KeyChord(keyCode: 54, isModifierOnly: true)])
        #expect(map.isEmpty)
        #expect(map.action(keyCode: 54, modifiers: 0) == nil)
        // A modifier on its own carries no modifiers to compare.
        #expect(KeyChord(keyCode: 54, modifiers: command, isModifierOnly: true).modifiers == 0)
    }

    @Test func shortcutsThatClashAreRefused() {
        let map = ShortcutMap()
        #expect(map.conflict(pasteChord, for: .pasteLastDictation, dictation: fn, escapeKeyCode: 53) == nil)
        #expect(map.conflict(KeyChord(keyCode: 55, isModifierOnly: true), for: .pasteLastDictation, dictation: fn,
                             escapeKeyCode: 53) == .modifierOnly)
        #expect(map.conflict(KeyChord(keyCode: 53, modifiers: command), for: .pasteLastDictation, dictation: fn,
                             escapeKeyCode: 53) == .escape)
        let optionSpace = KeyChord(keyCode: 49, modifiers: option)
        #expect(map.conflict(optionSpace, for: .pasteLastDictation, dictation: optionSpace, escapeKeyCode: 53) == .dictation)
        // The same key with other modifiers is a different shortcut.
        #expect(map.conflict(KeyChord(keyCode: 49, modifiers: option | command), for: .pasteLastDictation,
                             dictation: optionSpace, escapeKeyCode: 53) == nil)
        // Recording an action's own shortcut again is fine.
        let set = ShortcutMap([.pasteLastDictation: pasteChord])
        #expect(set.conflict(pasteChord, for: .pasteLastDictation, dictation: fn, escapeKeyCode: 53) == nil)
    }

    private func entry(_ kind: HistoryPruning.Kind, text: Bool = true, audio: Bool = true) -> LastDictation.Entry {
        LastDictation.Entry(kind: kind, hasText: text, hasAudio: audio)
    }

    @Test func pastesTheNewestFinishedDictation() {
        #expect(LastDictation.choose([entry(.finished), entry(.finished)]) == .paste(0))
        // Under way, cancelled with Esc, or no speech heard: passed over.
        #expect(LastDictation.choose([entry(.inProgress), entry(.cancelled), entry(.finished, text: false),
                                      entry(.finished)]) == .paste(3))
        #expect(LastDictation.choose([]) == .nothing)
        #expect(LastDictation.choose([entry(.cancelled), entry(.finished, text: false)]) == .nothing)
    }

    @Test func aFailedNewestDictationIsRetriedInstead() {
        #expect(LastDictation.choose([entry(.failed), entry(.finished)]) == .retry(0))
        #expect(LastDictation.choose([entry(.cancelled), entry(.failed, text: false)]) == .retry(1))
        // Its recording is gone: the newest finished one instead.
        #expect(LastDictation.choose([entry(.failed, audio: false), entry(.finished)]) == .paste(1))
        // An older failure doesn't stand in the way of a newer dictation that worked.
        #expect(LastDictation.choose([entry(.finished), entry(.failed)]) == .paste(0))
    }
}
