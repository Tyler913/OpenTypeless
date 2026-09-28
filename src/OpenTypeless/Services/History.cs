using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTypeless.Services;

public enum DictationStatus { Recording, Processing, Done, PolishFailed, Failed }

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
                _ => DictationStatus.Failed,
            };

        public override void Write(Utf8JsonWriter writer, DictationStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                DictationStatus.Recording => "recording",
                DictationStatus.Processing => "processing",
                DictationStatus.Done => "done",
                DictationStatus.PolishFailed => "polishFailed",
                _ => "failed",
            });
    }
}

/// <summary>
/// Every dictation is saved (audio + transcripts) so nothing is ever lost to a failure.
/// UI-thread only, like the macOS <c>@MainActor</c> store.
/// </summary>
public sealed class HistoryStore
{
    public static HistoryStore Shared { get; } = new();

    public event Action? Changed;

    private readonly List<DictationRecord> _records = new();
    public IReadOnlyList<DictationRecord> Records => _records;

    private const int KeepRecords = 200;
    private const int KeepAudioRecords = 30;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private HistoryStore()
    {
        Load();
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
        Prune();
    }

    private void Prune()
    {
        for (var index = 0; index < _records.Count; index++)
        {
            var record = _records[index];
            if (index >= KeepRecords)
            {
                try { Directory.Delete(record.Folder, recursive: true); } catch { }
            }
            else if (index >= KeepAudioRecords && record.Status is DictationStatus.Done or DictationStatus.PolishFailed)
            {
                try { File.Delete(record.AudioPath); } catch { }
            }
        }
        if (_records.Count > KeepRecords) _records.RemoveRange(KeepRecords, _records.Count - KeepRecords);
    }
}
