namespace TypelessCore.Tests;

/// <summary>Following the keys held from a keyboard hook's events: auto-repeats, key-ups, and key-ups that were missed.</summary>
public class HeldKeysTests
{
    private const int F9 = 0x78, Z = 0x5A;

    [Fact]
    public void AKeyDownForAKeyAlreadyDownIsAnAutoRepeat()
    {
        var keys = new HeldKeys();
        Assert.False(keys.Note(F9, down: true, 1_000)); // the press
        Assert.True(keys.Note(F9, down: true, 1_500)); // the first repeat, after the keyboard's delay
        Assert.True(keys.Note(F9, down: true, 1_533));
        Assert.True(keys.IsHeld(F9, 1_600));
        Assert.False(keys.IsHeld(Z, 1_600));

        Assert.False(keys.Note(F9, down: false, 1_700));
        Assert.False(keys.IsHeld(F9, 1_700));
        Assert.False(keys.Note(F9, down: true, 1_800)); // pressed again
    }

    [Fact]
    public void AKeyUpThatWasMissedDoesntLastForever()
    {
        var keys = new HeldKeys();
        keys.Note(Z, down: true, 10_000u);
        // No repeat and no key-up for longer than any keyboard repeats: the key-up went by unseen.
        Assert.False(keys.IsHeld(Z, 10_000u + HeldKeys.TimeoutMs));
        Assert.False(keys.Note(Z, down: true, 10_000u + HeldKeys.TimeoutMs + 100u)); // a new press, not a repeat
    }

    [Fact]
    public void TimesAreReadAcrossTheTickCountWrap()
    {
        var keys = new HeldKeys();
        keys.Note(F9, down: true, 0xFFFF_FF00);
        Assert.True(keys.Note(F9, down: true, 0x0000_0100)); // 512 ms later, after the wrap
        Assert.True(keys.IsHeld(F9, 0x0000_0200));
    }
}
