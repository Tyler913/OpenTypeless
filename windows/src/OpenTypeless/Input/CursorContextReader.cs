using System.Runtime.InteropServices;
using OpenTypeless.Native;
using TypelessCore;

namespace OpenTypeless.Input;

/// <summary>
/// One dictation's read of the text around the cursor. Cancelled when the dictation ends, the session locks, or the
/// clean-up has taken what came in so far; a cancelled read gives nothing, whenever it finishes.
/// </summary>
public sealed class CursorContextRead
{
    internal CursorContextRead(nint window, bool web)
    {
        Window = window;
        Web = web;
    }

    /// <summary>The window in front when recording started: the only one read.</summary>
    public nint Window { get; }
    /// <summary>A browser or Electron app (see <see cref="FocusProbe.IsWebBased"/>).</summary>
    internal bool Web { get; }
    private volatile bool _cancelled;

    public void Cancel() => _cancelled = true;
    public bool IsCancelled => _cancelled;

    /// <summary>Not cancelled, and its window is still the one in front.</summary>
    internal bool InFront => !_cancelled && Win32.GetForegroundWindow() == Window;
}

/// <summary>
/// Reads the text around the cursor in the field being dictated into, for the clean-up to spell names by (see
/// <see cref="CursorContext"/>). Through UI Automation, like the edit watcher, but once, as recording starts (the dictated
/// text only goes in at the end, so the field is still the speaker's): each read runs on the thread pool with a short
/// budget, since it goes into the other app, and whatever fails or takes long just means no context.
///
/// Only a field that answers clearly is read: one that says it isn't a password field (an element that doesn't say is
/// left alone) and that it takes text and can be written to, never a browser's page or a list. The text is kept only if,
/// once it's read, the same window is in front, the same element has focus, it still says it isn't a password field, and
/// the read wasn't cancelled.
/// </summary>
public static class CursorContextReader
{
    /// <summary>A hung app must never hold anything up.</summary>
    private const int ReadTimeoutMs = 500;
    /// <summary>
    /// A browser or Electron app whose focused element isn't a text field yet (Chromium builds its UI Automation tree a
    /// while after first being asked) is asked again this many times, this far apart: about a second in all.
    /// </summary>
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>Where the field has no Text pattern, its value is read whole, if it isn't longer than this.</summary>
    private const int MaxFieldLength = 200_000;

    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsKeyboardFocusablePropertyId = 30009;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_IsTextPatternAvailablePropertyId = 30040;
    private const int UIA_IsValuePatternAvailablePropertyId = 30043;
    private const int UIA_ValueValuePropertyId = 30045;
    private const int UIA_ValueIsReadOnlyPropertyId = 30046;
    private const int UIA_IsReadOnlyAttributeId = 40015;
    private const int UIA_TextPatternId = 10014;
    private const int Edit = 50004, Document = 50030, ComboBox = 50003;
    private const int TextPatternRangeEndpoint_Start = 0, TextPatternRangeEndpoint_End = 1, TextUnit_Character = 0;

    /// <summary>
    /// 1 while a read is running on the thread pool. A read a hung app holds up keeps its thread until the app answers
    /// (the timeout only stops the waiting), so no other read starts meanwhile, in this dictation or the next.
    /// </summary>
    private static int _outstanding;

    private enum Security { Secure, NotSecure, Unknown }

    /// <summary>A read of the window in front, or null when there's none.</summary>
    public static CursorContextRead? Begin()
    {
        var window = Win32.GetForegroundWindow();
        if (window == 0) return null;
        Win32.GetWindowThreadProcessId(window, out var pid);
        return new CursorContextRead(window, FocusProbe.IsWebBased(window, pid));
    }

    /// <summary>The excerpt around the cursor in the focused field of <paramref name="read"/>'s window, or null. Never throws.</summary>
    public static async Task<string?> Read(CursorContextRead read)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (attempt > 1) await Task.Delay(RetryDelay).ConfigureAwait(false);
            // Focus moved on meanwhile: whatever has it now isn't where the text goes.
            if (!read.InFront) return null;
            if (Interlocked.CompareExchange(ref _outstanding, 1, 0) != 0) return null;
            var work = Task.Run(() =>
            {
                try
                {
                    return ReadFocused(read);
                }
                finally
                {
                    Volatile.Write(ref _outstanding, 0);
                }
            });
            if (await Task.WhenAny(work, Task.Delay(ReadTimeoutMs)).ConfigureAwait(false) != work) return null;
            var (found, text) = await work.ConfigureAwait(false);
            if (found || !read.Web) return read.InFront ? text : null;
        }
        return null;
    }

    /// <summary>
    /// Whether the focused element is one to settle on (a field, or a password field), and if so the excerpt around its
    /// cursor. Every COM object it gets is released before it returns, also when it's abandoned for taking too long.
    /// </summary>
    private static (bool Found, string? Text) ReadFocused(CursorContextRead read)
    {
        var held = new List<object>();
        try
        {
            if (!read.InFront) return (true, null);
            if (Hold(held, UIAutomation.FocusedElement()) is not { } element) return (false, null);
            // The window in front changed while focus was asked for: this element may be another window's.
            if (!read.InFront) return (true, null);
            if (PasswordState(element) != Security.NotSecure) return (true, null);
            var selection = Selection(element, held);
            if (!IsEditable(element, selection, read.Web)) return (false, null);
            var text = selection != null ? TextAround(selection, held) : WholeValue(element);
            // Asked again once it's read: the field could have turned into a password field (a sign-in form swapping it),
            // or focus moved on, meanwhile.
            if (text == null || !read.InFront || Hold(held, UIAutomation.FocusedElement()) is not { } focused
                || !UIAutomation.SameElement(focused, element) || PasswordState(element) != Security.NotSecure)
            {
                return (true, null);
            }
            return (true, text);
        }
        catch (Exception)
        {
            // Whatever goes wrong reading another app's field, the dictation goes on without the context.
            return (true, null);
        }
        finally
        {
            foreach (var com in held)
            {
                try
                {
                    Marshal.ReleaseComObject(com);
                }
                catch (Exception)
                {
                    // Already gone with its app.
                }
            }
        }
    }

    /// <summary>Keeps <paramref name="com"/> to be released once the read is done.</summary>
    private static T? Hold<T>(List<object> held, T? com) where T : class
    {
        if (com != null && Marshal.IsComObject(com)) held.Add(com);
        return com;
    }

    /// <summary>
    /// Whether the element is a password field, as it says itself: one that doesn't give IsPassword at all (UI Automation
    /// would otherwise answer false for it), or whose answer can't be read, is unknown, and an unknown field isn't read.
    /// </summary>
    private static Security PasswordState(UIAutomation.IUIAutomationElement element) =>
        element.GetCurrentPropertyValueEx(UIA_IsPasswordPropertyId, 1, out var value) == 0 && value is bool password
            ? password ? Security.Secure : Security.NotSecure
            : Security.Unknown;

    /// <summary>
    /// Whether the element takes typed text: an edit box, a combo box, a document or a focusable element with a Text
    /// pattern (rich web editors), which also says it can be written to, through its Value pattern or else the read-only
    /// attribute of the text at its cursor. A read-only one doesn't, nor one that doesn't say. A browser's page is a
    /// Document too, and its text isn't the speaker's field, so a Document in a browser or Electron app never counts.
    /// </summary>
    private static bool IsEditable(UIAutomation.IUIAutomationElement element, UIAutomation.IUIAutomationTextRange? selection, bool web)
    {
        var type = Int(element, UIA_ControlTypePropertyId);
        if (web && type == Document) return false;
        if (type is not (Edit or ComboBox or Document) && !(selection != null && Bool(element, UIA_IsKeyboardFocusablePropertyId))) return false;
        if (Bool(element, UIA_IsValuePatternAvailablePropertyId)
            && element.GetCurrentPropertyValueEx(UIA_ValueIsReadOnlyPropertyId, 1, out var readOnly) == 0 && readOnly is bool valueReadOnly)
        {
            return !valueReadOnly;
        }
        return selection != null && selection.GetAttributeValue(UIA_IsReadOnlyAttributeId, out var attribute) == 0 && attribute is false;
    }

    /// <summary>The element's one selection (or the empty range at its cursor), from its Text pattern; null where it has none.</summary>
    private static UIAutomation.IUIAutomationTextRange? Selection(UIAutomation.IUIAutomationElement element, List<object> held)
    {
        if (!Bool(element, UIA_IsTextPatternAvailablePropertyId)
            || element.GetCurrentPattern(UIA_TextPatternId, out var pattern) != 0 || Hold(held, pattern) is not UIAutomation.IUIAutomationTextPattern textPattern
            || textPattern.GetSelection(out var ranges) != 0 || Hold(held, ranges) is not { } array
            || array.GetLength(out var count) != 0 || count != 1
            || array.GetElement(0, out var selection) != 0)
        {
            return null;
        }
        return Hold(held, selection);
    }

    /// <summary>
    /// The excerpt around <paramref name="selection"/>: the text before it, the selection and the text after it, each read
    /// up to twice the limit. A part that stops short of the field's start or end (the endpoint moved all it was asked to,
    /// or the selection is longer) may end inside a character, which is marked for <see cref="CursorContext"/> to trim.
    /// </summary>
    private static string? TextAround(UIAutomation.IUIAutomationTextRange selection, List<object> held)
    {
        var span = 2 * CursorContext.Limit;
        var cut = CursorContext.Cut.None;
        if (selection.GetText(span + 1, out var selectedText) != 0 || selectedText == null) return null;
        var selected = selectedText;
        if (selected.Length > span)
        {
            selected = selected[..span];
            cut |= CursorContext.Cut.Selection;
        }
        if (Around(selection, before: true, span, held) is not { } before) return null;
        if (before.Cut) cut |= CursorContext.Cut.Before;
        var after = "";
        // After a selection read only in part comes nothing: what follows its first part isn't what was read.
        if (!cut.HasFlag(CursorContext.Cut.Selection))
        {
            if (Around(selection, before: false, span, held) is not { } next) return null;
            after = next.Text;
            if (next.Cut) cut |= CursorContext.Cut.After;
        }
        return CursorContext.Excerpt(before.Text, selected, after, cut: cut);
    }

    /// <summary>
    /// The text up to <paramref name="count"/> characters before (or after) <paramref name="selection"/>: a copy of it
    /// collapsed onto its start (or end), then stretched away from it. <c>Cut</c>: it may not reach the field's start (or
    /// end). Null where the field doesn't answer, or answers with more than a range that long can hold.
    /// </summary>
    private static (string Text, bool Cut)? Around(UIAutomation.IUIAutomationTextRange selection, bool before, int count, List<object> held)
    {
        var (collapsed, onto) = before
            ? (TextPatternRangeEndpoint_End, TextPatternRangeEndpoint_Start)
            : (TextPatternRangeEndpoint_Start, TextPatternRangeEndpoint_End);
        // A character is one or more UTF-16 units: the range holds no more than this many.
        var most = 4 * count;
        if (selection.Clone(out var range) != 0 || Hold(held, range) is not { } copy
            || copy.MoveEndpointByRange(collapsed, selection, onto) != 0
            || copy.MoveEndpointByUnit(onto, TextUnit_Character, before ? -count : count, out var moved) != 0
            || copy.GetText(most + 1, out var text) != 0 || text == null || text.Length > most)
        {
            return null;
        }
        return (text, Math.Abs(moved) >= count);
    }

    /// <summary>The excerpt of the element's whole value (no Text pattern to read ranges with), with the cursor at its end.</summary>
    private static string? WholeValue(UIAutomation.IUIAutomationElement element) =>
        Bool(element, UIA_IsValuePatternAvailablePropertyId)
        && element.GetCurrentPropertyValue(UIA_ValueValuePropertyId, out var value) == 0 && value is string text && text.Length <= MaxFieldLength
            ? CursorContext.Excerpt(text, null)
            : null;

    private static int Int(UIAutomation.IUIAutomationElement element, int property) =>
        element.GetCurrentPropertyValue(property, out var value) == 0 && value is int i ? i : 0;

    private static bool Bool(UIAutomation.IUIAutomationElement element, int property) =>
        element.GetCurrentPropertyValue(property, out var value) == 0 && value is true;
}
