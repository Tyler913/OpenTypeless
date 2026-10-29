using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary><c>Cancelled</c>: stopped with Esc after <see cref="TypelessCore.CancelPolicy.KeepAfterSeconds"/>, transcribed but not inserted, kept for a day.</summary>
public enum DictationStatus { Recording, Processing, Done, PolishFailed, Failed, Cancelled }

public static class DictationStatusExtensions
{
    public static HistoryPruning.Kind PruningKind(this DictationStatus status) => status switch
    {
        DictationStatus.Recording or DictationStatus.Processing => HistoryPruning.Kind.InProgress,
        DictationStatus.Done or DictationStatus.PolishFailed => HistoryPruning.Kind.Finished,
        DictationStatus.Failed => HistoryPruning.Kind.Failed,
        _ => HistoryPruning.Kind.Cancelled,
    };

    /// <summary>As stored in session.json, the same as macOS: "done", "polishFailed", …</summary>
    public static string RawValue(this DictationStatus status) => status switch
    {
        DictationStatus.Recording => "recording",
        DictationStatus.Processing => "processing",
        DictationStatus.Done => "done",
        DictationStatus.PolishFailed => "polishFailed",
        DictationStatus.Cancelled => "cancelled",
        _ => "failed",
    };
}

/// <summary>One dictation. Stored as <c>Sessions\&lt;id&gt;\session.json</c> next to its <c>audio.wav</c>, same schema as macOS.</summary>
public sealed class DictationRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("date")][JsonConverter(typeof(Iso8601Converter))] public DateTimeOffset Date { get; init; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("status")][JsonConverter(typeof(StatusConverter))] public DictationStatus Status { get; set; } = DictationStatus.Recording;
    [JsonPropertyName("rawText")] public string RawText { get; set; } = "";
    [JsonPropertyName("polishedText")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PolishedText { get; set; }
    /// <summary>What went in when snippets changed the final text (see SnippetExpander); null when the final text went in as it was.</summary>
    [JsonPropertyName("insertedText")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? InsertedText { get; set; }
    [JsonPropertyName("error")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
    /// <summary>Per-chunk transcripts, so a retry only re-sends the chunks that failed.</summary>
    [JsonPropertyName("chunkTexts")] public Dictionary<int, string> ChunkTexts { get; set; } = new();
    /// <summary>
    /// How the recording was cut into those chunks (<see cref="TypelessCore.Chunker.Layout"/>; null before it was saved): a
    /// retry that cuts it differently can't reuse them by index.
    /// </summary>
    [JsonPropertyName("chunkLayout")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ChunkLayout { get; set; }
    /// <summary>
    /// The language hint those chunks were transcribed with ("" auto-detect; null before it was saved): a retry with
    /// another one sends them again (see <see cref="TypelessCore.DictationSettings.ReusesTranscripts"/>).
    /// </summary>
    [JsonPropertyName("chunkLanguage")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ChunkLanguage { get; set; }
    [JsonPropertyName("timing")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DictationTiming? Timing { get; set; }
    /// <summary>What processing it cost so far in USD, retries included (null when nothing could be priced).</summary>
    [JsonPropertyName("cost")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Cost { get; set; }
    /// <summary>Its words are in the usage totals (so re-transcribing it doesn't count them twice).</summary>
    [JsonPropertyName("counted")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Counted { get; set; }
    /// <summary>
    /// The app profile it was made with (see <see cref="TypelessCore.AppProfile"/>), so a retry uses that profile again while
    /// it exists; null when none applied.
    /// </summary>
    [JsonPropertyName("profileID")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProfileId { get; set; }
    /// <summary>That profile's name, for History.</summary>
    [JsonPropertyName("profileName")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProfileName { get; set; }
    /// "translate" for a translation dictation (see <see cref="TranslationTarget.RecordMode"/>), null for plain dictation:
    /// records from before translation, and older versions reading newer ones, see a dictation.
    /// </summary>
    [JsonPropertyName("mode")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
    /// <summary>The language a translation dictation is written in (<see cref="TranslationTarget.Id"/>).</summary>
    [JsonPropertyName("targetLanguage")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TargetLanguage { get; set; }
    /// <summary>
    /// Where the audio came from: a dictation (not written), or a transcribed audio or video file (see
    /// <see cref="Session.FileTranscriber"/>).
    /// </summary>
    [JsonPropertyName("source")][JsonConverter(typeof(RecordSourceConverter))][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RecordSource Source { get; set; }
    /// <summary>The name of the file it was transcribed from.</summary>
    [JsonPropertyName("sourceName")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceName { get; set; }

    /// <summary>What a translation dictation is translated into; null for plain dictation. A retry translates it again.</summary>
    [JsonIgnore]
    public TranslationTarget? Translation
    {
        get => TranslationTarget.FromRecord(Mode, TargetLanguage);
        set
        {
            Mode = value is null ? null : TranslationTarget.RecordMode;
            TargetLanguage = value?.Id;
        }
    }

    /// <summary>What the speaker said: cleaned up, or as transcribed. Word counts go by it, not by what snippets put in.</summary>
    [JsonIgnore] public string FinalText => PolishedText ?? RawText;
    /// <summary>What was (or would be) inserted: the final text with its snippets expanded.</summary>
    [JsonIgnore] public string OutputText => InsertedText ?? FinalText;

    /// <summary>Transcribed from a file rather than dictated: never pasted, and not in the Home page's dictation totals.</summary>
    [JsonIgnore] public bool IsFile => Source == RecordSource.File;

    [JsonIgnore] public string Folder => Path.Combine(AppPaths.Sessions, Id);
    [JsonIgnore] public string AudioPath => Path.Combine(Folder, "audio.wav");
    /// <summary>Where a file's sound is written while it's read; renamed to <see cref="AudioPath"/> once all of it is there.</summary>
    [JsonIgnore] public string PartialAudioPath => Path.Combine(Folder, "audio.partial.wav");
    [JsonIgnore] public bool HasAudio => File.Exists(AudioPath);

    /// <summary>
    /// This dictation as History exports it. A record keeps no time zone, so its time is written in this PC's, with the
    /// offset that zone had on that date.
    /// </summary>
    public HistoryExport.Entry ExportEntry() =>
        new(Date, HistoryExport.UtcOffsetMinutes(Date, TimeZoneInfo.Local), Duration, Status.RawValue(), FinalText, RawText, Cost);

    /// <summary>Records have value semantics (a struct on macOS): the store and the session each keep their own copy.</summary>
    public DictationRecord Copy()
    {
        var copy = (DictationRecord)MemberwiseClone();
        copy.ChunkTexts = new Dictionary<int, string>(ChunkTexts);
        copy.Timing = Timing?.Copy();
        return copy;
    }

    private sealed class Iso8601Converter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    private sealed class StatusConverter : JsonConverter<DictationStatus>
    {
        public override DictationStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() switch
            {
                "recording" => DictationStatus.Recording,
                "processing" => DictationStatus.Processing,
                "done" => DictationStatus.Done,
                "polishFailed" => DictationStatus.PolishFailed,
                "cancelled" => DictationStatus.Cancelled,
                _ => DictationStatus.Failed,
            };

        public override void Write(Utf8JsonWriter writer, DictationStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.RawValue());
    }
}

/// <summary>Where the wait after the key was released went, in seconds.</summary>
public sealed class DictationTiming
{
    /// <summary>Release → complete raw transcript (only the last chunk is usually left by then).</summary>
    [JsonPropertyName("transcription")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Transcription { get; set; }
    /// <summary>Chunks the backup speech-to-text route answered because the main one was late or failed.</summary>
    [JsonPropertyName("transcriptionBackupChunks")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TranscriptionBackupChunks { get; set; }
    [JsonPropertyName("polishFirstToken")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PolishFirstToken { get; set; }
    [JsonPropertyName("polish")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Polish { get; set; }
    [JsonPropertyName("polishModel")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PolishModel { get; set; }
    [JsonPropertyName("usedBackup")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? UsedBackup { get; set; }
    /// <summary>The last segment's transcript came from a speculative tail, sent while the speaker paused before letting go.</summary>
    [JsonPropertyName("speculativeTail")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SpeculativeTail { get; set; }
    /// <summary>The speech-to-text route that answered the last segment transcribed: "primary" or "backup".</summary>
    [JsonPropertyName("lastChunkRoute")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastChunkRoute { get; set; }
    /// <summary>How long the clean-up taken over at release had been running by then (started while the speaker paused).</summary>
    [JsonPropertyName("polishHeadStart")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PolishHeadStart { get; set; }
    /// <summary>The clean-up was sent the text around the cursor to spell names by (see <see cref="TypelessCore.CursorContext"/>); the text isn't kept.</summary>
    [JsonPropertyName("usedContext")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? UsedContext { get; set; }
    /// <summary>Release → the text ready and handed over for delivery (focus is checked, then it's pasted or copied at once).</summary>
    [JsonPropertyName("delivered")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Delivered { get; set; }
    /// <summary>
    /// "pasted"; "copied" (focus wasn't a text field); "failed" (the paste didn't land, or wasn't allowed; the text is on
    /// the clipboard).
    /// </summary>
    [JsonPropertyName("delivery")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Delivery { get; set; }

    public DictationTiming Copy() => (DictationTiming)MemberwiseClone();

    /// <summary>"转写 0.8 秒 · 整理 1.2 秒（首字 0.4 秒，备用 deepseek/…）"</summary>
    [JsonIgnore]
    public string? Summary
    {
        get
        {
            static string Secs(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + L(" 秒", " s");
            var parts = new List<string>();
            if (Transcription is { } transcription)
            {
                var backup = TranscriptionBackupChunks is { } n
                    ? n == 1 ? L("（备用转写 1 段）", " (backup for 1 segment)") : L($"（备用转写 {n} 段）", $" (backup for {n} segments)")
                    : "";
                parts.Add(L("转写 ", "Transcription ") + Secs(transcription) + backup);
            }
            if (Polish is { } polish)
            {
                var detail = new List<string>();
                if (PolishFirstToken is { } firstToken) detail.Add(L("首字 ", "first token ") + Secs(firstToken));
                if (PolishModel is { } model) detail.Add((UsedBackup == true ? L("备用 ", "backup ") : "") + model);
                if (UsedContext == true) detail.Add(L("参考了光标附近的文字", "with text around the cursor"));
                parts.Add(L("整理 ", "Clean-up ") + Secs(polish)
                          + (detail.Count == 0 ? "" : L("（", " (") + string.Join(L("，", ", "), detail) + L("）", ")")));
            }
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Every dictation is saved (audio + transcripts) so nothing is ever lost to a failure. What expires, by the
/// retention setting and a cap on the recordings' total size, is decided by <see cref="HistoryPruning"/>: finished
/// dictations lose their recording first and their text later; failed ones keep theirs for at least a month, so they
/// can be re-sent.
/// UI-thread only, like the macOS <c>@MainActor</c> store.
/// </summary>
public sealed class HistoryStore
{
    public static HistoryStore Shared { get; } = new();

    public event Action? Changed;

    private readonly List<DictationRecord> _records = new();
    public IReadOnlyList<DictationRecord> Records => _records;

    private readonly DispatcherQueueTimer? _retentionTimer;
    /// <summary>
    /// Recording sizes by record ID (0 = none), measured once a dictation is over, since its recording doesn't change
    /// after that: the size cap then doesn't look at every file on disk after each dictation.
    /// </summary>
    private readonly Dictionary<string, long> _audioSizes = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private HistoryStore()
    {
        Load();
        // Expiry is by age, so a tray app that runs for days has to check on its own.
        _retentionTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_retentionTimer != null)
        {
            _retentionTimer.Interval = TimeSpan.FromHours(1);
            _retentionTimer.Tick += (_, _) => ApplyRetention();
            _retentionTimer.Start();
        }
    }

    public DictationRecord Create()
    {
        var now = DateTimeOffset.Now;
        var id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var record = new DictationRecord { Id = id, Date = now, ChunkLayout = TypelessCore.Chunker.Layout };
        Directory.CreateDirectory(record.Folder);
        _records.Insert(0, record);
        Save(record);
        Changed?.Invoke();
        return record.Copy();
    }

    public void Update(DictationRecord record)
    {
        var stored = record.Copy();
        var index = _records.FindIndex(r => r.Id == record.Id);
        if (index >= 0) _records[index] = stored; else _records.Insert(0, stored);
        Save(stored);
        Changed?.Invoke();
        if (record.Status is DictationStatus.Done or DictationStatus.PolishFailed) ApplyRetention();
    }

    public void Delete(DictationRecord record)
    {
        _records.RemoveAll(r => r.Id == record.Id);
        try { Directory.Delete(record.Folder, recursive: true); } catch { /* already gone */ }
        _audioSizes.Remove(record.Id);
        Changed?.Invoke();
    }

    private static void Save(DictationRecord record)
    {
        try
        {
            Directory.CreateDirectory(record.Folder);
            var path = Path.Combine(record.Folder, "session.json");
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(record, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Load()
    {
        var loaded = new List<DictationRecord>();
        foreach (var folder in Directory.EnumerateDirectories(AppPaths.Sessions))
        {
            if (!File.Exists(Path.Combine(folder, "session.json")) && !File.Exists(Path.Combine(folder, "audio.wav")))
            {
                // What a delete that couldn't finish left behind (a cancelled file's transcoder still had its output open):
                // no record and no recording, so nothing in it is anyone's.
                try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                continue;
            }
            try
            {
                var record = JsonSerializer.Deserialize<DictationRecord>(File.ReadAllBytes(Path.Combine(folder, "session.json")), Json);
                if (record != null && record.Id.Length > 0) loaded.Add(record);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or FormatException) { }
        }
        loaded.Sort((a, b) => b.Date.CompareTo(a.Date));
        // A record still "recording"/"processing" at launch means the app quit mid-way; the audio is
        // on disk, so surface it as failed and retryable.
        foreach (var record in loaded.Where(r => r.Status is DictationStatus.Recording or DictationStatus.Processing))
        {
            record.Status = DictationStatus.Failed;
            if (record.IsFile && !record.HasAudio)
            {
                // Quit while the file was still being read: what was read of it is no use.
                try { File.Delete(record.PartialAudioPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            record.Error = FileTranscription.InterruptedMessage(record.IsFile, record.HasAudio);
            Save(record);
        }
        _records.AddRange(loaded);
        ApplyRetention();
    }

    /// <summary>
    /// Deletes what has expired by the retention setting, and the oldest recordings while they take more than
    /// <see cref="HistoryPruning.AudioBudget"/> (see <see cref="HistoryPruning"/>).
    /// </summary>
    public void ApplyRetention()
    {
        var entries = _records.Select(r => new HistoryPruning.Entry(r.Status.PruningKind(), r.Date, AudioBytes(r))).ToList();
        var actions = HistoryPruning.Plan(entries, AppSettings.Shared.HistoryRetention.MaxAge(), DateTimeOffset.Now);
        var kept = new List<DictationRecord>();
        var changed = false;
        for (var index = 0; index < _records.Count; index++)
        {
            var record = _records[index];
            switch (actions[index])
            {
                case HistoryPruning.Action.RemoveAudio:
                    try { File.Delete(record.AudioPath); } catch { }
                    _audioSizes[record.Id] = 0;
                    kept.Add(record);
                    changed = true;
                    break;
                case HistoryPruning.Action.Remove:
                    try { Directory.Delete(record.Folder, recursive: true); } catch { }
                    _audioSizes.Remove(record.Id);
                    changed = true;
                    break;
                default:
                    kept.Add(record);
                    break;
            }
        }
        _records.Clear();
        _records.AddRange(kept);
        if (changed) Changed?.Invoke();
    }

    private long AudioBytes(DictationRecord record)
    {
        if (_audioSizes.TryGetValue(record.Id, out var known)) return known;
        long size;
        try
        {
            var file = new FileInfo(record.AudioPath);
            size = file.Exists ? file.Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            size = 0;
        }
        // One still being recorded is measured again next time.
        if (record.Status.PruningKind() != HistoryPruning.Kind.InProgress) _audioSizes[record.Id] = size;
        return size;
    }

    /// <summary>Bytes used by every saved dictation (recordings and transcripts). Safe to call off the UI thread.</summary>
    public static long StorageBytes()
    {
        try
        {
            return new DirectoryInfo(AppPaths.Sessions).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file =>
            {
                try { return file.Length; } catch (IOException) { return 0; }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
