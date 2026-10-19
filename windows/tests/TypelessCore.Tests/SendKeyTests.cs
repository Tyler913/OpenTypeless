namespace TypelessCore.Tests;

/// <summary>"Finish and send": which key presses are the chosen send key, and how the choice is saved.</summary>
public class SendKeyTests
{
    // Windows codes: Enter is 0x0D (the keypad's too), Space 0x20; Ctrl 1, Alt 2, Shift 4, Win 8 (HotkeyModifiers).
    private const int Enter = 0x0D, Space = 0x20, Control = 1, Alt = 2, Shift = 4, Win = 8;
    private static readonly SendKeyCodes Codes = new(new HashSet<int> { Enter }, Shift, Control);

    [Fact]
    public void EnterWithExactlyItsModifiers()
    {
        Assert.True(SendKey.Enter.Matches(Enter, 0, 0, Codes));
        Assert.False(SendKey.Enter.Matches(Enter, Shift, 0, Codes));
        Assert.False(SendKey.Enter.Matches(Enter, Control, 0, Codes));
        Assert.True(SendKey.ShiftEnter.Matches(Enter, Shift, 0, Codes));
        Assert.False(SendKey.ShiftEnter.Matches(Enter, 0, 0, Codes));
        Assert.False(SendKey.ShiftEnter.Matches(Enter, Shift | Control, 0, Codes));
        Assert.True(SendKey.CommandEnter.Matches(Enter, Control, 0, Codes));
        Assert.False(SendKey.CommandEnter.Matches(Enter, Win, 0, Codes));
        // Alt + Enter is another key altogether.
        Assert.False(SendKey.Enter.Matches(Enter, Alt, 0, Codes));
    }

    [Fact]
    public void OtherKeysDont()
    {
        Assert.False(SendKey.Enter.Matches(Space, 0, 0, Codes));
        Assert.False(SendKey.CommandEnter.Matches(Space, Control, 0, Codes));
    }

    [Fact]
    public void OffMatchesNothing()
    {
        Assert.Null(SendKey.Off.Modifiers(Codes));
        foreach (var modifiers in new[] { 0, Shift, Control, Alt, Win })
        {
            Assert.False(SendKey.Off.Matches(Enter, modifiers, 0, Codes));
        }
    }

    [Fact]
    public void TheHeldHotkeysModifiersDontCount()
    {
        // Holding Right Ctrl to talk: Enter is still Enter, and Ctrl + Enter still Ctrl + Enter.
        Assert.True(SendKey.Enter.Matches(Enter, Control, Control, Codes));
        Assert.True(SendKey.CommandEnter.Matches(Enter, Control, Control, Codes));
        Assert.True(SendKey.ShiftEnter.Matches(Enter, Shift | Control, Control, Codes));
        // Holding Right Alt, which brings a Ctrl along on AltGr keyboard layouts.
        Assert.True(SendKey.Enter.Matches(Enter, Alt | Control, Alt | Control, Codes));
        // Holding Alt + Space: its Alt doesn't count either, but nothing else is forgiven.
        Assert.True(SendKey.Enter.Matches(Enter, Alt, Alt, Codes));
        Assert.False(SendKey.Enter.Matches(Enter, Alt | Win, Alt, Codes));
        // A hotkey modifier still has to be pressed for a send key that needs it.
        Assert.False(SendKey.CommandEnter.Matches(Enter, 0, Control, Codes));
    }

    [Fact]
    public void RightWithTheHotkeyItsAKeyCombination()
    {
        Assert.True(SendKeys.IsCombination(100, 100.1));
        Assert.True(SendKeys.IsCombination(100, 100.39));
        Assert.False(SendKeys.IsCombination(100, 100.4));
        Assert.False(SendKeys.IsCombination(100, 103));
    }

    [Fact]
    public void SavedChoicesRoundTrip()
    {
        foreach (var key in SendKeys.All) Assert.Equal(key, SendKeys.Parse(key.RawValue()));
        Assert.Equal(["off", "enter", "shiftEnter", "commandEnter"], SendKeys.All.Select(key => key.RawValue())); // as on macOS
        // Nothing saved yet, or a choice from a newer version: off, never a key that sends.
        Assert.Equal(SendKey.Off, SendKeys.Parse(null));
        Assert.Equal(SendKey.Off, SendKeys.Parse(""));
        Assert.Equal(SendKey.Off, SendKeys.Parse("altEnter"));
    }
}
