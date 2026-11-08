using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypelessCore;

/// <summary>A note in the Scratchpad: plain text, when it was made and when it was last changed.</summary>
public sealed record ScratchpadNote
{
    public required string Id { get; init; }
    public string Text { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    /// <summary>
    /// The local day ("2026-11-07") whose dictations that had nowhere to go are added to this note (see
    /// <see cref="Scratchpad.Capture"/>); null for a note the user started.
    /// </summary>
    public string? CaptureDay { get; init; }
    /// <summary>
    /// When it was deleted. A deleted note is never listed; it's kept a while (see <see cref="Scratchpad.KeepDeleted"/>)
    /// so Undo can bring it back.
    /// </summary>
    public DateTimeOffset? Deleted { get; init; }

    /// <summary>Its first line with text (see <see cref="Scratchpad.Title"/>); "" for an empty note.</summary>
    public string Title => Scratchpad.Title(Text);
    /// <summary>Nothing but spaces and line breaks in it.</summary>
    public bool IsBlank => Scratchpad.Title(Text).Length == 0;
}

/// <summary>
/// The Scratchpad's notes, as <c>scratchpad.json</c> in the data folder keeps them: plain text, listed by when they were
/// last edited. Times are whole seconds (as the file stores them), so a note is listed in the same place after a
/// relaunch. Ported line by line from the macOS app; <c>testdata/scratchpad-cases.json</c> holds cases both test suites read.
/// </summary>
public sealed class Scratchpad
{
    private readonly List<ScratchpadNote> _entries;

    /// <summary>How long a deleted note is kept for Undo.</summary>
    public static readonly TimeSpan KeepDeleted = TimeSpan.FromDays(30);
    /// <summary>The most characters a title shows; the list cuts it shorter to fit.</summary>
    public const int TitleLength = 80;

    public Scratchpad(IEnumerable<ScratchpadNote>? entries = null)
    {
        _entries = entries?.ToList() ?? [];
    }

    /// <summary>Every note, deleted ones included, in the order the file keeps them (a new note last).</summary>
    public IReadOnlyList<ScratchpadNote> Entries => _entries;

    /// <summary>
    /// The file is there but couldn't be read (or a damaged one couldn't be moved aside): the notes standing in for it
    /// this session are never saved over it.
    /// </summary>
    public bool IsUnreadable { get; set; }

    /// <summary>The notes to list: not deleted, the last edited first (then the last made, then the one further down the file).</summary>
    public List<ScratchpadNote> Notes =>
        _entries.Select((note, index) => (Note: note, Index: index))
            .Where(item => item.Note.Deleted == null)
            .OrderByDescending(item => item.Note.Modified)
            .ThenByDescending(item => item.Note.Created)
            .ThenByDescending(item => item.Index)
            .Select(item => item.Note)
            .ToList();

    public ScratchpadNote? Note(string id) => _entries.FirstOrDefault(note => note.Id == id);

    /// <summary>A new, empty note, made <paramref name="now"/>.</summary>
    public ScratchpadNote Add(DateTimeOffset? now = null, string? id = null)
    {
        var stamp = Stamp(now ?? DateTimeOffset.UtcNow);
        var note = new ScratchpadNote { Id = id ?? NewId(), Text = "", Created = stamp, Modified = stamp };
        _entries.Add(note);
        return note;
    }

    /// <summary>The note's text as the user edited it; it counts as edited <paramref name="now"/> only when the text changed.</summary>
    public void SetText(string text, string id, DateTimeOffset? now = null)
    {
        var index = _entries.FindIndex(note => note.Id == id);
        if (index < 0 || _entries[index].Text == text) return;
        _entries[index] = _entries[index] with { Text = text, Modified = Stamp(now ?? DateTimeOffset.UtcNow) };
    }

    /// <summary>Takes the note off the list; <see cref="Restore"/> brings it back.</summary>
    public void Delete(string id, DateTimeOffset? now = null)
    {
        var index = _entries.FindIndex(note => note.Id == id);
        if (index < 0) return;
        _entries[index] = _entries[index] with { Deleted = Stamp(now ?? DateTimeOffset.UtcNow) };
    }

    public void Restore(string id)
    {
        var index = _entries.FindIndex(note => note.Id == id);
        if (index < 0) return;
        _entries[index] = _entries[index] with { Deleted = null };
    }

    /// <summary>Drops the notes left empty (a new note never typed in), but <paramref name="id"/>'s, the one being edited.</summary>
    public void RemoveBlank(string? id = null) => _entries.RemoveAll(note => note.IsBlank && note.Id != id);

    /// <summary>
    /// A dictation that had nowhere to go: added to the end of the note for <paramref name="day"/> (the local date,
    /// "2026-11-07"), or a new note for that day when there's none (or it was deleted). Returns the note's ID.
    /// </summary>
    public string Capture(string text, string day, DateTimeOffset? now = null, string? newId = null)
    {
        var stamp = Stamp(now ?? DateTimeOffset.UtcNow);
        var index = _entries.FindLastIndex(note => note.CaptureDay == day && note.Deleted == null);
        if (index >= 0)
        {
            _entries[index] = _entries[index] with { Text = Appending(text, _entries[index].Text), Modified = stamp };
            return _entries[index].Id;
        }
        var id = newId ?? NewId();
        _entries.Add(new ScratchpadNote { Id = id, Text = text, Created = stamp, Modified = stamp, CaptureDay = day });
        return id;
    }

    /// <summary>Forgets the notes deleted longer ago than <see cref="KeepDeleted"/>.</summary>
    public void Purge(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        _entries.RemoveAll(note => note.Deleted is { } deleted && current - deleted > KeepDeleted);
    }

    /// <summary>The line breaks a title is cut at: those Swift's <c>Character.isNewline</c> knows.</summary>
    private static readonly char[] NewLines = ['\n', '\v', '\f', '\r', '\u0085', ' ', ' '];

    /// <summary>
    /// The first line that has text, without the spaces around it, cut to <see cref="TitleLength"/> characters (whole
    /// ones, as a reader counts them); "" when there's none.
    /// </summary>
    public static string Title(string text)
    {
        foreach (var line in text.Split(NewLines))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var info = new StringInfo(trimmed);
            return info.LengthInTextElements <= TitleLength ? trimmed : info.SubstringByTextElements(0, TitleLength);
        }
        return "";
    }

    /// <summary>
    /// <paramref name="addition"/> after <paramref name="existing"/>, a blank line between them; the spaces and line
    /// breaks <paramref name="existing"/> ends with give way to it. An empty note just gets <paramref name="addition"/>.
    /// </summary>
    public static string Appending(string addition, string existing)
    {
        var head = existing.TrimEnd(' ', '\t', '\n', '\r');
        return head.Length == 0 ? addition : head + "\n\n" + addition;
    }

    /// <summary><paramref name="date"/> cut to whole seconds, in UTC, as the file keeps it.</summary>
    internal static DateTimeOffset Stamp(DateTimeOffset date) => DateTimeOffset.FromUnixTimeSeconds(date.ToUnixTimeSeconds());

    private static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    // MARK: - Storage

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The notes in a saved file. Each one is read on its own: an entry without text is skipped, one without an ID (or
    /// with one already taken) gets a new one, a time that can't be read falls back to the note's other time or
    /// <paramref name="now"/>, and fields this version doesn't know are ignored. Null when the text isn't a notes file at all.
    /// </summary>
    public static Scratchpad? Decode(string json, DateTimeOffset? now = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is not JsonObject obj || obj["notes"] is not JsonArray items) return null;
        var fallback = Stamp(now ?? DateTimeOffset.UtcNow);
        var notes = new List<ScratchpadNote>();
        var ids = new HashSet<string>();
        foreach (var node in items)
        {
            if (node is not JsonObject item || Text(item["text"]) is not { } text) continue;
            var id = Text(item["id"]) ?? "";
            if (id.Length == 0 || ids.Contains(id)) id = NewId();
            ids.Add(id);
            var created = ParseDate(Text(item["created"]));
            var modified = ParseDate(Text(item["modified"]));
            var captureDay = Text(item["captureDay"]);
            notes.Add(new ScratchpadNote
            {
                Id = id,
                Text = text,
                Created = created ?? modified ?? fallback,
                Modified = modified ?? created ?? fallback,
                CaptureDay = string.IsNullOrEmpty(captureDay) ? null : captureDay,
                Deleted = ParseDate(Text(item["deleted"])),
            });
        }
        return new Scratchpad(notes);

        static string? Text(JsonNode? value) =>
            value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
    }

    /// <summary>The file's contents. Empty notes aren't kept.</summary>
    public string ToJson()
    {
        var notes = new JsonArray();
        foreach (var note in _entries.Where(note => !note.IsBlank))
        {
            var item = new JsonObject
            {
                ["id"] = note.Id,
                ["text"] = note.Text,
                ["created"] = FormatDate(note.Created),
                ["modified"] = FormatDate(note.Modified),
            };
            if (note.CaptureDay is { } day) item["captureDay"] = day;
            if (note.Deleted is { } deleted) item["deleted"] = FormatDate(deleted);
            notes.Add(item);
        }
        return new JsonObject { ["version"] = 1, ["notes"] = notes }.ToJsonString(Json);
    }

    /// <summary>
    /// The saved notes, without those deleted too long ago; none when there's no file yet. A file that isn't a notes file
    /// is moved aside (see <see cref="SafeFile"/>) before an empty Scratchpad takes its place, so the next save doesn't
    /// overwrite it. One that still can't be read after a few tries is held by another program, not damaged: it stays,
    /// and the notes standing in for it this session are never saved.
    /// </summary>
    public static Scratchpad Load(string path, DateTimeOffset? now = null)
    {
        if (!File.Exists(path)) return new Scratchpad();
        string text;
        try
        {
            text = SafeFile.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Scratchpad { IsUnreadable = true };
        }
        if (Decode(text, now) is { } notes)
        {
            notes.Purge(now);
            return notes;
        }
        // Not moved aside (the folder is read-only, say): then it's the only copy, and isn't saved over either.
        return new Scratchpad { IsUnreadable = SafeFile.MoveAside(path) == null };
    }

    /// <summary>Writes the file whole (see <see cref="SafeFile.WriteAllText"/>). False when it wasn't written.</summary>
    public bool Save(string path)
    {
        if (IsUnreadable) return false;
        try
        {
            SafeFile.WriteAllText(path, ToJson());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"2026-11-07T09:30:00Z", as History's files write their dates.</summary>
    internal static string FormatDate(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static readonly string[] DateFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    /// <summary>ISO 8601, with or without fractions of a second (kept to whole seconds).</summary>
    internal static DateTimeOffset? ParseDate(string? text) =>
        text != null && DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? Stamp(date)
            : null;
}

/// <summary>
/// "Save to Scratchpad when there's nowhere to type": whether a dictation's text, once delivered, is also added to the
/// Scratchpad (see <see cref="Scratchpad.Capture"/>).
/// </summary>
public static class ScratchpadCapture
{
    /// <summary>Where a dictation's text ended up.</summary>
    public enum Outcome
    {
        /// <summary>Pasted at the cursor.</summary>
        Pasted,
        /// <summary>Typed into the Scratchpad's own editor.</summary>
        Scratchpad,
        /// <summary>Focus was on something that takes no text: the clipboard.</summary>
        NotEditable,
        /// <summary>Another window came to the front, or an app with other settings: the clipboard.</summary>
        MovedAway,
        /// <summary>The paste was sent but nothing took it: the clipboard.</summary>
        NotPasted,
        /// <summary>No permission to paste: the clipboard.</summary>
        NoPermission,
        /// <summary>The screen locked while it was processed: the clipboard.</summary>
        Locked,
        /// <summary>Re-transcribed from History, which copies it and keeps it: the clipboard.</summary>
        FromHistory,
        /// <summary>Esc, or another dictation took over: nowhere.</summary>
        Cancelled,
    }

    public static readonly Outcome[] AllOutcomes = Enum.GetValues<Outcome>();

    /// <summary>Its name, the same as on macOS.</summary>
    public static string RawValue(this Outcome outcome) => outcome switch
    {
        Outcome.Pasted => "pasted",
        Outcome.Scratchpad => "scratchpad",
        Outcome.NotEditable => "notEditable",
        Outcome.MovedAway => "movedAway",
        Outcome.NotPasted => "notPasted",
        Outcome.NoPermission => "noPermission",
        Outcome.Locked => "locked",
        Outcome.FromHistory => "fromHistory",
        _ => "cancelled",
    };

    /// <summary>Only text that went to the clipboard because there was nowhere to put it, while the setting is on.</summary>
    public static bool Saves(string text, Outcome outcome, bool enabled) =>
        enabled && TypelessCore.Scratchpad.Title(text).Length > 0
        && outcome is Outcome.NotEditable or Outcome.MovedAway or Outcome.NotPasted or Outcome.NoPermission or Outcome.Locked;
}
