using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;

namespace OpenTypeless.Services;

/// <summary><c>Cancelled</c>: stopped with Esc after <see cref="TypelessCore.CancelPolicy.KeepAfterSeconds"/>, transcribed but not inserted, kept for a day.</summary>
public enum DictationStatus { Recording, Processing, Done, PolishFailed, Failed, Cancelled }

/// <summary>One dictation. Stored as <c>Sessions\&lt;id&gt;\session.json</c> next to its <c>audio.wav</c>, same schema as macOS.</summary>
public sealed class DictationRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("date")][JsonConverter(typeof(Iso8601Converter))] public DateTimeOffset Date { get; init; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("status")][JsonConverter(typeof(StatusConverter))] public DictationStatus Status { get; set; } = DictationStatus.Recording;
    [JsonPropertyName("rawText")] public string RawText { get; set; } = "";
    [JsonPropertyName("polishedText")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PolishedText { get; set; }
    [JsonPropertyName("error")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
    /// <summary>Per-chunk transcripts, so a retry only re-sends the chunks that failed.</summary>
    [JsonPropertyName("chunkTexts")] public Dictionary<int, string> ChunkTexts { get; set; } = new();
    [JsonPropertyName("timing")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DictationTiming? Timing { get; set; }
    /// <summary>What processing it cost so far in USD, retries included (null when nothing could be priced).</summary>
    [JsonPropertyName("cost")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Cost { get; set; }
    /// <summary>Its words are in the usage totals (so re-transcribing it doesn't count them twice).</summary>
    [JsonPropertyName("counted")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Counted { get; set; }

    /// <summary>What was (or would be) inserted.</summary>
    [JsonIgnore] public string FinalText => PolishedText ?? RawText;

    [JsonIgnore] public string Folder => Path.Combine(AppPaths.Sessions, Id);
    [JsonIgnore] public string AudioPath => Path.Combine(Folder, "audio.wav");
    [JsonIgnore] public bool HasAudio => File.Exists(AudioPath);

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
            writer.WriteStringValue(value switch
            {
                DictationStatus.Recording => "recording",
                DictationStatus.Processing => "processing",
                DictationStatus.Done => "done",
                DictationStatus.PolishFailed => "polishFailed",
                DictationStatus.Cancelled => "cancelled",
                _ => "failed",
            });
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
                parts.Add(L("整理 ", "Clean-up ") + Secs(polish)
                          + (detail.Count == 0 ? "" : L("（", " (") + string.Join(L("，", ", "), detail) + L("）", ")")));
            }
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Every dictation is saved (audio + transcripts) so nothing is ever lost to a failure. Finished
/// dictations older than the retention setting lose their recording; their text stays in History
/// among the newest <see cref="MinimumTextRecords"/>, and older ones are removed entirely. Failed dictations
/// keep everything until retried or deleted, so they can always be re-sent.
/// UI-thread only, like the macOS <c>@MainActor</c> store.
/// </summary>
public sealed class HistoryStore
{
    public static HistoryStore Shared { get; } = new();

    public event Action? Changed;

    private readonly List<DictationRecord> _records = new();
    public IReadOnlyList<DictationRecord> Records => _records;

    private const int MinimumTextRecords = 200;
    private readonly DispatcherQueueTimer? _retentionTimer;

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
        var record = new DictationRecord { Id = id, Date = now };
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
            record.Error = L("上次运行中断（音频已保存，可重试）", "Interrupted last time (audio saved — you can retry)");
            Save(record);
        }
        _records.AddRange(loaded);
        ApplyRetention();
    }

    /// <summary>Deletes what the retention setting says has expired.</summary>
    public void ApplyRetention()
    {
        var maxAge = AppSettings.Shared.HistoryRetention.MaxAge();
        var now = DateTimeOffset.Now;
        var kept = new List<DictationRecord>();
        var changed = false;
        for (var index = 0; index < _records.Count; index++)
        {
            var record = _records[index];
            if (record.Status == DictationStatus.Cancelled)
            {
                // A cancelled dictation is only a safety net: it goes a day after it was made, whatever the setting.
                if (TypelessCore.CancelPolicy.IsExpired(record.Date, now))
                {
                    try { Directory.Delete(record.Folder, recursive: true); } catch { }
                    changed = true;
                }
                else
                {
                    kept.Add(record);
                }
                continue;
            }
            var finished = record.Status is DictationStatus.Done or DictationStatus.PolishFailed;
            var expired = maxAge is { } age && now - record.Date >= age;
            if (!finished || !expired)
            {
                kept.Add(record);
                continue;
            }
            if (index >= MinimumTextRecords)
            {
                try { Directory.Delete(record.Folder, recursive: true); } catch { }
                changed = true;
            }
            else
            {
                if (record.HasAudio)
                {
                    try { File.Delete(record.AudioPath); } catch { }
                    changed = true;
                }
                kept.Add(record);
            }
        }
        _records.Clear();
        _records.AddRange(kept);
        if (changed) Changed?.Invoke();
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
