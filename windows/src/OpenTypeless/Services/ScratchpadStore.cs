using Microsoft.UI.Dispatching;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// The Scratchpad's notes on disk (<c>scratchpad.json</c> next to the settings, see <see cref="Scratchpad"/>). Typing is
/// saved a second after it stops, everything else at once. UI-thread only, like the history store.
/// </summary>
public sealed class ScratchpadStore
{
    private static ScratchpadStore? _shared;
    public static ScratchpadStore Shared => _shared ??= new ScratchpadStore(Path.Combine(AppPaths.Support, "scratchpad.json"));

    /// <summary>The notes changed (an edit, a new or deleted note, a dictation added to today's note).</summary>
    public event Action? Changed;

    public Scratchpad Notes { get; }
    /// <summary>Null for the snapshots' sample notes, which are never saved.</summary>
    private readonly string? _path;
    private DispatcherQueueTimer? _saveTimer;

    public ScratchpadStore(string? path, Scratchpad? notes = null)
    {
        _path = path;
        Notes = notes ?? (path != null ? Scratchpad.Load(path) : new Scratchpad());
    }

    /// <summary>A few notes for the UI snapshots (the user's own words, so not translated; the same on macOS).</summary>
    public static ScratchpadStore Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var notes = new Scratchpad();
        var first = notes.Add(now.AddDays(-3));
        notes.SetText("Launch review on Thursday\n- Check the demo setup\n- Draft the release notes", first.Id, now.AddDays(-2));
        var second = notes.Add(now.AddHours(-1));
        notes.SetText("Call Sam back about the contract.", second.Id, now.AddHours(-1));
        notes.Capture("Send the weekly update to the team and mention the new launch date.", Today, now.AddMinutes(-10));
        return new ScratchpadStore(null, notes);
    }

    /// <summary>The local date, as <see cref="Scratchpad.Capture"/> keys a day's note.</summary>
    private static string Today => UsageLedger.Key(DateOnly.FromDateTime(DateTime.Now));

    public string Add()
    {
        var id = Notes.Add().Id;
        ScheduleSave();
        Changed?.Invoke();
        return id;
    }

    public void SetText(string text, string id)
    {
        if (Notes.Note(id) is not { } note || note.Text == text) return;
        Notes.SetText(text, id);
        ScheduleSave();
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        Notes.Delete(id);
        Save();
        Changed?.Invoke();
    }

    public void Restore(string id)
    {
        Notes.Restore(id);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Drops the notes left empty, but <paramref name="id"/>'s.</summary>
    public void RemoveBlank(string? id)
    {
        if (!Notes.Entries.Any(note => note.IsBlank && note.Id != id)) return;
        Notes.RemoveBlank(id);
        Changed?.Invoke();
    }

    /// <summary>A dictation that had nowhere to go, added to today's note (see <see cref="Scratchpad.Capture"/>) and saved at once.</summary>
    public void Capture(string text)
    {
        Notes.Capture(text, Today);
        Save();
        Changed?.Invoke();
    }

    private void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            if (DispatcherQueue.GetForCurrentThread() is not { } dispatcher)
            {
                Save();
                return;
            }
            _saveTimer = dispatcher.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromSeconds(1);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => Save();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes what's there now (see <see cref="SafeFile"/>); also on quit, for the last keystrokes.</summary>
    public void Save()
    {
        _saveTimer?.Stop();
        if (_path != null) Notes.Save(_path);
    }
}
