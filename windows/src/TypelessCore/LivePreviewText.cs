using System.Globalization;

namespace TypelessCore;

/// <summary>What the live preview shows above the recording capsule: the end of what's been recognised so far.</summary>
public static class LivePreviewText
{
    /// <summary>
    /// The last <paramref name="limit"/> characters on one line, starting at a word boundary when one is near, with a
    /// leading "…" when anything was cut. Characters are whole (an emoji or a CJK character is never split).
    /// </summary>
    public static string Tail(string text, int limit = 60)
    {
        var line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '\t');
        var info = new StringInfo(line);
        if (info.LengthInTextElements <= limit) return line;
        var tail = info.SubstringByTextElements(info.LengthInTextElements - limit);
        // Latin text: drop the cut-off word at the start, when a space comes early enough.
        var space = tail.IndexOf(' ');
        if (space >= 0 && new StringInfo(tail[..space]).LengthInTextElements < 15) tail = tail[(space + 1)..];
        return "…" + tail;
    }
}
