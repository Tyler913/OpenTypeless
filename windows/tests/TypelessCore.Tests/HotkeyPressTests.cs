namespace TypelessCore.Tests;

/// <summary>Reading a hotkey press: tap or hold, key combinations, and which time a key event counts at.</summary>
public class HotkeyPressTests
{

    [Fact]
    public void TapComboAndEventAgeBoundariesAreExclusive()
    {
        Assert.True(HotkeyPress.IsTap(0, Math.BitDecrement(HotkeyPress.TapThreshold)));
        Assert.False(HotkeyPress.IsTap(0, HotkeyPress.TapThreshold));
        Assert.True(HotkeyPress.IsCombo(0, Math.BitDecrement(HotkeyPress.ComboWindow)));
        Assert.False(HotkeyPress.IsCombo(0, HotkeyPress.ComboWindow));
        Assert.Equal(100, HotkeyPress.EventTime(90, 100));
        Assert.Equal(90.001, HotkeyPress.EventTime(90.001, 100));
        foreach (var stamp in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Equal(100, HotkeyPress.EventTime(stamp, 100));
    }

    [Fact]
    public void HoldOrTapIsMeasuredFromTheKeys()
    {
        Assert.True(HotkeyPress.IsTap(100, 100.2));
        Assert.False(HotkeyPress.IsTap(100, 100.4));
        // A 1.2 s hold whose release was only handled after a 1.5 s microphone start is still a hold…
        Assert.False(HotkeyPress.IsTap(100, 101.2));
        // …and a quick tap handled just as late is still a tap, as long as the events' own times are used.
        Assert.True(HotkeyPress.IsTap(100, 100.15));
    }

    [Fact]
    public void AnotherKeySoonAfterThePressIsACombination()
    {
        Assert.True(HotkeyPress.IsCombo(50, 50.4));
        Assert.False(HotkeyPress.IsCombo(50, 51));
    }

    [Fact]
    public void UntrustworthyStampsFallBackToNow()
    {
        Assert.Equal(99.7, HotkeyPress.EventTime(99.7, 100));
        Assert.Equal(100.0, HotkeyPress.EventTime(100, 100));
        Assert.Equal(100.0, HotkeyPress.EventTime(0, 100)); // sent with no time
        Assert.Equal(100.0, HotkeyPress.EventTime(100.5, 100)); // in the future
        Assert.Equal(100.0, HotkeyPress.EventTime(80, 100)); // implausibly old
    }

    [Fact]
    public void HookTimesAreReadAcrossTheTickCountWrap()
    {
        Assert.Equal(99.97, HotkeyPress.TickTime(99_970, 100_000), 9);
        // The 32-bit event time wrapped 16 ms before now; the event itself was 32 ms ago.
        const long now = 0x1_0000_0010;
        Assert.Equal((now - 32) / 1000.0, HotkeyPress.TickTime(0xFFFF_FFF0, now), 9);
        // An event time from after "now" is an untrustworthy stamp, not one 49.7 days old.
        Assert.Equal(100.0, HotkeyPress.TickTime(100_005, 100_000), 9);
    }

    private enum Mode { Dictation, Translate }

    [Fact]
    public void WithNoChordRegisteredOtherKeysAreReadAsBefore()
    {
        var chords = new ModeChords<Mode>();
        Assert.True(chords.IsEmpty);
        Assert.Equal(new OtherKeyOutcome<Mode>.Cancel(), chords.OtherKey(0x43, 50, 50.4, handsFree: false));
        Assert.Equal(new OtherKeyOutcome<Mode>.Ignore(), chords.OtherKey(0x43, 50, 51, handsFree: false));
        Assert.Equal(new OtherKeyOutcome<Mode>.Ignore(), chords.OtherKey(0x43, 50, 50.4, handsFree: true));
    }

    [Fact]
    public void ARegisteredChordSwitchesTheModeInsteadOfCancelling()
    {
        var chords = new ModeChords<Mode>();
        chords.Register(0x54, Mode.Translate);
        Assert.True(chords.Keys.SetEquals([0x54]));
        Assert.True(chords.TryGetMode(0x54, out var mode));
        Assert.Equal(Mode.Translate, mode);
        // Straight after the press, where any other key would cancel…
        Assert.Equal(new OtherKeyOutcome<Mode>.SwitchMode(Mode.Translate), chords.OtherKey(0x54, 50, 50.2, handsFree: false));
        // …and long after it, for as long as the hotkey is held.
        Assert.Equal(new OtherKeyOutcome<Mode>.SwitchMode(Mode.Translate), chords.OtherKey(0x54, 50, 58, handsFree: false));
        // Keys that aren't registered still make a combination.
        Assert.Equal(new OtherKeyOutcome<Mode>.Cancel(), chords.OtherKey(0x43, 50, 50.2, handsFree: false));

        chords.Register(0x54, Mode.Dictation);
        Assert.Equal(new OtherKeyOutcome<Mode>.SwitchMode(Mode.Dictation), chords.OtherKey(0x54, 50, 50.2, handsFree: false));
        chords.Unregister(0x54);
        Assert.True(chords.IsEmpty);
        Assert.False(chords.TryGetMode(0x54, out _));
        Assert.Equal(new OtherKeyOutcome<Mode>.Cancel(), chords.OtherKey(0x54, 50, 50.2, handsFree: false));
    }
}
