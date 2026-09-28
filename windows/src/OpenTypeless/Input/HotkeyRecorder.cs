using Microsoft.UI.Dispatching;

namespace OpenTypeless.Input;

/// <summary>
/// Records a new hotkey from the settings window. XAML key events can't tell left from right modifiers and
/// never see Alt / Win cleanly, so this listens through the low-level hook instead, but only while the
/// settings window is in front, so keys meant for other apps are never taken.
/// </summary>
public sealed class HotkeyRecorder
{
    public event Action? Changed;
    public Action<Hotkey>? OnRecorded;

    public bool IsRecording { get; private set; }
    public string? Message { get; private set; }

    private readonly Func<bool> _isTargetInFront;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<int> _heldModifiers = new();
    private int? _pendingModifier;
    private bool _sawKeyDown;

    public HotkeyRecorder(Func<bool> isTargetInFront)
    {
        _isTargetInFront = isTargetInFront;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public void Start()
    {
        if (IsRecording) return;
        IsRecording = true;
        Message = null;
        _pendingModifier = null;
        _heldModifiers.Clear();
        HotkeyMonitor.Shared.IsSuspended = true;
        HotkeyMonitor.Shared.Recorder = Handle;
        Changed?.Invoke();
    }

    public void Stop()
    {
        HotkeyMonitor.Shared.Recorder = null;
        var was = IsRecording;
        IsRecording = false;
        HotkeyMonitor.Shared.IsSuspended = false;
        if (was) Changed?.Invoke();
    }

    /// <summary>Runs on the hook thread; must return quickly.</summary>
    private bool Handle(KeyEvent e)
    {
        if (!_isTargetInFront()) return false;
        var code = e.VirtualKey;
        if (Hotkey.ModifierKeys.ContainsKey(code))
        {
            if (e.IsDown)
            {
                if (_heldModifiers.Add(code)) // ignore auto-repeat
                {
                    _pendingModifier = code;
                    _sawKeyDown = false;
                }
            }
            else
            {
                _heldModifiers.Remove(code);
                if (_pendingModifier == code && !_sawKeyDown) Finish(new Hotkey(code, HotkeyModifiers.None, true));
            }
            return true;
        }
        if (!e.IsDown) return true;

        _sawKeyDown = true;
        var mods = HotkeyModifiers.None;
        foreach (var held in _heldModifiers) mods |= Hotkey.ModifierKeys[held];
        if (code == Hotkey.VK_ESCAPE && mods == HotkeyModifiers.None)
        {
            HotkeyMonitor.Shared.Recorder = null;
            _dispatcher.TryEnqueue(Stop);
            return true;
        }
        if (mods == HotkeyModifiers.None && !Hotkey.IsFunctionKey(code))
        {
            _dispatcher.TryEnqueue(() =>
            {
                Message = L("单独的字母键会影响正常打字，请搭配 Ctrl / Alt / Shift / Win 使用，或者只按一个修饰键 / F1–F24。",
                            "A plain key would break normal typing — combine it with Ctrl / Alt / Shift / Win, or press a single modifier / F1–F24.");
                Changed?.Invoke();
            });
            return true;
        }
        Finish(new Hotkey(code, mods, false, Hotkey.Label(code)));
        return true;
    }

    private void Finish(Hotkey hotkey)
    {
        HotkeyMonitor.Shared.Recorder = null; // stop swallowing right away, on the hook thread
        _dispatcher.TryEnqueue(() =>
        {
            Stop();
            OnRecorded?.Invoke(hotkey);
        });
    }
}
