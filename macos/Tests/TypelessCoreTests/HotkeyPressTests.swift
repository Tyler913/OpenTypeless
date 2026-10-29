import Foundation
import Testing
@testable import TypelessCore

/// Reading a hotkey press: tap or hold, key combinations, and which time a key event counts at.
@Suite struct HotkeyPressTests {
    @Test func holdOrTapIsMeasuredFromTheKeys() {
        #expect(HotkeyPress.isTap(pressedAt: 100, releasedAt: 100.2))
        #expect(!HotkeyPress.isTap(pressedAt: 100, releasedAt: 100.4))
        // A 1.2 s hold whose release was only handled after a 1.5 s microphone start is still a hold…
        #expect(!HotkeyPress.isTap(pressedAt: 100, releasedAt: 101.2))
        // …and a quick tap handled just as late is still a tap, as long as the events' own times are used.
        #expect(HotkeyPress.isTap(pressedAt: 100, releasedAt: 100.15))
    }

    @Test func anotherKeySoonAfterThePressIsACombination() {
        #expect(HotkeyPress.isCombo(pressedAt: 50, otherKeyAt: 50.4))
        #expect(!HotkeyPress.isCombo(pressedAt: 50, otherKeyAt: 51))
    }

    @Test func untrustworthyStampsFallBackToNow() {
        #expect(HotkeyPress.eventTime(99.7, now: 100) == 99.7)
        #expect(HotkeyPress.eventTime(100, now: 100) == 100)
        #expect(HotkeyPress.eventTime(0, now: 100) == 100) // posted with no time
        #expect(HotkeyPress.eventTime(100.5, now: 100) == 100) // in the future
        #expect(HotkeyPress.eventTime(80, now: 100) == 100) // implausibly old
    }

    private enum Mode: Equatable, Sendable { case dictation, translate }

    @Test func withNoChordRegisteredOtherKeysAreReadAsBefore() {
        let chords = ModeChords<Mode>()
        #expect(chords.isEmpty)
        #expect(chords.otherKey(18, pressedAt: 50, at: 50.4, handsFree: false) == .cancel)
        #expect(chords.otherKey(18, pressedAt: 50, at: 51, handsFree: false) == .ignore)
        #expect(chords.otherKey(18, pressedAt: 50, at: 50.4, handsFree: true) == .ignore)
    }

    @Test func aRegisteredChordSwitchesTheModeInsteadOfCancelling() {
        var chords = ModeChords<Mode>()
        chords.register(17, mode: .translate)
        #expect(chords.keys == [17])
        #expect(chords.mode(for: 17) == .translate)
        // Straight after the press, where any other key would cancel…
        #expect(chords.otherKey(17, pressedAt: 50, at: 50.2, handsFree: false) == .switchMode(.translate))
        // …and long after it, for as long as the hotkey is held.
        #expect(chords.otherKey(17, pressedAt: 50, at: 58, handsFree: false) == .switchMode(.translate))
        // Keys that aren't registered still make a combination.
        #expect(chords.otherKey(18, pressedAt: 50, at: 50.2, handsFree: false) == .cancel)

        chords.register(17, mode: .dictation)
        #expect(chords.otherKey(17, pressedAt: 50, at: 50.2, handsFree: false) == .switchMode(.dictation))
        chords.unregister(17)
        #expect(chords.isEmpty)
        #expect(chords.otherKey(17, pressedAt: 50, at: 50.2, handsFree: false) == .cancel)
    }
}

extension HotkeyPressTests {

    @Test func tapComboAndEventAgeBoundariesAreExclusive() {
        #expect(HotkeyPress.isTap(pressedAt: 0, releasedAt: HotkeyPress.tapThreshold.nextDown))
        #expect(!HotkeyPress.isTap(pressedAt: 0, releasedAt: HotkeyPress.tapThreshold))
        #expect(HotkeyPress.isCombo(pressedAt: 0, otherKeyAt: HotkeyPress.comboWindow.nextDown))
        #expect(!HotkeyPress.isCombo(pressedAt: 0, otherKeyAt: HotkeyPress.comboWindow))
        #expect(HotkeyPress.eventTime(90, now: 100) == 100)
        #expect(HotkeyPress.eventTime(90.001, now: 100) == 90.001)
        for stamp in [-1.0, .nan, .infinity, -.infinity] {
            #expect(HotkeyPress.eventTime(stamp, now: 100) == 100)
        }
    }
}
