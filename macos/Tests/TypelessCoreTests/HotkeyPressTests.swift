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
}
