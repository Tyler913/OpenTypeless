import Foundation
import Testing
@testable import TypelessCore

/// "Finish and send": which key presses are the chosen send key, and how the choice is saved.
@Suite struct SendKeyTests {
    // macOS codes: Return is 36, keypad Enter 76, Space 49; ⌃ 0x40000, ⌥ 0x80000, ⇧ 0x20000, ⌘ 0x100000.
    private let control: UInt64 = 0x40000, option: UInt64 = 0x80000, shift: UInt64 = 0x20000, command: UInt64 = 0x100000
    private var codes: SendKeyCodes { SendKeyCodes(returnKeys: [36, 76], shift: shift, command: command) }

    @Test func returnWithExactlyItsModifiers() {
        #expect(SendKey.enter.matches(keyCode: 36, modifiers: 0, codes: codes))
        #expect(!SendKey.enter.matches(keyCode: 36, modifiers: shift, codes: codes))
        #expect(!SendKey.enter.matches(keyCode: 36, modifiers: command, codes: codes))
        #expect(SendKey.shiftEnter.matches(keyCode: 36, modifiers: shift, codes: codes))
        #expect(!SendKey.shiftEnter.matches(keyCode: 36, modifiers: 0, codes: codes))
        #expect(!SendKey.shiftEnter.matches(keyCode: 36, modifiers: shift | command, codes: codes))
        #expect(SendKey.commandEnter.matches(keyCode: 36, modifiers: command, codes: codes))
        #expect(!SendKey.commandEnter.matches(keyCode: 36, modifiers: control, codes: codes))
        // ⌥Return is another key altogether.
        #expect(!SendKey.enter.matches(keyCode: 36, modifiers: option, codes: codes))
    }

    @Test func theKeypadsEnterCountsAndOtherKeysDont() {
        // Fn+Return on a Mac laptop is the keypad's Enter.
        #expect(SendKey.enter.matches(keyCode: 76, modifiers: 0, codes: codes))
        #expect(SendKey.shiftEnter.matches(keyCode: 76, modifiers: shift, codes: codes))
        #expect(!SendKey.enter.matches(keyCode: 49, modifiers: 0, codes: codes))
    }

    @Test func offMatchesNothing() {
        #expect(SendKey.off.modifiers(codes) == nil)
        for modifiers in [0, shift, command, control, option] {
            #expect(!SendKey.off.matches(keyCode: 36, modifiers: modifiers, codes: codes))
        }
    }

    @Test func theHeldHotkeysModifiersDontCount() {
        // Holding right ⌘ to talk: Return is still Return, and ⌘Return still ⌘Return.
        #expect(SendKey.enter.matches(keyCode: 36, modifiers: command, hotkeyModifiers: command, codes: codes))
        #expect(SendKey.commandEnter.matches(keyCode: 36, modifiers: command, hotkeyModifiers: command, codes: codes))
        #expect(SendKey.shiftEnter.matches(keyCode: 36, modifiers: shift | command, hotkeyModifiers: command, codes: codes))
        // Holding ⌥Space: its ⌥ doesn't count either, but nothing else is forgiven.
        #expect(SendKey.enter.matches(keyCode: 36, modifiers: option, hotkeyModifiers: option, codes: codes))
        #expect(!SendKey.enter.matches(keyCode: 36, modifiers: option | control, hotkeyModifiers: option, codes: codes))
        // A hotkey modifier still has to be pressed for a send key that needs it.
        #expect(!SendKey.commandEnter.matches(keyCode: 36, modifiers: 0, hotkeyModifiers: command, codes: codes))
    }

    @Test func rightWithTheHotkeyItsAKeyCombination() {
        #expect(SendKey.isCombination(hotkeyPressedAt: 100, at: 100.1))
        #expect(SendKey.isCombination(hotkeyPressedAt: 100, at: 100.39))
        #expect(!SendKey.isCombination(hotkeyPressedAt: 100, at: 100.4))
        #expect(!SendKey.isCombination(hotkeyPressedAt: 100, at: 103))
    }

    @Test func savedChoicesRoundTrip() {
        for key in SendKey.allCases {
            #expect(SendKey(saved: key.rawValue) == key)
        }
        #expect(SendKey.allCases.map(\.rawValue) == ["off", "enter", "shiftEnter", "commandEnter"]) // as on Windows
        // Nothing saved yet, or a choice from a newer version: off, never a key that sends.
        #expect(SendKey(saved: nil) == .off)
        #expect(SendKey(saved: "") == .off)
        #expect(SendKey(saved: "altEnter") == .off)
    }
}
