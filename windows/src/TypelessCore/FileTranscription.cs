using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypelessCore;

/// <summary>
/// Where a History entry's audio came from. Stored as <c>source</c> in session.json, only for a file: a record without one
/// (every dictation, and everything saved before files could be transcribed) is a dictation, and a value this version
/// doesn't know reads as a dictation too (see <see cref="RecordSourceConverter"/>), rather than losing the whole record.
/// </summary>
public enum RecordSource { Dictation, File }

/// <summary>Writes <see cref="RecordSource"/> as "dictation" or "file", as macOS does; anything else reads as a dictation.</summary>
public sealed class RecordSourceConverter : JsonConverter<RecordSource>
{
    public override RecordSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
            return RecordSource.Dictation;
        }
        return reader.TokenType == JsonTokenType.String && reader.GetString() == "file" ? RecordSource.File : RecordSource.Dictation;
    }

    public override void Write(Utf8JsonWriter writer, RecordSource value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == RecordSource.File ? "file" : "dictation");
}

/// <summary>
/// Transcribing an audio or video file (a voice memo, a meeting, a video) with the pipeline dictation uses: chunked at
/// pauses, each chunk retried and backed up, then optionally cleaned up in parts, never pasted. The file is decoded into
/// the record's recording first, so History's Re-transcribe works as it does for a dictation; then the recording is handed
/// to the pipeline a block at a time from disk, so an hour of it (about 115 MB) is never all in memory.
///
/// A file mustn't get in the way of dictating: it's transcribed with fewer requests at once than a dictation uses, no new
/// audio is handed out while a dictation is under way, and its clean-up sends a few parts at a time.
/// </summary>
public static class FileTranscription
{
    /// <summary>Speech-to-text requests out at once for a file (a dictation has 3 of its own).</summary>
    public const int MaxConcurrentRequests = 2;
    /// <summary>
    /// Chunks handed to the pipeline and still waiting for their transcript before more audio is read: enough to keep
    /// <see cref="MaxConcurrentRequests"/> busy, few enough that only a minute or two of audio sits in memory.
    /// </summary>
    public const int MaxOutstandingChunks = 4;
    /// <summary>Clean-up parts out at once (a dictation sends all of its parts together; a long file's transcript has dozens).</summary>
    public const int CleanUpConcurrency = 2;
    /// <summary>How much of the recording is read from disk at a time.</summary>
    public const double BlockSeconds = 10;
    /// <summary>
    /// How often the transcripts so far are saved while a file is transcribed, in seconds: if the app quits, a retry only
    /// sends what wasn't transcribed yet.
    /// </summary>
    public const double SaveInterval = 20;

    /// <summary>
    /// How many chunks may be out before more audio is handed over: none while a dictation is under way, so a file never
    /// competes with it for more than the requests it already has out.
    /// </summary>
    public static int OutstandingLimit(bool dictating) => dictating ? 0 : MaxOutstandingChunks;

    /// <summary>
    /// What transcribing <paramref name="seconds"/> of audio should cost at <paramref name="price"/>, in USD; null when the
    /// model has no per-minute price (one billed by the token, or no price entered) or the length isn't known. Silence
    /// isn't sent, so it's a ceiling.
    /// </summary>
    public static double? EstimatedCost(double seconds, ModelPrice? price) =>
        price?.PerMinute is { } perMinute && seconds > 0 ? seconds / 60 * perMinute : null;

    /// <summary>
    /// Where a file being transcribed has got to. Kept in memory only: a file still under way when the app quits shows as
    /// interrupted at the next launch, like a dictation.
    /// </summary>
    public abstract record Progress
    {
        /// <summary>Waiting for the file before it.</summary>
        public sealed record Queued : Progress;
        /// <summary>Being read into the recording; how far, 0…1 (null when its length isn't known).</summary>
        public sealed record Reading(double? Part) : Progress;
        public sealed record Transcribing(int Done, int Total) : Progress;
        public sealed record CleaningUp : Progress;

        /// <summary>"Reading the file… 40%", "Transcribing 3/12 segments"</summary>
        public string Label => this switch
        {
            Reading { Part: { } part } => L($"正在读取文件… {Percent(part)}%", $"Reading the file… {Percent(part)}%"),
            Reading => L("正在读取文件…", "Reading the file…"),
            Transcribing t => L($"正在转写 {t.Done}/{t.Total} 段", $"Transcribing {t.Done}/{t.Total} segments"),
            CleaningUp => L("正在整理…", "Cleaning up…"),
            _ => L("排队中…", "Waiting…"),
        };

        /// <summary>How far along a progress bar is, 0…1; null when there's nothing to measure (it spins instead).</summary>
        public double? Fraction => this switch
        {
            Reading { Part: { } part } => Math.Clamp(part, 0, 1),
            Transcribing { Total: > 0 } t => Math.Min((double)t.Done / t.Total, 1),
            _ => null,
        };

        private static int Percent(double part) => (int)Math.Round(Math.Clamp(part, 0, 1) * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>Whether <paramref name="error"/> (or one it wraps) says the disk is full.</summary>
    public static bool IsDiskFull(Exception error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL, as HRESULTs.
            if (e.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027)) return true;
        }
        return false;
    }
}

/// <summary>Why a file couldn't be transcribed, as History shows it on the record.</summary>
public sealed class FileTranscriptionException : Exception
{
    public enum Kind { NotFound, PermissionDenied, Unsupported, NoAudioTrack, DiskFull, WriteFailed }

    public Kind Reason { get; }
    /// <summary>For <see cref="Kind.Unsupported"/>, what the system said when that says more; for <see cref="Kind.WriteFailed"/>, why.</summary>
    public string? Detail { get; }

    public FileTranscriptionException(Kind reason, string? detail = null, Exception? inner = null) : base(detail, inner)
    {
        Reason = reason;
        Detail = detail;
    }

    public override string Message => Reason switch
    {
        Kind.NotFound => L("找不到这个文件（可能已被移动或删除）", "The file can't be found (it may have been moved or deleted)"),
        Kind.PermissionDenied => L("没有读取这个文件的权限", "No permission to read this file"),
        Kind.Unsupported when Detail is { } detail => L($"无法读取这个文件：已损坏，或是不支持的格式（{detail}）",
                                                        $"Can't read this file: it's damaged or in a format that isn't supported ({detail})"),
        Kind.Unsupported => L("无法读取这个文件：已损坏，或是不支持的格式", "Can't read this file: it's damaged or in a format that isn't supported"),
        Kind.NoAudioTrack => L("这个文件里没有音轨", "This file has no audio track"),
        Kind.DiskFull => L("磁盘空间不足，无法保存音频", "Not enough disk space to save the audio"),
        _ => L("无法保存音频：", "Can't save the audio: ") + Detail,
    };

    /// <summary>A failure to write the recording.</summary>
    public static FileTranscriptionException Writing(Exception error) =>
        error as FileTranscriptionException
        ?? (FileTranscription.IsDiskFull(error)
            ? new FileTranscriptionException(Kind.DiskFull, inner: error)
            : new FileTranscriptionException(Kind.WriteFailed, error.Message, error));

    /// <summary>
    /// A failure to open or decode the file: a missing file and a refused one say so; anything else is taken for a damaged
    /// or unsupported file.
    /// </summary>
    public static FileTranscriptionException Reading(Exception error) => error switch
    {
        FileTranscriptionException known => known,
        FileNotFoundException or DirectoryNotFoundException => new FileTranscriptionException(Kind.NotFound, inner: error),
        UnauthorizedAccessException => new FileTranscriptionException(Kind.PermissionDenied, inner: error),
        // The same, as the HRESULTs Windows Runtime calls throw: ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, E_ACCESSDENIED.
        _ when error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003) => new FileTranscriptionException(Kind.NotFound, inner: error),
        _ when error.HResult == unchecked((int)0x80070005) => new FileTranscriptionException(Kind.PermissionDenied, inner: error),
        _ => new FileTranscriptionException(Kind.Unsupported, inner: error),
    };
}
