using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

/// <summary>The Scratchpad's notes: titles, order, what a dictation with nowhere to go adds, and how the file is read and written.</summary>
public class ScratchpadTests
{
    /// <summary>testdata/scratchpad-cases.json, shared with the macOS tests so both apps agree.</summary>
    private static JsonObject Cases([CallerFilePath] string source = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "scratchpad-cases.json")))!.AsObject();

    private static DateTimeOffset Date(string text) => Scratchpad.ParseDate(text)!.Value;

    [Fact]
    public void TitlesFollowSharedCases()
    {
        foreach (var node in Cases()["titles"]!.AsArray())
        {
            Assert.True(Scratchpad.Title((string)node!["text"]!) == (string)node["title"]!, node.ToJsonString());
        }
    }

    [Fact]
    public void AppendingFollowsSharedCases()
    {
        foreach (var node in Cases()["appending"]!.AsArray())
        {
            Assert.True(Scratchpad.Appending((string)node!["addition"]!, (string)node["existing"]!) == (string)node["expected"]!, node.ToJsonString());
        }
    }

    [Fact]
    public void CaptureDecisionFollowsSharedCases()
    {
        var cases = Cases()["capture"]!.AsArray();
        // Every outcome is covered, by its name on both platforms.
        Assert.Equal(ScratchpadCapture.AllOutcomes.Select(o => o.RawValue()).Order().ToList(),
                     cases.Select(c => (string)c!["outcome"]!).Distinct().Order().ToList());
        foreach (var node in cases)
        {
            var outcome = ScratchpadCapture.AllOutcomes.Single(o => o.RawValue() == (string)node!["outcome"]!);
            Assert.True(ScratchpadCapture.Saves((string)node!["text"]!, outcome, (bool)node["enabled"]!) == (bool)node["saves"]!, node.ToJsonString());
        }
    }

    [Fact]
    public void FilesAreReadAsSharedCasesSay()
    {
        foreach (var node in Cases()["files"]!.AsArray())
        {
            var name = (string)node!["name"]!;
            var now = Date((string)node["now"]!);
            var decoded = Scratchpad.Decode((string)node["json"]!, now);
            if (node["listed"] is not JsonArray listed)
            {
                Assert.True(decoded == null, name);
                continue;
            }
            Assert.True(decoded != null, name);
            var notes = decoded!;
            notes.Purge(now);
            Assert.True(notes.Entries.Count == (int)node["entries"]!, name);
            Assert.True(notes.Entries.Select(n => n.Id).Distinct().Count() == notes.Entries.Count, name + ": IDs are unique");
            var shown = notes.Notes;
            Assert.True(shown.Count == listed.Count, name);
            for (var i = 0; i < listed.Count; i++)
            {
                var note = shown[i];
                var expected = listed[i]!;
                if ((string?)expected["id"] is { } id) Assert.True(note.Id == id, name); else Assert.False(string.IsNullOrEmpty(note.Id), name);
                Assert.True(note.Text == (string)expected["text"]!, name);
                Assert.True(note.CaptureDay == (string?)expected["captureDay"], name);
                if ((string?)expected["created"] is { } created) Assert.True(note.Created == Date(created), name);
                if ((string?)expected["modified"] is { } modified) Assert.True(note.Modified == Date(modified), name);
            }
        }
    }

    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var now = Date("2026-11-07T09:30:00Z");
        var notes = new Scratchpad();
        var first = notes.Add(now, "first");
        notes.SetText("Groceries\nmilk", first.Id, now.AddSeconds(5));
        var second = notes.Add(now.AddSeconds(10), "second");
        notes.SetText("Call Sam — 会议", second.Id, now.AddSeconds(10));
        notes.Capture("Heard while nothing had focus.", "2026-11-07", now.AddSeconds(20), "captured");
        notes.Delete(first.Id, now.AddSeconds(30));
        notes.Add(now.AddSeconds(40), "blank"); // never typed in: not saved

        var json = notes.ToJson();
        Assert.Contains("会议", json); // written as is, not escaped
        var loaded = Scratchpad.Decode(json, now)!;
        Assert.Equal(notes.Entries.Where(n => n.Id != "blank"), loaded.Entries);
        Assert.Equal(["captured", "second"], loaded.Notes.Select(n => n.Id));
        Assert.True(loaded.Note("first")!.Deleted == now.AddSeconds(30));
        Assert.Equal("2026-11-07", loaded.Note("captured")!.CaptureDay);
    }

    [Fact]
    public void EditsMoveANoteUpOnlyWhenTheTextChanges()
    {
        var now = Date("2026-11-07T09:30:00Z");
        var notes = new Scratchpad();
        notes.Add(now, "a");
        notes.SetText("A", "a", now);
        notes.Add(now.AddSeconds(60), "b");
        notes.SetText("B", "b", now.AddSeconds(60));
        Assert.Equal(["b", "a"], notes.Notes.Select(n => n.Id));
        notes.SetText("A", "a", now.AddSeconds(120)); // the same text: not an edit
        Assert.Equal(["b", "a"], notes.Notes.Select(n => n.Id));
        notes.SetText("A again", "a", now.AddSeconds(120.7));
        Assert.Equal(["a", "b"], notes.Notes.Select(n => n.Id));
        Assert.Equal(now.AddSeconds(120), notes.Note("a")!.Modified); // whole seconds, as the file keeps them
        Assert.Equal(now, notes.Note("a")!.Created);
    }

    [Fact]
    public void DictationsWithNowhereToGoShareTheDaysNote()
    {
        var now = Date("2026-11-07T09:30:00Z");
        var notes = new Scratchpad();
        var first = notes.Capture("One.", "2026-11-07", now, "day1");
        var second = notes.Capture("Two.", "2026-11-07", now.AddSeconds(60), "unused");
        Assert.Equal("day1", first);
        Assert.Equal("day1", second);
        Assert.Equal("One.\n\nTwo.", notes.Note("day1")!.Text);
        Assert.Equal(now.AddSeconds(60), notes.Note("day1")!.Modified);
        // The next day starts a note of its own.
        Assert.Equal("day2", notes.Capture("Three.", "2026-11-08", now.AddSeconds(86_400), "day2"));
        // A deleted day's note isn't added to: a new one takes its place, and Undo brings back the old one as it was.
        notes.Delete("day2", now.AddSeconds(86_500));
        Assert.Equal("day2b", notes.Capture("Four.", "2026-11-08", now.AddSeconds(86_600), "day2b"));
        notes.Restore("day2");
        Assert.Equal(["day2b", "day2", "day1"], notes.Notes.Select(n => n.Id));
        Assert.Equal("Three.", notes.Note("day2")!.Text);
    }

    [Fact]
    public void BlankNotesAreDroppedButTheOneBeingEdited()
    {
        var now = Date("2026-11-07T09:30:00Z");
        var notes = new Scratchpad();
        notes.Add(now, "kept");
        notes.SetText("Text", "kept", now);
        notes.Add(now, "blank");
        notes.Add(now, "editing");
        notes.SetText(" \n", "blank", now);
        notes.RemoveBlank("editing");
        Assert.Equal(["kept", "editing"], notes.Entries.Select(n => n.Id));
        notes.RemoveBlank();
        Assert.Equal(["kept"], notes.Entries.Select(n => n.Id));
    }

    private static string TemporaryFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "scratchpad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ADamagedFileIsMovedAsideNotOverwritten()
    {
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "scratchpad.json");
            Assert.Empty(Scratchpad.Load(file).Entries); // no file yet
            File.WriteAllBytes(file, new byte[64]); // what a power cut mid-write can leave
            var notes = Scratchpad.Load(file);
            Assert.Empty(notes.Entries);
            Assert.False(notes.IsUnreadable);
            var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
            Assert.Single(names);
            Assert.StartsWith("scratchpad.json.corrupt-", names[0]);
            notes.Capture("Kept.", "2026-11-07");
            Assert.True(notes.Save(file));
            Assert.Equal(["Kept."], Scratchpad.Load(file).Notes.Select(n => n.Text));
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void AnUnreadableFileIsNeverSavedOver()
    {
        // Held files block reading only on Windows (other systems' locks are advisory).
        if (!OperatingSystem.IsWindows()) return;
        var folder = TemporaryFolder();
        try
        {
            var file = Path.Combine(folder, "scratchpad.json");
            const string original = "{\"notes\":[{\"id\":\"a\",\"text\":\"Mine\"}]}";
            File.WriteAllText(file, original);
            Scratchpad notes;
            using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                notes = Scratchpad.Load(file);
                Assert.True(notes.IsUnreadable);
            }
            notes.Capture("Would replace it.", "2026-11-07");
            Assert.False(notes.Save(file));
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Equal(["scratchpad.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
