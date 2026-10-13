using System.Runtime.InteropServices;

namespace OpenTypeless.Native;

/// <summary>
/// The slice of the UI Automation client API the edit watcher and the cursor context need: the focused element, whether
/// it's still the same element, whether it takes text, its text, and the text around its selection. Calls go into the
/// other app's process, so make them off the UI thread.
/// </summary>
internal static class UIAutomation
{
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsKeyboardFocusablePropertyId = 30009;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_IsTextPatternAvailablePropertyId = 30040;
    private const int UIA_IsValuePatternAvailablePropertyId = 30043;
    private const int UIA_ValueValuePropertyId = 30045;
    private const int UIA_ValueIsReadOnlyPropertyId = 30046;
    private const int UIA_TextPatternId = 10014;
    private const int Edit = 50004, Document = 50030, ComboBox = 50003;
    private const int TextPatternRangeEndpoint_Start = 0, TextPatternRangeEndpoint_End = 1, TextUnit_Character = 0;

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
    /// Whether the element takes typed text: an edit box or a document (Word), and in a browser or Electron app
    /// (<paramref name="web"/>) an edit box or editable content. Never a read-only one, nor a browser's page itself, whose
    /// text isn't the speaker's field.
    /// </summary>
    public static bool IsTextInput(IUIAutomationElement element, bool web)
    {
        var hasValue = Bool(element, UIA_IsValuePatternAvailablePropertyId);
        if (hasValue && Bool(element, UIA_ValueIsReadOnlyPropertyId)) return false;
        return Int(element, UIA_ControlTypePropertyId) switch
        {
            Edit or ComboBox => true,
            Document => !web || hasValue,
            _ => hasValue && Bool(element, UIA_IsTextPatternAvailablePropertyId) && Bool(element, UIA_IsKeyboardFocusablePropertyId),
        };
    }

    /// <summary>
    /// Up to <paramref name="maxLength"/> characters before the selection (the cursor, when nothing is selected), the
    /// selection itself, cut to as many, and as many after it, through the Text pattern, so a long document isn't read
    /// whole. Null where the element has no Text pattern or doesn't say where its selection is.
    /// </summary>
    public static (string Before, string Selected, string After)? TextAroundSelection(IUIAutomationElement element, int maxLength)
    {
        try
        {
            if (!Bool(element, UIA_IsTextPatternAvailablePropertyId)
                || element.GetCurrentPattern(UIA_TextPatternId, out var pattern) != 0 || pattern is not IUIAutomationTextPattern textPattern
                || textPattern.GetSelection(out var ranges) != 0 || ranges == null
                || ranges.GetLength(out var count) != 0 || count < 1
                || ranges.GetElement(0, out var selection) != 0 || selection == null)
            {
                return null;
            }
            var selected = selection.GetText(maxLength, out var text) == 0 ? text ?? "" : "";
            return (Around(selection, before: true, maxLength), selected, Around(selection, before: false, maxLength));
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// The text up to <paramref name="maxLength"/> characters before (or after) <paramref name="selection"/>: a copy of it
    /// collapsed onto its start (or end), then stretched away from it.
    /// </summary>
    private static string Around(IUIAutomationTextRange selection, bool before, int maxLength)
    {
        var (collapsed, onto) = before
            ? (TextPatternRangeEndpoint_End, TextPatternRangeEndpoint_Start)
            : (TextPatternRangeEndpoint_Start, TextPatternRangeEndpoint_End);
        if (selection.Clone(out var range) != 0 || range == null
            || range.MoveEndpointByRange(collapsed, selection, onto) != 0
            || range.MoveEndpointByUnit(onto, TextUnit_Character, before ? -maxLength : maxLength, out _) != 0
            // A character is one or more UTF-16 units; the range already holds no more than maxLength of them.
            || range.GetText(4 * maxLength, out var text) != 0)
        {
            return "";
        }
        return text ?? "";
    }

    private static int Int(IUIAutomationElement element, int property) =>
        element.GetCurrentPropertyValue(property, out var value) == 0 && value is int i ? i : 0;

    private static bool Bool(IUIAutomationElement element, int property) =>
        element.GetCurrentPropertyValue(property, out var value) == 0 && value is true;

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
        /// <summary>The <c>Length</c> property's getter.</summary>
        [PreserveSig] int GetLength(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationTextRange? element);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextRange
    {
        [PreserveSig] int Clone(out IUIAutomationTextRange? clonedRange);
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
        /// <param name="endpoint">TextPatternRangeEndpoint: 0 start, 1 end.</param>
        /// <param name="unit">TextUnit: 0 character.</param>
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
