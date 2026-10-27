using System.Runtime.InteropServices;
using TypelessCore;

namespace OpenTypeless.Native;

/// <summary>
/// The slice of the UI Automation client API the edit watcher and command mode need: the focused element, whether it's
/// still the same element, its text, and the text selected in it. Calls go into the other app's process, so make them
/// off the UI thread.
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

    /// <summary>
    /// The text selected in the element (<c>TextPattern.GetSelection</c>, its ranges joined): <see cref="SelectionProbe.Empty"/>
    /// when it has a text pattern and nothing is selected, <see cref="SelectionProbe.Unavailable"/> when it has none (or
    /// didn't answer). Up to <paramref name="maxLength"/> characters a range.
    /// </summary>
    public static SelectionProbe SelectedText(IUIAutomationElement element, int maxLength)
    {
        try
        {
            if (element.GetCurrentPropertyValue(UIA_IsTextPatternAvailablePropertyId, out var hasText) != 0 || hasText is not true
                || element.GetCurrentPattern(UIA_TextPatternId, out var pattern) != 0 || pattern is not IUIAutomationTextPattern textPattern
                || textPattern.GetSelection(out var ranges) != 0 || ranges == null || ranges.GetLength(out var count) != 0)
            {
                return new SelectionProbe.Unavailable();
            }
            var parts = new List<string>();
            for (var i = 0; i < count; i++)
            {
                if (ranges.GetElement(i, out var range) != 0 || range == null) return new SelectionProbe.Unavailable();
                if (range.GetText(maxLength, out var text) != 0) return new SelectionProbe.Unavailable();
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
            return parts.Count == 0 ? new SelectionProbe.Empty() : new SelectionProbe.Text(string.Join("\n", parts));
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return new SelectionProbe.Unavailable();
        }
    }

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
        void Clone();
        void Compare();
        void CompareEndpoints();
        void ExpandToEnclosingUnit();
        void FindAttribute();
        void FindText();
        void GetAttributeValue();
        void GetBoundingRectangles();
        void GetEnclosingElement();
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string? text);
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
