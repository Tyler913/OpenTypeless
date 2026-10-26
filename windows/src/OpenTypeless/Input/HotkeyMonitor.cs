using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using OpenTypeless.Native;
using TypelessCore;

namespace OpenTypeless.Input;

/// <summary>A key event as seen by the low-level hook (used while recording a new shortcut).</summary>
public readonly record struct KeyEvent(int VirtualKey, bool IsDown, bool IsInjected);

/// <summary>
/// A key going down, for following an edit after a paste (see EditWatcher): the text it types with the current keyboard
/// layout (null for keys that type nothing), the modifiers held, and whether an input method such as Pinyin is active,
/// whose keys spell a word before it's chosen.
/// </summary>
public readonly record struct TypedKey(int VirtualKey, string? Text, bool Ctrl, bool Alt, bool Shift, bool Win, bool InputMethod, bool IsInjected);

/// <summary>A mouse button going down or up (<c>WM_*BUTTON*</c>), where on screen, and when (ms, as in window messages).</summary>
public readonly record struct MouseButton(int Message, int X, int Y, uint Time);

/// <summary>
/// Listens globally for the configured hotkey via a low-level keyboard hook (<c>WH_KEYBOARD_LL</c>), the
/// Windows counterpart of the macOS CGEventTap.
///
/// The hook runs on its own thread with its own message loop, so a busy UI thread can never make Windows
/// drop it for being slow. Everything is passed through untouched except the keystrokes of a key-combination
/// hotkey, which are swallowed so that e.g. Alt+Space doesn't also reach the focused app, Esc while a dictation
/// is under way, and the keys of an action's shortcut or of a mode chord.
///
/// The press, release and other-key callbacks get the key event's own time, in seconds on the
/// <see cref="Environment.TickCount64"/> clock (see <see cref="HotkeyPress"/>).
/// </summary>
public sealed class HotkeyMonitor
{
    public static HotkeyMonitor Shared { get; } = new();

    public Action<double>? OnPress;
    public Action<double>? OnRelease;
    /// <summary>
    /// Another key went down while the hotkey was held, by its virtual-key code: with a modifier-only hotkey any key
    /// (e.g. Right Ctrl + C), with a key combination only a mode chord (see <see cref="ModeChordKeys"/>). A modifier only
    /// counts as a mode chord.
    /// </summary>
    public Action<int, double>? OnOtherKey;
    public Action? OnEscape;
    /// <summary>An action's shortcut went down (see <see cref="Shortcuts"/>).</summary>
    public Action<ShortcutAction>? OnAction;

    /// <summary>
    /// The shortcuts of actions besides dictating. Their keys are always kept from the focused app, whatever the session
    /// makes of them. Replaced as a whole (it's immutable), so the hook thread always sees a complete one.
    /// </summary>
    public volatile ShortcutMap Shortcuts = ShortcutMap.Empty;

    /// <summary>Keys that switch the recording's mode while the hotkey is held (see <see cref="ModeChords{TMode}"/>); kept from the focused app then.</summary>
    public volatile IReadOnlySet<int> ModeChordKeys = new HashSet<int>();

    /// <summary>
    /// Keys whose key-down was swallowed as an action's shortcut or a mode chord: their auto-repeats and key-up go too.
    /// Hook thread only.
    /// </summary>
    private readonly HashSet<int> _swallowedKeys = new();

    /// <summary>
    /// The keys physically down, from every key event the hook sees, the ones it swallows too. Noted on the hook thread,
    /// asked on the UI thread (see <see cref="WaitForRelease"/>).
    /// </summary>
    private readonly HeldKeys _held = new();

    /// <summary>
    /// Set (on the UI thread) while a dictation is recording or processing: Esc then cancels it and is kept from the
    /// focused app, where it would also interrupt a command-line AI tool or close a dialog.
    /// </summary>
    public volatile bool SwallowsEscape;
    /// <summary>
    /// An Esc key-down was swallowed: its auto-repeats and its key-up are swallowed too, even once the dictation has
    /// ended, so the focused app never sees half a key press. Hook thread only.
    /// </summary>
    private bool _escapeSwallowed;

    private volatile Hotkey _hotkey = Hotkey.Default;
    public Hotkey Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; _isDown = false; }
    }

    private volatile bool _isSuspended;
    /// <summary>Set while the settings window records a new hotkey.</summary>
    public bool IsSuspended
    {
        get => _isSuspended;
        set { _isSuspended = value; _isDown = false; }
    }

    /// <summary>While set, receives every key event (on the hook thread) and decides whether to swallow it.</summary>
    public volatile Func<KeyEvent, bool>? Recorder;

    /// <summary>While set, told of every key going down (on the hook thread), for following an edit.</summary>
    public volatile Action<TypedKey>? KeyObserver;

    public bool IsRunning { get; private set; }

    private volatile bool _isDown;
    private DispatcherQueue? _dispatcher;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Win32.LowLevelKeyboardProc? _proc; // kept alive for the lifetime of the hook

    /// <summary>Posted to the hook thread to send the menu mask once the hook has let a key-down through.</summary>
    private const uint WM_SEND_MENU_MASK = Win32.WM_APP + 1;
    /// <summary>Posted to the hook thread to install (wParam 1) or remove (0) the mouse hook.</summary>
    private const uint WM_WATCH_CLICKS = Win32.WM_APP + 2;

    private nint _mouseHook;
    private Win32.LowLevelMouseProc? _mouseProc;
    private volatile Action<MouseButton>? _onClick;

    /// <summary>
    /// Calls <paramref name="onClick"/> (on the UI thread) when a mouse button goes down anywhere, or the left one up;
    /// stops with null. The mouse hook is only installed while it's wanted.
    /// </summary>
    public void WatchClicks(Action<MouseButton>? onClick)
    {
        _onClick = onClick;
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, WM_WATCH_CLICKS, onClick != null ? 1 : 0, 0);
    }

    /// <summary>Starts the hook thread. Callbacks are delivered on the calling (UI) thread.</summary>
    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "OpenTypeless hotkey hook", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        // Retry every 2 s if the hook can't be installed (the macOS app retries until Accessibility is granted).
        while (!Install()) Thread.Sleep(2000);
        // Windows silently removes a low-level hook that ever times out (e.g. after heavy paging), with no
        // notification. Re-installing it every few minutes while idle makes that self-healing.
        Win32.SetTimer(0, 1, 5 * 60 * 1000, 0);
        while (Win32.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == Win32.WM_TIMER && !_isDown) Install();
            if (msg.message == WM_SEND_MENU_MASK)
            {
                SendMenuMask();
                continue;
            }
            if (msg.message == WM_WATCH_CLICKS)
            {
                if (msg.wParam != 0 && _mouseHook == 0)
                {
                    _mouseProc ??= MouseProc;
                    _mouseHook = Win32.SetWindowsHookExMouse(Win32.WH_MOUSE_LL, _mouseProc, Win32.GetModuleHandle(null), 0);
                }
                else if (msg.wParam == 0 && _mouseHook != 0)
                {
                    Win32.UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = 0;
                }
                continue;
            }
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    private bool Install()
    {
        var hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc!, Win32.GetModuleHandle(null), 0);
        if (hook == 0)
        {
            HotkeyLog.Debug($"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()}), retrying");
            return false;
        }
        var old = _hook;
        _hook = hook;
        if (old != 0) Win32.UnhookWindowsHookEx(old);
        else HotkeyLog.Debug($"hook installed; hotkey {_hotkey.DisplayName}");
        return true;
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != Win32.InjectedSignature)
            {
                if (Handle((int)wParam, info)) return 1;
                if (KeyObserver is { } observer && (int)wParam is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN) observer(Typed(info));
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (int)wParam is Win32.WM_LBUTTONDOWN or (int)Win32.WM_LBUTTONUP or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN
            && _onClick is { } onClick)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            var button = new MouseButton((int)wParam, info.pt.X, info.pt.Y, info.time);
            _dispatcher?.TryEnqueue(() => onClick(button));
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>What a key-down types in the foreground app's keyboard layout, with the modifiers held now.</summary>
    private static TypedKey Typed(Win32.KBDLLHOOKSTRUCT info)
    {
        static bool Held(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
        var thread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
        var layout = Win32.GetKeyboardLayout(thread);
        // Chinese, Japanese and Korean layouts come with an input method.
        var inputMethod = ((int)layout & 0x3FF) is 0x04 or 0x11 or 0x12;
        var state = new byte[256];
        foreach (var vk in new[] { 0x10, 0xA0, 0xA1, 0x11, 0xA2, 0xA3, 0x12, 0xA4, 0xA5 })
        {
            if (Held(vk)) state[vk] = 0x80;
        }
        if ((Win32.GetKeyState(0x14) & 1) != 0) state[0x14] = 1; // Caps Lock on
        var buffer = new char[8];
        var count = Win32.ToUnicodeEx(info.vkCode, info.scanCode, state, buffer, buffer.Length, Win32.TOUNICODE_NO_STATE_CHANGE, layout);
        return new TypedKey((int)info.vkCode, count > 0 ? new string(buffer, 0, count) : null,
                            Held(0x11), Held(0x12), Held(0x10), Held(Hotkey.VK_LWIN) || Held(Hotkey.VK_RWIN), inputMethod,
                            (info.flags & Win32.LLKHF_INJECTED) != 0);
    }

    /// <summary>Returns true when the event should be swallowed.</summary>
    private bool Handle(int message, Win32.KBDLLHOOKSTRUCT info)
    {
        var down = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        var up = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
        if (!down && !up) return false;
        var vk = (int)info.vkCode;
        var injected = (info.flags & Win32.LLKHF_INJECTED) != 0;
        // First of all, so the keys held are known while a shortcut is being recorded too. A low-level hook gets no
        // auto-repeat flag: a key-down for a key already down is one.
        var repeat = _held.Note(vk, down, info.time);

        if (Recorder is { } recorder) return recorder(new KeyEvent(vk, down, injected));

        // A key whose press was swallowed (an action's shortcut, a mode chord) stays swallowed until it's let go, its
        // auto-repeats and key-up alike, whatever is held with it by then: letting go of Shift while Shift + F5 is still
        // down mustn't turn F5's repeats into the F5 that starts a dictation. Suspended or not: the focused app mustn't
        // get half a key press because Settings changed meanwhile. (The key-combination hotkey's own release, while it's
        // down, is read below.)
        var hotkey = _hotkey;
        var hotkeyHeld = _isDown && !hotkey.IsModifierOnly && vk == hotkey.KeyCode;
        if (_swallowedKeys.Contains(vk) && !hotkeyHeld)
        {
            if (up) return _swallowedKeys.Remove(vk);
            if (repeat) return true;
            // A new press: the last one's key-up went by while a shortcut was being recorded (or the hook was off).
            _swallowedKeys.Remove(vk);
        }
        if (_isSuspended) return false;

        var time = HotkeyPress.TickTime(info.time, Environment.TickCount64);
        if (hotkey.IsModifierOnly)
        {
            if (vk == hotkey.KeyCode)
            {
                if (down && !_isDown)
                {
                    _isDown = true;
                    HotkeyLog.Debug($"{hotkey.DisplayName} down{(injected ? " (injected)" : "")}");
                    // Alt and Win act on their own release (menu bar / Start menu) unless another key is seen
                    // while they're held; a harmless unassigned key tells Windows they were part of a combo.
                    // Keys sent from inside the hook can reach the system *before* the key being hooked, so a mask
                    // sent right here would land ahead of the Alt-down and do nothing: the lone Alt release would
                    // still open the menu bar of a native app, which then swallows the Ctrl+V of the paste.
                    // Send it from the hook thread's message loop instead, once this key-down has gone through.
                    if (hotkey.NeedsMenuMask) Win32.PostThreadMessage(_threadId, WM_SEND_MENU_MASK, 0, 0);
                    Fire(OnPress, time);
                }
                else if (up && _isDown)
                {
                    _isDown = false;
                    HotkeyLog.Debug($"{hotkey.DisplayName} up");
                    // And once more from inside the hook, which lands ahead of the release while the key is still
                    // held, in case the tap was too quick for the posted mask. A spare mask is harmless.
                    if (hotkey.NeedsMenuMask) SendMenuMask();
                    Fire(OnRelease, time);
                }
                // Auto-repeat while held arrives as more key-downs: ignored.
                return false;
            }
            if (vk == Hotkey.VK_ESCAPE) return HandleEscape(down);
            if (!down) return false;
            // A mode chord pressed with the hotkey counts, a modifier too (Right Alt + Shift translates), and is kept
            // from the focused app, where Shift on its own would switch a Chinese or Japanese input method between its
            // own letters and English, and Alt + Shift the keyboard layout. Not AltGr's made-up Left Ctrl (below).
            if (_isDown && !injected && !repeat && ModeChordKeys.Contains(vk) && info.scanCode != AltGrControlScanCode)
            {
                HotkeyLog.Debug($"chord key (vk {vk}) with {hotkey.DisplayName}");
                Fire(OnOtherKey, vk, time);
                return Swallow(vk);
            }
            // Shift, Ctrl… pressed with the hotkey make no combo on their own (macOS doesn't count them either). AltGr
            // layouts also send a made-up Left Ctrl with every Right Alt event, auto-repeat included, which would
            // otherwise cancel a dictation held on Right Alt.
            if (IsModifier(vk) || info.scanCode == AltGrControlScanCode) return false;
            if (_isDown)
            {
                // A real combo (Right Ctrl + C…) comes from the keyboard. Keys injected by other software at
                // the same moment (e.g. a mouse utility sending its own shortcut) don't count and must not cancel.
                if (!injected)
                {
                    HotkeyLog.Debug($"combo key (vk {vk}) with {hotkey.DisplayName}");
                    Fire(OnOtherKey, vk, time);
                }
                else
                {
                    HotkeyLog.Debug($"ignored injected key (vk {vk}) while {hotkey.DisplayName} is held");
                }
            }
            // After the combo above, which cancels a dictation the shortcut's modifier may have just started.
            return HandleAction(vk, repeat);
        }

        // Key combination.
        if (vk == hotkey.KeyCode)
        {
            if (down && (CurrentModifiers() == hotkey.Modifiers || _isDown))
            {
                if (!_isDown)
                {
                    _isDown = true;
                    // Also swallowed: its key-up, should the hotkey change or recording a new one start first.
                    _swallowedKeys.Add(vk);
                    HotkeyLog.Debug($"{hotkey.DisplayName} down");
                    if ((hotkey.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0) SendMenuMask();
                    Fire(OnPress, time);
                }
                return true;
            }
            if (up && _isDown)
            {
                _isDown = false;
                _swallowedKeys.Remove(vk);
                HotkeyLog.Debug($"{hotkey.DisplayName} up");
                Fire(OnRelease, time);
                return true;
            }
            // Otherwise the same key with other modifiers, which may be an action's shortcut (F5 dictates, Shift + F5 pastes).
        }
        if (vk == Hotkey.VK_ESCAPE) return HandleEscape(down);
        if (!down) return false;
        // A new press only: the repeats of one that went through before the hotkey (Shift held, then ⌥Space) go through
        // too, or the focused app would see it go down and never come up. A swallowed one's repeats went above.
        if (_isDown && !injected && !repeat && ModeChordKeys.Contains(vk) && info.scanCode != AltGrControlScanCode)
        {
            // Other keys are left alone while a key-combination hotkey is held, as they always were, but a chord counts.
            HotkeyLog.Debug($"chord key (vk {vk}) with {hotkey.DisplayName}");
            Fire(OnOtherKey, vk, time);
            return Swallow(vk);
        }
        return HandleAction(vk, repeat);
    }

    /// <summary>
    /// A key going down that is an action's shortcut fires the action (once, not on its auto-repeats), and is swallowed,
    /// like the rest of its presses.
    /// </summary>
    private bool HandleAction(int vk, bool repeat)
    {
        var shortcuts = Shortcuts;
        // Every key typed comes through here: with no shortcut set, nothing more is asked of the system.
        if (shortcuts.IsEmpty || shortcuts.ActionFor(vk, (int)CurrentModifiers()) is not { } action) return false;
        if (!repeat)
        {
            HotkeyLog.Debug($"{action.RawValue()} shortcut");
            // Alt and Win act on their own release (menu bar / Start menu, or Alt + Shift switching the keyboard layout)
            // unless another key is seen while they're held, and the shortcut's key is kept from the system.
            if ((shortcuts.Chords[action].Modifiers & (int)(HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0) SendMenuMask();
            Fire(OnAction, action);
        }
        return Swallow(vk);
    }

    private bool Swallow(int vk)
    {
        _swallowedKeys.Add(vk);
        return true;
    }

    /// <summary>
    /// Waits until none of <paramref name="shortcut"/>'s keys is physically held any more (its key, and any of Ctrl, Alt,
    /// Shift and Win): a Ctrl+V sent meanwhile would be read together with them, as another shortcut altogether. Returns
    /// false when they're still held after <paramref name="timeout"/>. The key itself is followed through the hook: its
    /// presses were swallowed, so <c>GetAsyncKeyState</c> never heard of them (modifiers always go through).
    /// </summary>
    public async Task<bool> WaitForRelease(Hotkey shortcut, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var keyHeld = !shortcut.IsModifierOnly && _held.IsHeld(shortcut.KeyCode, unchecked((uint)Environment.TickCount));
            if (CurrentModifiers() == HotkeyModifiers.None && !keyHeld) return true;
            if (clock.Elapsed >= timeout) return false;
            await Task.Delay(20);
        }
    }

    /// <summary>The scan code of the Left Ctrl that AltGr layouts send along with Right Alt.</summary>
    private const uint AltGrControlScanCode = 0x21D;

    /// <summary>Shift, Ctrl, Alt or Win, either side or the side-less code.</summary>
    private static bool IsModifier(int vk) => vk is >= 0x10 and <= 0x12 || Hotkey.ModifierKeys.ContainsKey(vk);

    /// <summary>Esc cancels a dictation under way; returns whether the key is swallowed (see <see cref="SwallowsEscape"/>).</summary>
    private bool HandleEscape(bool down)
    {
        if (!down)
        {
            var swallowed = _escapeSwallowed;
            _escapeSwallowed = false;
            return swallowed;
        }
        if (_escapeSwallowed) return true; // auto-repeat of a swallowed Esc
        Fire(OnEscape);
        _escapeSwallowed = SwallowsEscape;
        return _escapeSwallowed;
    }

    public static HotkeyModifiers CurrentModifiers()
    {
        static bool Held(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
        var mods = HotkeyModifiers.None;
        if (Held(0x11)) mods |= HotkeyModifiers.Control;
        if (Held(0x12)) mods |= HotkeyModifiers.Alt;
        if (Held(0x10)) mods |= HotkeyModifiers.Shift;
        if (Held(Hotkey.VK_LWIN) || Held(Hotkey.VK_RWIN)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    /// <summary>VK 0xE8 is unassigned: apps ignore it, but Windows sees Alt / Win used in a combination.</summary>
    private static void SendMenuMask()
    {
        var inputs = new[] { Win32.Key(0xE8, up: false), Win32.Key(0xE8, up: true) };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private void Fire(Action? callback)
    {
        if (callback == null) return;
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => callback());
    }

    private void Fire(Action<double>? callback, double time)
    {
        if (callback == null) return;
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => callback(time));
    }

    private void Fire(Action<int, double>? callback, int key, double time)
    {
        if (callback == null) return;
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => callback(key, time));
    }

    private void Fire(Action<ShortcutAction>? callback, ShortcutAction action)
    {
        if (callback == null) return;
        _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => callback(action));
    }
}

/// <summary>Debug log of hotkey events (see <see cref="Services.AppLog"/>).</summary>
public static class HotkeyLog
{
    public static void Debug(string text) => Services.AppLog.Debug("hotkey", text);
}
