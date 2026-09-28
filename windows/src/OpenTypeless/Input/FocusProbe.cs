using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenTypeless.Native;

namespace OpenTypeless.Input;

/// <summary>
/// Asks Windows what currently has keyboard focus, to decide between pasting at the cursor and leaving the
/// text on the clipboard. Uses the caret (classic Win32 edits) and UI Automation (everything else), the
/// counterparts of the macOS Accessibility role checks.
/// </summary>
public static class FocusProbe
{
    public enum Target
    {
        /// <summary>A text field, text area or document editor: paste there.</summary>
        Editable,
        /// <summary>Focus is clearly on something that can't take text (the desktop, a list, a button…).</summary>
        NotEditable,
        /// <summary>Can't tell, e.g. browsers and Electron apps that don't expose their page to UI Automation.</summary>
        Unknown,
    }

    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsValuePatternAvailablePropertyId = 30043;
    private const int UIA_ValueIsReadOnlyPropertyId = 30046;
    private const int UIA_IsTextPatternAvailablePropertyId = 30040;
    private const int UIA_IsKeyboardFocusablePropertyId = 30009;

    private const int Edit = 50004, Document = 50030, ComboBox = 50003;

    private static readonly HashSet<int> NonTextControlTypes =
    [
        50000 /* Button */, 50002 /* CheckBox */, 50013 /* RadioButton */, 50031 /* SplitButton */, 50011 /* MenuItem */,
        50009 /* Menu */, 50010 /* MenuBar */, 50008 /* List */, 50007 /* ListItem */, 50023 /* Tree */, 50024 /* TreeItem */,
        50036 /* Table */, 50028 /* DataGrid */, 50029 /* DataItem */, 50034 /* Header */, 50035 /* HeaderItem */,
        50006 /* Image */, 50021 /* ToolBar */, 50018 /* Tab */, 50019 /* TabItem */, 50015 /* Slider */, 50020 /* Text */,
        50005 /* Hyperlink */, 50032 /* Window */, 50033 /* Pane */, 50026 /* Group */, 50037 /* TitleBar */,
        50014 /* ScrollBar */, 50012 /* ProgressBar */, 50017 /* StatusBar */, 50001 /* Calendar */, 50027 /* Thumb */,
    ];

    /// <summary>Desktop, taskbar: the equivalent of Finder with nothing selected on macOS.</summary>
    private static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public static async Task<Target> FocusedTarget()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground == 0) return Target.Unknown;
        Services.AppLog.Debug("focus", $"foreground {Win32.ClassName(foreground)} (thread {Win32.GetWindowThreadProcessId(foreground, out var owner)}, pid {owner}, " +
                                       $"{Path.GetFileName(Win32.ProcessImagePath(owner) ?? "?")})");
        if (ShellClasses.Contains(Win32.ClassName(foreground))) return Target.NotEditable;

        var threadId = Win32.GetWindowThreadProcessId(foreground, out var pid);
        // Windows blocks input into apps running as administrator from a normal app, so a paste there
        // would silently do nothing: leave the text on the clipboard instead.
        if (!SelfIsElevated && Win32.IsElevated(pid) != false) return Target.NotEditable;

        // Terminals are one big text input (and the usual home of command-line AI tools), but expose their
        // buffer to UI Automation as static text.
        if (IsTerminal(foreground, pid)) return Target.Editable;

        var gui = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (Win32.GetGUIThreadInfo(threadId, ref gui) && gui.hwndCaret != 0) return Target.Editable;

        var web = IsWebBased(foreground, pid);
        // UI Automation calls into the other process; never let a hung app stall the paste.
        var probe = Task.Run(() => ProbeAutomation(web));
        if (await Task.WhenAny(probe, Task.Delay(500)) != probe) return Target.Unknown;
        return probe.Result;
    }

    private static Target ProbeAutomation(bool web)
    {
        try
        {
            var automation = Automation.Value;
            if (automation == null || automation.GetFocusedElement(out var element) != 0 || element == null) return Target.Unknown;
            try
            {
                var controlType = Int(element, UIA_ControlTypePropertyId);
                var readOnly = Bool(element, UIA_IsValuePatternAvailablePropertyId) && Bool(element, UIA_ValueIsReadOnlyPropertyId);
                switch (controlType)
                {
                    case Edit or ComboBox:
                        return readOnly ? Target.NotEditable : Target.Editable;
                    case Document:
                        // A browser's page is a Document too, whether or not a text box inside has focus.
                        if (web) return Target.Unknown;
                        return !readOnly && Bool(element, UIA_IsTextPatternAvailablePropertyId) ? Target.Editable : Target.Unknown;
                }
                // Rich web editors (contenteditable) often expose a focusable element with a text pattern.
                if (!web && Bool(element, UIA_IsTextPatternAvailablePropertyId) && Bool(element, UIA_IsKeyboardFocusablePropertyId) && !readOnly
                    && Bool(element, UIA_IsValuePatternAvailablePropertyId))
                {
                    return Target.Editable;
                }
                if (NonTextControlTypes.Contains(controlType))
                {
                    // Browsers and Electron apps often report a pane or the window as focused even when the cursor
                    // is in a web text box. So a vague answer there means "can't tell".
                    return web ? Target.Unknown : Target.NotEditable;
                }
                return Target.Unknown;
            }
            finally
            {
                Marshal.ReleaseComObject(element);
            }
        }
        catch (COMException)
        {
            return Target.Unknown;
        }
        catch (InvalidCastException)
        {
            return Target.Unknown;
        }
    }

    private static readonly Lazy<bool> Elevated = new(() => Win32.IsElevated((uint)Environment.ProcessId) == true);
    private static bool SelfIsElevated => Elevated.Value;

    private static readonly string[] BrowserExecutables =
    [
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "zen", "waterfox", "librewolf",
        "floorp", "thorium", "chromium", "comet", "dia", "iexplore", "yandex", "360se", "qqbrowser", "sogouexplorer",
    ];
    private static readonly ConcurrentDictionary<string, bool> WebBasedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A browser, or an app built on Electron / the Chromium Embedded Framework (Slack, VS Code, Notion…).</summary>
    public static bool IsWebBased(nint window, uint pid)
    {
        var className = Win32.ClassName(window);
        if (className.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) || className == "MozillaWindowClass") return true;
        var exe = Win32.ProcessImagePath(pid);
        if (exe == null) return false;
        return WebBasedCache.GetOrAdd(exe, path =>
        {
            if (BrowserExecutables.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)) return true;
            var folder = Path.GetDirectoryName(path) ?? "";
            return File.Exists(Path.Combine(folder, "resources", "app.asar")) || File.Exists(Path.Combine(folder, "libcef.dll"));
        });
    }

    private static readonly HashSet<string> TerminalClasses = ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PuTTY", "mintty"];
    private static readonly string[] TerminalExecutables = ["windowsterminal", "openconsole", "wezterm-gui", "alacritty", "mintty", "putty", "kitty"];

    private static bool IsTerminal(nint window, uint pid)
    {
        if (TerminalClasses.Contains(Win32.ClassName(window))) return true;
        var exe = Win32.ProcessImagePath(pid);
        return exe != null && TerminalExecutables.Contains(Path.GetFileNameWithoutExtension(exe), StringComparer.OrdinalIgnoreCase);
    }

    private static int Int(IUIAutomationElement element, int property) =>
        element.GetCurrentPropertyValue(property, out var value) == 0 && value is int i ? i : 0;

    private static bool Bool(IUIAutomationElement element, int property) =>
        element.GetCurrentPropertyValue(property, out var value) == 0 && value is bool b && b;

    private static readonly Lazy<IUIAutomation?> Automation = new(() =>
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"));
            return type == null ? null : (IUIAutomation?)Activator.CreateInstance(type);
        }
        catch (COMException)
        {
            return null;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // Only the vtable slots before the methods we call need to exist; they are never invoked.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        void ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
    }
}
