using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using OpenTypeless.Native;

namespace OpenTypeless.Input;

/// <summary>A key event as seen by the low-level hook (used while recording a new shortcut).</summary>
public readonly record struct KeyEvent(int VirtualKey, bool IsDown, bool IsInjected);

/// <summary>
/// Listens globally for the configured hotkey via a low-level keyboard hook (<c>WH_KEYBOARD_LL</c>), the
/// Windows counterpart of the macOS CGEventTap.
///
/// The hook runs on its own thread with its own message loop, so a busy UI thread can never make Windows
/// drop it for being slow. Everything is passed through untouched except the keystrokes of a key-combination
/// hotkey, which are swallowed so that e.g. Alt+Space doesn't also reach the focused app.
/// </summary>
public sealed class HotkeyMonitor
{
    public static HotkeyMonitor Shared { get; } = new();

    public Action? OnPress;
    public Action? OnRelease;
    /// <summary>Another key went down while a modifier-only hotkey was held (e.g. Right Ctrl + C).</summary>
    public Action? OnOtherKey;
    public Action? OnEscape;

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

    public bool IsRunning { get; private set; }

    private volatile bool _isDown;
    private DispatcherQueue? _dispatcher;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private Win32.LowLevelKeyboardProc? _proc; // kept alive for the lifetime of the hook

    /// <summary>Posted to the hook thread to send the menu mask once the hook has let a key-down through.</summary>
    private const uint WM_SEND_MENU_MASK = Win32.WM_APP + 1;

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
            if (info.dwExtraInfo != Win32.InjectedSignature && Handle((int)wParam, info)) return 1;
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Returns true when the event should be swallowed.</summary>
    private bool Handle(int message, Win32.KBDLLHOOKSTRUCT info)
    {
        var down = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        var up = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
        if (!down && !up) return false;
        var vk = (int)info.vkCode;
        var injected = (info.flags & Win32.LLKHF_INJECTED) != 0;

        if (Recorder is { } recorder) return recorder(new KeyEvent(vk, down, injected));
        if (_isSuspended) return false;

        var hotkey = _hotkey;
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
                    Fire(OnPress);
                }
                else if (up && _isDown)
                {
                    _isDown = false;
                    HotkeyLog.Debug($"{hotkey.DisplayName} up");
                    // And once more from inside the hook, which lands ahead of the release while the key is still
                    // held, in case the tap was too quick for the posted mask. A spare mask is harmless.
                    if (hotkey.NeedsMenuMask) SendMenuMask();
                    Fire(OnRelease);
                }
                // Auto-repeat while held arrives as more key-downs: ignored.
                return false;
            }
            if (!down) return false;
            if (vk == Hotkey.VK_ESCAPE)
            {
                Fire(OnEscape);
            }
            else if (_isDown)
            {
                // A real combo (Right Ctrl + C…) comes from the keyboard. Keys injected by other software at
                // the same moment (e.g. a mouse utility sending its own shortcut) don't count and must not cancel.
                if (!injected)
                {
                    HotkeyLog.Debug($"combo key (vk {vk}) with {hotkey.DisplayName}");
                    Fire(OnOtherKey);
                }
                else
                {
                    HotkeyLog.Debug($"ignored injected key (vk {vk}) while {hotkey.DisplayName} is held");
                }
            }
            return false;
        }

        // Key combination.
        if (vk == hotkey.KeyCode)
        {
            if (down && (CurrentModifiers() == hotkey.Modifiers || _isDown))
            {
                if (!_isDown)
                {
                    _isDown = true;
                    HotkeyLog.Debug($"{hotkey.DisplayName} down");
                    if ((hotkey.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0) SendMenuMask();
                    Fire(OnPress);
                }
                return true;
            }
            if (up && _isDown)
            {
                _isDown = false;
                HotkeyLog.Debug($"{hotkey.DisplayName} up");
                Fire(OnRelease);
                return true;
            }
            return false;
        }
        if (down && vk == Hotkey.VK_ESCAPE) Fire(OnEscape);
        return false;
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
}

/// <summary>Debug log of hotkey events (see <see cref="Services.AppLog"/>).</summary>
public static class HotkeyLog
{
    public static void Debug(string text) => Services.AppLog.Debug("hotkey", text);
}
