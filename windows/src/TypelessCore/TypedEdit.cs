namespace TypelessCore;

/// <summary>
/// Follows the keys pressed after a dictation is pasted, to know what the dictated text became without reading the
/// text field: for apps whose fields can't be read, this is how a fix is seen. Same as the macOS <c>TypedEdit</c>.
/// </summary>
/// <remarks>
/// It starts as the pasted text with the cursor at its end, and applies typing, Backspace / Delete and the arrow keys,
/// with or without a selection. A click puts the cursor somewhere it can't see (<see cref="CursorLost"/>); a word
/// selected by double-clicking or dragging finds it again (<see cref="Select"/>), when that text occurs once in the
/// dictated text. Anything it can't follow exactly stops it (<see cref="Apply"/> returns false), and then only the state
/// before that key counts: a key while the cursor is lost, the cursor leaving the dictated text (it doesn't know what's
/// around it), a word jump across Chinese or Japanese text (word boundaries there come from a dictionary), a jump to the
/// start or end of a line, and up / down.
/// </remarks>
public sealed class TypedEdit
{
    public enum Unit { Character, Word }

    public abstract record Key
    {
        /// <summary>Text typed, or pasted.</summary>
        public sealed record Insert(string Text) : Key;
        public sealed record DeleteBackward(Unit Unit) : Key;
        public sealed record DeleteForward(Unit Unit) : Key;
        public sealed record Left(Unit Unit, bool Extend) : Key;
        public sealed record Right(Unit Unit, bool Extend) : Key;
    }

    private readonly List<string> _characters;

    /// <summary>Where the cursor is; with <see cref="Anchor"/> it spans the selection.</summary>
    public int Caret { get; private set; }
    public int Anchor { get; private set; }
    /// <summary>False after a click, until a selection shows where the cursor is.</summary>
    public bool KnowsCursor { get; private set; } = true;

    public TypedEdit(string pasted)
    {
        _characters = Graphemes(pasted);
        Caret = Anchor = _characters.Count;
    }

    public string Text => string.Concat(_characters);

    private (int Start, int End) Selection => (Math.Min(Caret, Anchor), Math.Max(Caret, Anchor));

    /// <summary>A click: the cursor is somewhere unknown until <see cref="Select"/> finds it.</summary>
    public void CursorLost() => KnowsCursor = false;

    /// <summary>
    /// The user selected <paramref name="text"/> (double-click, drag): selects it here, if it occurs exactly once. False
    /// otherwise, and then the cursor stays unknown.
    /// </summary>
    public bool Select(string text)
    {
        var needle = Graphemes(text);
        if (needle.Count == 0 || needle.Count > _characters.Count) return false;
        int? found = null;
        for (var start = 0; start + needle.Count <= _characters.Count; start++)
        {
            if (!_characters.Skip(start).Take(needle.Count).SequenceEqual(needle)) continue;
            if (found != null) return false; // more than once: can't tell which
            found = start;
        }
        if (found is not { } at) return false;
        Anchor = at;
        Caret = at + needle.Count;
        KnowsCursor = true;
        return true;
    }

    /// <summary>Applies one key; false when it can't be followed (the state stays as before it).</summary>
    public bool Apply(Key key)
    {
        if (!KnowsCursor) return false;
        switch (key)
        {
            case Key.Insert insert:
                if (insert.Text.Length == 0) return false;
                ReplaceSelection(Graphemes(insert.Text));
                return true;
            case Key.DeleteBackward or Key.DeleteForward:
            {
                var (start, end) = Selection;
                if (start != end)
                {
                    ReplaceSelection([]);
                    return true;
                }
                var backward = key is Key.DeleteBackward;
                var unit = key is Key.DeleteBackward b ? b.Unit : ((Key.DeleteForward)key).Unit;
                if (Position(Caret, backward, unit) is not { } target || target == Caret) return false;
                var from = Math.Min(Caret, target);
                _characters.RemoveRange(from, Math.Abs(Caret - target));
                Caret = Anchor = from;
                return true;
            }
            case Key.Left or Key.Right:
            {
                var backward = key is Key.Left;
                var (unit, extend) = key is Key.Left l ? (l.Unit, l.Extend) : (((Key.Right)key).Unit, ((Key.Right)key).Extend);
                var (start, end) = Selection;
                int target;
                if (!extend && start != end && unit == Unit.Character)
                {
                    // An arrow collapses a selection to its side.
                    target = backward ? start : end;
                }
                else if (Position(Caret, backward, unit) is { } moved)
                {
                    target = moved;
                }
                else
                {
                    return false;
                }
                Caret = target;
                if (!extend) Anchor = target;
                return true;
            }
            default:
                return false;
        }
    }

    private void ReplaceSelection(List<string> replacement)
    {
        var (start, end) = Selection;
        _characters.RemoveRange(start, end - start);
        _characters.InsertRange(start, replacement);
        Caret = Anchor = start + replacement.Count;
    }

    /// <summary>The cursor position one unit away, or null when that leaves the text or can't be told.</summary>
    private int? Position(int index, bool backward, Unit unit)
    {
        if (unit == Unit.Character)
        {
            var target = backward ? index - 1 : index + 1;
            return target >= 0 && target <= _characters.Count ? target : null;
        }
        // Over the spaces and punctuation next to the cursor, then over the word.
        var i = index;
        string At(int at) => _characters[backward ? at - 1 : at];
        bool Inside(int at) => backward ? at > 0 : at < _characters.Count;
        while (Inside(i) && !IsWordCharacter(At(i))) i += backward ? -1 : 1;
        if (!Inside(i)) return null; // would run past the dictated text
        var start = i;
        while (Inside(i) && IsWordCharacter(At(i)))
        {
            if (IsIdeographic(At(i))) return null;
            i += backward ? -1 : 1;
        }
        // A word that runs to the edge may go on outside the dictated text: only where it ends at a separator.
        if (!Inside(i) && i != start) return null;
        return i;
    }

    internal static bool IsWordCharacter(string c)
    {
        var rune = System.Text.Rune.GetRuneAt(c, 0);
        return System.Text.Rune.IsLetterOrDigit(rune) || c is "_" or "'" or "’";
    }

    /// <summary>Han, kana and hangul, where word boundaries come from a dictionary.</summary>
    internal static bool IsIdeographic(string c) => c.EnumerateRunes().Any(r => r.Value is
        >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF
        or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F);

    private static List<string> Graphemes(string text)
    {
        var list = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }
}
