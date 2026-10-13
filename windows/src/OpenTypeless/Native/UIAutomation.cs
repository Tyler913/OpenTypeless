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

    /// <summary>
    /// What's just before and after the cursor (or the selection) in the focused element, from its Text pattern: up to
    /// <paramref name="before"/> and <paramref name="after"/> characters, "" at the start or end of the field; After is
    /// null when it couldn't be read. Null when the element is a password field, has no Text pattern or no single
    /// selection, or answers in a way that doesn't add up.
    /// </summary>
    public static (string Before, string? After)? TextAroundCursor(int before, int after)
    {
        try
        {
            if (FocusedElement() is not { } element || IsPassword(element)) return null;
            if (element.GetCurrentPropertyValue(UIA_IsTextPatternAvailablePropertyId, out var hasText) != 0 || hasText is not true
                || element.GetCurrentPattern(UIA_TextPatternId, out var pattern) != 0 || pattern is not IUIAutomationTextPattern textPattern
                || textPattern.GetSelection(out var ranges) != 0 || ranges == null
                || ranges.GetLength(out var count) != 0 || count != 1
                || ranges.GetElement(0, out var selection) != 0 || selection == null)
            {
                return null;
            }
            if (selection.Clone(out var head) != 0 || head == null
                || head.MoveEndpointByRange(TextPatternRangeEndpoint_End, selection, TextPatternRangeEndpoint_Start) != 0
                || head.MoveEndpointByUnit(TextPatternRangeEndpoint_Start, TextUnit_Character, -before, out var back) != 0
                || head.GetText(before * 2, out var beforeText) != 0 || beforeText == null
                // Moved back, but no text came (or the other way round): the range can't be trusted.
                || (back == 0) != (beforeText.Length == 0))
            {
                return null;
            }
            string? afterText = null;
            if (selection.Clone(out var tail) == 0 && tail != null
                && tail.MoveEndpointByRange(TextPatternRangeEndpoint_Start, selection, TextPatternRangeEndpoint_End) == 0
                && tail.MoveEndpointByUnit(TextPatternRangeEndpoint_End, TextUnit_Character, after, out var onward) == 0
                && tail.GetText(after * 2, out var read) == 0 && read != null
                && (onward == 0) == (read.Length == 0))
            {
                afterText = read;
            }
            return (beforeText, afterText);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
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
        void GetCurrentPropertyValueEx();
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
        void Compare();
        void CompareEndpoints();
        void ExpandToEnclosingUnit();
        void FindAttribute();
        void FindText();
        void GetAttributeValue();
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
