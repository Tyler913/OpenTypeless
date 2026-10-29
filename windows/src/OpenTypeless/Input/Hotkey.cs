using System.Text.Json.Serialization;
using OpenTypeless.Native;
using TypelessCore;

namespace OpenTypeless.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// The dictation trigger: either a single modifier key held on its own (Right Ctrl, Right Alt …) or a
/// key combination (Alt + Space, Ctrl + Shift + D, F5 …). Key codes are Windows virtual-key codes.
/// </summary>
public sealed record Hotkey(
    [property: JsonPropertyName("keyCode")] int KeyCode,
    /// <summary>Required modifiers for a combination.</summary>
    [property: JsonPropertyName("modifiers")] HotkeyModifiers Modifiers,
    [property: JsonPropertyName("isModifierOnly")] bool IsModifierOnly,
    /// <summary>Key name captured when recording a combination ("Space", "D", "F5").</summary>
    [property: JsonPropertyName("keyLabel")] string KeyLabel = "")
{
    public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_ESCAPE = 0x1B;

    public static readonly Hotkey RightControl = new(VK_RCONTROL, HotkeyModifiers.None, true);
    public static readonly Hotkey RightAlt = new(VK_RMENU, HotkeyModifiers.None, true);
    public static readonly Hotkey RightShift = new(VK_RSHIFT, HotkeyModifiers.None, true);

    /// <summary>
    /// Windows keyboards don't expose Fn to software, so the default is Right Alt: rarely used on its own, and on
    /// every keyboard, unlike Right Ctrl, which Copilot+ PCs replace with the Copilot key. A lone tap is masked so it
    /// doesn't open the menu bar, and AltGr combinations still type (they cancel the dictation instead).
    /// Installs from before 1.4.0 keep Right Ctrl (see AppSettings).
    /// </summary>
    public static readonly Hotkey Default = RightAlt;
    public static readonly Hotkey[] Presets = [RightAlt, RightControl, RightShift];

    /// <summary>Virtual-key code → the modifier it represents, for keys usable on their own.</summary>
    public static readonly IReadOnlyDictionary<int, HotkeyModifiers> ModifierKeys = new Dictionary<int, HotkeyModifiers>
    {
        [VK_LCONTROL] = HotkeyModifiers.Control, [VK_RCONTROL] = HotkeyModifiers.Control,
        [VK_LMENU] = HotkeyModifiers.Alt, [VK_RMENU] = HotkeyModifiers.Alt,
        [VK_LSHIFT] = HotkeyModifiers.Shift, [VK_RSHIFT] = HotkeyModifiers.Shift,
        [VK_LWIN] = HotkeyModifiers.Win, [VK_RWIN] = HotkeyModifiers.Win,
    };

    public static bool IsFunctionKey(int vk) => vk is >= 0x70 and <= 0x87; // F1–F24

    private static readonly Dictionary<int, string> NamedKeys = new()
    {
        [0x20] = "Space", [0x0D] = "Enter", [0x09] = "Tab", [0x08] = "Backspace", [0x2E] = "Delete", [0x2D] = "Insert",
        [0x25] = "←", [0x27] = "→", [0x26] = "↑", [0x28] = "↓", [0x24] = "Home", [0x23] = "End",
        [0x21] = "PgUp", [0x22] = "PgDn", [0x13] = "Pause", [0x91] = "ScrLk", [0x2C] = "PrtSc", [0x5D] = "Menu",
        [0x6A] = "Num *", [0x6B] = "Num +", [0x6D] = "Num -", [0x6E] = "Num .", [0x6F] = "Num /",
    };

    [JsonIgnore]
    public HotkeyModifiers ModifierFlag => ModifierKeys.TryGetValue(KeyCode, out var flag) ? flag : HotkeyModifiers.None;

    /// <summary>Alt and Win open the menu bar / Start menu when tapped alone, so their taps need masking.</summary>
    [JsonIgnore]
    public bool NeedsMenuMask => IsModifierOnly && ModifierFlag is HotkeyModifiers.Alt or HotkeyModifiers.Win;

    /// <summary>Pieces shown as separate key caps, e.g. ["Alt", "Space"] or ["Right Ctrl"].</summary>
    [JsonIgnore]
    public IReadOnlyList<string> KeyCaps
    {
        get
        {
            if (IsModifierOnly) return [ModifierName(KeyCode)];
            var caps = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) caps.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) caps.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) caps.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) caps.Add("Win");
            caps.Add(KeyLabel);
            return caps;
        }
    }

    [JsonIgnore]
    public string DisplayName => string.Join(IsModifierOnly ? "" : " + ", KeyCaps);

    /// <summary>Ctrl+letter combos shadow everyday shortcuts (copy, paste…) system-wide.</summary>
    [JsonIgnore]
    public bool ShadowsCommonShortcut => !IsModifierOnly && Modifiers == HotkeyModifiers.Control;

    /// <summary>
    /// Ctrl + Alt with a key that types something is AltGr on layouts such as German or French (AltGr + Q is @ there),
    /// so as a shortcut, which is kept from the focused app, it would take those characters away.
    /// </summary>
    [JsonIgnore]
    public bool BlocksAltGr =>
        !IsModifierOnly
        && (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == (HotkeyModifiers.Control | HotkeyModifiers.Alt)
        && KeyCode is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    /// <summary>As shortcuts are compared (see <see cref="ShortcutMap"/>).</summary>
    [JsonIgnore]
    public KeyChord Chord => new(KeyCode, (int)Modifiers, IsModifierOnly);

    public static string ModifierName(int keyCode)
    {
        var left = L("左 ", "Left ");
        var right = L("右 ", "Right ");
        return keyCode switch
        {
            VK_RCONTROL => right + "Ctrl",
            VK_LCONTROL => left + "Ctrl",
            VK_RMENU => right + "Alt",
            VK_LMENU => left + "Alt",
            VK_RSHIFT => right + "Shift",
            VK_LSHIFT => left + "Shift",
            VK_RWIN => right + "Win",
            VK_LWIN => left + "Win",
            _ => "?",
        };
    }

    public static string Label(int keyCode)
    {
        if (IsFunctionKey(keyCode)) return "F" + (keyCode - 0x6F);
        if (NamedKeys.TryGetValue(keyCode, out var name)) return name;
        if (keyCode is >= 0x60 and <= 0x69) return "Num " + (keyCode - 0x60);
        if (keyCode is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)keyCode).ToString();
        var ch = Win32.MapVirtualKey((uint)keyCode, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFF;
        return ch > 0x20 ? char.ToUpperInvariant((char)ch).ToString() : $"#{keyCode}";
    }
}

public static class ShortcutActionText
{
    public static string Title(this ShortcutAction action) => action switch
    {
        ShortcutAction.PasteLastDictation => L("粘贴上一次听写", "Paste last dictation"),
        _ => action.ToString(),
    };

    public static string Subtitle(this ShortcutAction action) => action switch
    {
        ShortcutAction.PasteLastDictation => L("把最近一次听写的文字再插入到光标处；如果它转写失败了，会先重新转写",
                                               "Inserts your latest dictation at the cursor again; if it failed, it's transcribed again first"),
        _ => "",
    };

    public static string Message(this ShortcutConflict conflict) => conflict switch
    {
        ShortcutConflict.ModifierOnly => L("单独的修饰键不能用作这个快捷键，请再加一个键。", "A modifier on its own can't be used here — add a key to it."),
        ShortcutConflict.Escape => L("Esc 用来取消听写，请换一个键。", "Esc cancels a dictation — pick another key."),
        ShortcutConflict.Dictation => L("这是听写快捷键，请换一个。", "That's the dictation shortcut — pick another."),
        ShortcutConflict.Action taken => L($"已被「{taken.Other.Title()}」使用，请换一个。", $"Already used by “{taken.Other.Title()}” — pick another."),
        _ => "",
    };
}
