using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenTypeless.Native;

/// <summary>
/// The slice of the UI Automation client API the edit watcher and the paste need: the focused element, whether it's still
/// the same element, its text, and the text around its cursor. Calls go into the other app's process, so make them off
/// the UI thread.
/// </summary>
internal static class UIAutomation
{
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_IsTextPatternAvailablePropertyId = 30040;
    private const int UIA_IsValuePatternAvailablePropertyId = 30043;
    private const int UIA_ValueValuePropertyId = 30045;
    private const int UIA_TextPatternId = 10014;

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

    public static IUIAutomationElement? FocusedElement()
    {
        try
        {
            return Automation.Value is { } automation && automation.GetFocusedElement(out var element) == 0 ? element : null;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
    }

    public static bool SameElement(IUIAutomationElement a, IUIAutomationElement b)
    {
        try
        {
            return Automation.Value is { } automation && automation.CompareElements(a, b, out var same) == 0 && same != 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return false;
        }
    }

    public static bool IsPassword(IUIAutomationElement element) =>
        element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId, out var value) == 0 && value is true;

    /// <summary>The element's text: its value, or for documents (Word, rich editors) the whole text range. Null if unreadable.</summary>
    public static string? Text(IUIAutomationElement element, int maxLength)
    {
        try
        {
            if (element.GetCurrentPropertyValue(UIA_IsValuePatternAvailablePropertyId, out var hasValue) == 0 && hasValue is true
                && element.GetCurrentPropertyValue(UIA_ValueValuePropertyId, out var value) == 0 && value is string text)
            {
                return text;
            }
            if (element.GetCurrentPropertyValue(UIA_IsTextPatternAvailablePropertyId, out var hasText) == 0 && hasText is true
                && element.GetCurrentPattern(UIA_TextPatternId, out var pattern) == 0 && pattern is IUIAutomationTextPattern textPattern
                && textPattern.GetDocumentRange(out var range) == 0 && range != null
                && range.GetText(maxLength, out var document) == 0)
            {
                return document;
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
        }
        return null;
    }

    private const int TextPatternRangeEndpoint_Start = 0, TextPatternRangeEndpoint_End = 1, TextUnit_Character = 0;
    /// <summary>A read around the cursor this long or longer returned more than was asked for.</summary>
    private const int ReadLimit = 64;

    /// <summary>The text either side of the cursor in a field, and the element and selection it was read at.</summary>
    public sealed record CursorContext(string Before, string After, IUIAutomationElement Element, IUIAutomationTextRange Selection);

    /// <summary>
    /// What's just before and after the cursor (or the selection) in the focused element, from its Text pattern: up to
    /// <paramref name="before"/> and <paramref name="after"/> characters, "" at the start or end of the field. Null
    /// unless the element says it isn't a password field and has one selection, and both reads add up.
    /// </summary>
    public static CursorContext? TextAroundCursor(int before, int after)
    {
        try
        {
            if (FocusedElement() is not { } element || IsPasswordField(element) != false || Selection(element) is not { } selection) return null;
            if (selection.Clone(out var head) != 0 || head == null
                || head.MoveEndpointByRange(TextPatternRangeEndpoint_End, selection, TextPatternRangeEndpoint_Start) != 0
                || head.MoveEndpointByUnit(TextPatternRangeEndpoint_Start, TextUnit_Character, -before, out var back) != 0
                || head.GetText(ReadLimit, out var beforeText) != 0 || !Plausible(beforeText, back, before))
            {
                return null;
            }
            if (selection.Clone(out var tail) != 0 || tail == null
                || tail.MoveEndpointByRange(TextPatternRangeEndpoint_Start, selection, TextPatternRangeEndpoint_End) != 0
                || tail.MoveEndpointByUnit(TextPatternRangeEndpoint_End, TextUnit_Character, after, out var onward) != 0
                || tail.GetText(ReadLimit, out var afterText) != 0 || !Plausible(afterText, onward, after))
            {
                return null;
            }
            return new CursorContext(beforeText, afterText, element, selection);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether the cursor is still where <paramref name="context"/> was read: the same focused element, with the same selection.</summary>
    public static bool CursorStillAt(CursorContext context)
    {
        try
        {
            return FocusedElement() is { } element && SameElement(element, context.Element)
                && Selection(element) is { } selection && selection.Compare(context.Selection, out var same) == 0 && same != 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Whether the element is a password field; null when it didn't say.</summary>
    private static bool? IsPasswordField(IUIAutomationElement element) =>
        element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId, out var value) == 0 && value is bool password ? password : null;

    /// <summary>The element's one selection (or degenerate range at the cursor), from its Text pattern.</summary>
    private static IUIAutomationTextRange? Selection(IUIAutomationElement element) =>
        element.GetCurrentPropertyValue(UIA_IsTextPatternAvailablePropertyId, out var hasText) == 0 && hasText is true
        && element.GetCurrentPattern(UIA_TextPatternId, out var pattern) == 0 && pattern is IUIAutomationTextPattern textPattern
        && textPattern.GetSelection(out var ranges) == 0 && ranges != null
        && ranges.GetLength(out var count) == 0 && count == 1
        && ranges.GetElement(0, out var selection) == 0
            ? selection
            : null;

    /// <summary>
    /// Whether a read next to the cursor is what was asked for: the endpoint moved no further than asked, the text isn't
    /// cut off by the read limit, and it's as many characters as the endpoint moved (counted as UTF-16 units or as
    /// graphemes). A provider without a character unit moves by words or lines instead, which this rejects.
    /// </summary>
    private static bool Plausible([NotNullWhen(true)] string? text, int moved, int asked)
    {
        if (text == null || text.Length >= ReadLimit) return false;
        var count = Math.Abs(moved);
        if (count > asked) return false;
        return count == 0 ? text.Length == 0 : new StringInfo(text).LengthInTextElements <= count && count <= text.Length;
    }

    // Only the vtable slots before the methods we call need to exist; they are never invoked.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IUIAutomationElement first, IUIAutomationElement second, out int areSame);
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        void ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
        // ignoreDefaultValue nonzero: a property the element doesn't give comes back as UI Automation's "not supported"
        // value, not as its default (false for IsPassword), so an answer can be told from a silence.
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object? value);
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextPattern
    {
        void RangeFromPoint();
        void RangeFromChild();
        [PreserveSig] int GetSelection(out IUIAutomationTextRangeArray? ranges);
        void GetVisibleRanges();
        [PreserveSig] int GetDocumentRange(out IUIAutomationTextRange? range);
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextRangeArray
    {
        [PreserveSig] int GetLength(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationTextRange? element);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextRange
    {
        [PreserveSig] int Clone(out IUIAutomationTextRange? clone);
        [PreserveSig] int Compare(IUIAutomationTextRange range, out int areSame);
        void CompareEndpoints();
        void ExpandToEnclosingUnit();
        void FindAttribute();
        void FindText();
        [PreserveSig] int GetAttributeValue(int attributeId, [MarshalAs(UnmanagedType.Struct)] out object? value);
        void GetBoundingRectangles();
        void GetEnclosingElement();
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string? text);
        void Move();
        [PreserveSig] int MoveEndpointByUnit(int endpoint, int unit, int count, out int moved);
        [PreserveSig] int MoveEndpointByRange(int sourceEndpoint, IUIAutomationTextRange range, int targetEndpoint);
    }
}

/// <summary>
/// The Windows spell checker (the one behind every text box's red squiggles), used to tell ordinary English
/// words from names and terms. UI thread only.
/// </summary>
internal static class SpellChecker
{
    private static readonly Lazy<ISpellChecker?> English = new(() =>
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7ab36653-1796-484b-bdfa-e74f1db7c1dc"));
            if (type == null || Activator.CreateInstance(type) is not ISpellCheckerFactory factory) return null;
            foreach (var language in new[] { "en-US", "en-GB" })
            {
                if (factory.IsSupported(language, out var supported) == 0 && supported != 0
                    && factory.CreateSpellChecker(language, out var checker) == 0)
                {
                    return checker;
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
        }
        return null;
    });

    /// <summary>An everyday English word, per the system spelling dictionary. False when there's no English dictionary.</summary>
    public static bool IsCommonWord(string word)
    {
        try
        {
            if (English.Value is not { } checker || checker.Check(word.ToLowerInvariant(), out var errors) != 0 || errors == null) return false;
            // S_FALSE: no spelling errors in the word.
            var hr = errors.Next(out var error);
            if (error != 0) Marshal.Release(error);
            return hr == 1;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return false;
        }
    }

    [ComImport, Guid("8e018a9d-2415-4677-bf08-794ea61f94bb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        void GetSupportedLanguages();
        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);
        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker? value);
    }

    [ComImport, Guid("b6fd0b71-e2bc-4653-8d05-f197e412770b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        void GetLanguageTag();
        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError? value);
    }

    [ComImport, Guid("803e3bd4-2828-4410-8290-418d1d73c762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out nint value);
    }
}
