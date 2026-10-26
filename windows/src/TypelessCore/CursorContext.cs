using System.Globalization;
using System.Text;

namespace TypelessCore;

/// <summary>
/// The text around the cursor in the field being dictated into, which the clean-up gets as a spelling reference: the
/// names and terms a reply is about (a colleague's name in the thread, a product, a code identifier) are already on
/// screen, and they're what speech recognition gets wrong most. The app reads it once, as recording starts, through UI
/// Automation (Accessibility on macOS), only from a field that says it's editable and not a password field, and only when
/// the speaker switched it on. It's sent with the transcript and never kept. Like Wispr Flow's nearby text; Superwhisper
/// and VoiceInk read the screen instead (a screenshot and OCR), which needs screen recording permission and reads far more
/// than the field.
/// </summary>
public static class CursorContext
{
    /// <summary>
    /// At most this many characters go to the clean-up: the message being replied to, or the paragraph being written,
    /// with room on either side of the cursor, for a few hundred tokens.
    /// </summary>
    public const int Limit = 1_500;

    /// <summary>
    /// Which parts of a field were read only in part, at a length the app chose rather than at the start or end of the
    /// field. Such a read can end in the middle of a character (half an emoji, a family emoji without its last member).
    /// </summary>
    [Flags]
    public enum Cut
    {
        None = 0,
        /// <summary>The text before the selection doesn't reach back to the start of the field.</summary>
        Before = 1,
        /// <summary>The selection was read only up to a length.</summary>
        Selection = 2,
        /// <summary>The text after the selection doesn't reach the end of the field.</summary>
        After = 4,
    }

    /// <summary>
    /// The excerpt of a field's whole text that goes to the clean-up. <paramref name="selection"/> is where the cursor or the
    /// selection is, in UTF-16 offsets (as UI Automation and Accessibility count them), clamped to the text and rounded
    /// down to whole characters; null when the field doesn't say, and then the cursor is taken to be at the end, where it
    /// is when replying.
    /// </summary>
    public static string? Excerpt(string text, (int Location, int Length)? selection, int limit = Limit)
    {
        var location = Math.Clamp(selection?.Location ?? text.Length, 0, text.Length);
        var length = Math.Clamp(selection?.Length ?? 0, 0, text.Length - location);
        var start = Boundary(text, location);
        var end = Math.Max(start, Boundary(text, location + length));
        return Excerpt(text[..start], text[start..end], text[end..], limit);
    }

    /// <summary>
    /// The selection, cut to <paramref name="limit"/> characters, and as much of the text on either side of it as fits,
    /// split evenly (what one side doesn't need goes to the other), with every run of spaces or blank lines squeezed to
    /// one. A word cut in two at either edge is dropped, so a name is never shown half spelled (Chinese and Japanese,
    /// written without spaces, are cut anywhere), and so is a character a read cut through the middle of (U+FFFD, or half
    /// a surrogate pair). Characters are whole: an emoji or a CJK character is never split.
    ///
    /// Where a part was read only in part (<paramref name="cut"/>), its cut end can be anywhere, even inside a character:
    /// the read's last character may be the first two of a family emoji's three people, which looks whole. So it's trimmed
    /// back past the first space or punctuation mark from that end (see <see cref="IsBoundary"/>), beyond which the text
    /// reads as in the whole field, and all of it goes when there's none. A selection read in part isn't followed by the
    /// text after it, which isn't what comes after its first part. Null when nothing but whitespace is left.
    /// </summary>
    public static string? Excerpt(string before, string selected, string after, int limit = Limit, Cut cut = Cut.None)
    {
        if (cut.HasFlag(Cut.Before))
        {
            var parts = Elements(before);
            var boundary = parts.FindIndex(IsBoundary);
            before = boundary < 0 ? "" : string.Concat(parts.Skip(boundary + 1));
        }
        if (cut.HasFlag(Cut.Selection))
        {
            selected = BeforeLastBoundary(selected);
            after = "";
        }
        else if (cut.HasFlag(Cut.After))
        {
            after = BeforeLastBoundary(after);
        }
        var selection = Elements(Squeezed(selected));
        if (selection.Count > Math.Max(0, limit)) selection.RemoveRange(Math.Max(0, limit), selection.Count - Math.Max(0, limit));
        var head = Elements(Squeezed(before));
        var tail = Elements(Squeezed(after));
        var room = Math.Max(0, limit - selection.Count);
        var beforeCount = Math.Min(head.Count, Math.Max(room / 2, room - tail.Count));
        var afterCount = Math.Min(tail.Count, room - beforeCount);
        var previous = beforeCount < head.Count ? head[head.Count - beforeCount - 1] : null;
        head.RemoveRange(0, head.Count - beforeCount);
        if (previous != null) DropCutWordAtStart(head, previous);
        var next = afterCount < tail.Count ? tail[afterCount] : null;
        tail.RemoveRange(afterCount, tail.Count - afterCount);
        if (next != null) DropCutWordAtEnd(tail, next);
        // Squeezed again where the parts meet: a space on both sides of the cursor is still one.
        var text = Trimmed(Squeezed(string.Concat(head) + string.Concat(selection) + string.Concat(tail)));
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// A character a part read only in part is trimmed back to: a single code point that is a space, a line break or a
    /// punctuation mark. Nothing joins onto either side of one to make a character of it, so the text on its far side from
    /// the cut is split into characters as it is in the whole field.
    /// </summary>
    internal static bool IsBoundary(string element) =>
        Rune.TryGetRuneAt(element, 0, out var rune) && rune.Utf16SequenceLength == element.Length
        && (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune));

    /// <summary>The text up to its last <see cref="IsBoundary"/> character, or "" when it has none.</summary>
    private static string BeforeLastBoundary(string text)
    {
        var parts = Elements(text);
        var boundary = parts.FindLastIndex(IsBoundary);
        return boundary < 0 ? "" : string.Concat(parts.Take(boundary));
    }

    /// <summary>
    /// Each run of whitespace as one line break when it has one, otherwise as one space: indentation and blank lines
    /// would only use up the room.
    /// </summary>
    internal static string Squeezed(string text)
    {
        var result = new StringBuilder(text.Length);
        char? run = null;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (run == null || IsNewline(c)) run = IsNewline(c) ? '\n' : ' ';
                continue;
            }
            if (run is { } space) result.Append(space);
            run = null;
            result.Append(c);
        }
        if (run is { } last) result.Append(last);
        return result.ToString();
    }

    /// <summary>Line breaks, as Swift's <c>Character.isNewline</c> counts them.</summary>
    private static bool IsNewline(char c) => c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>Drops the word a cut went through at the start of <paramref name="head"/>: up to its first space, or all of it.</summary>
    private static void DropCutWordAtStart(List<string> head, string previous)
    {
        if (head.Count == 0 || !IsSpacedWordCharacter(previous) || !IsSpacedWordCharacter(head[0])) return;
        var space = head.FindIndex(IsWhiteSpace);
        head.RemoveRange(0, space < 0 ? head.Count : space + 1);
    }

    /// <summary>The same at the end of <paramref name="tail"/>; <paramref name="next"/> is the character after the cut.</summary>
    private static void DropCutWordAtEnd(List<string> tail, string next)
    {
        if (tail.Count == 0 || !IsSpacedWordCharacter(next) || !IsSpacedWordCharacter(tail[^1])) return;
        var space = tail.FindLastIndex(IsWhiteSpace);
        tail.RemoveRange(Math.Max(0, space), tail.Count - Math.Max(0, space));
    }

    private static bool IsWhiteSpace(string element) => Rune.TryGetRuneAt(element, 0, out var rune) && Rune.IsWhiteSpace(rune);

    /// <summary>
    /// A letter or digit of a script that puts spaces between words (Latin, Cyrillic, Korean…), so a cut next to it can be
    /// in the middle of a word.
    /// </summary>
    private static bool IsSpacedWordCharacter(string element)
    {
        if (!Rune.TryGetRuneAt(element, 0, out var rune)) return false;
        return (Rune.IsLetter(rune) || Rune.IsNumber(rune)) && !TranscriptJoiner.JoinsWithoutSpace(rune);
    }

    /// <summary>Trims whitespace, U+FFFD and half surrogate pairs from both ends.</summary>
    private static string Trimmed(string text)
    {
        static bool Trims(char c) => char.IsWhiteSpace(c) || c == '\uFFFD' || char.IsSurrogate(c);
        int start = 0, end = text.Length;
        // A whole surrogate pair (an emoji) at an edge stays: only a half one is trimmed.
        while (start < end && Trims(text[start]) && !(char.IsHighSurrogate(text[start]) && start + 1 < end && char.IsLowSurrogate(text[start + 1]))) start++;
        while (end > start && Trims(text[end - 1]) && !(char.IsLowSurrogate(text[end - 1]) && end - 2 >= start && char.IsHighSurrogate(text[end - 2]))) end--;
        return text[start..end];
    }

    /// <summary>The text's characters (grapheme clusters, like Swift's <c>Character</c>).</summary>
    private static List<string> Elements(string text)
    {
        var elements = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text, i);
            elements.Add(text.Substring(i, length));
            i += length;
        }
        return elements;
    }

    /// <summary>The start of the character <paramref name="offset"/> falls in (the end for an offset past it).</summary>
    internal static int Boundary(string text, int offset)
    {
        for (var i = 0; i < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text, i);
            if (i + length > offset) return i;
            i += length;
        }
        return text.Length;
    }
}
