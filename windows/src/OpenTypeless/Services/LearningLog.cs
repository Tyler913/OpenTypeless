using System.Text.Json;
using System.Text.Json.Nodes;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// A local record of what learning from edits did after each paste: which app, whether its field could be read, why
/// the watch ended, and each change found with the reason it was or wasn't learned. It is how a "nothing ever gets
/// learned" report can be looked into. One JSON object per line in <c>learning-log.jsonl</c> in the data folder;
/// never sent anywhere, and cut back to the newest half once it passes 512 KB.
/// </summary>
public static class LearningLog
{
    private const int Limit = 512 * 1024;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string FilePath => Path.Combine(AppPaths.Support, "learning-log.jsonl");

    public static void Write(string eventName, string? app, JsonObject fields)
    {
        fields["time"] = DateTimeOffset.Now.ToString("o");
        fields["event"] = eventName;
        if (app != null) fields["app"] = app;
        var line = fields.ToJsonString(Options) + "\n";
        _ = Task.Run(() =>
        {
            lock (Lock)
            {
                try
                {
                    File.AppendAllText(FilePath, line);
                    TrimIfNeeded();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        });
    }

    public static void AddReview(JsonObject fields, CorrectionLearner.Review review)
    {
        if (review.Skipped != null) fields["skipped"] = review.Skipped;
        var changes = new JsonArray();
        foreach (var change in review.Changes)
        {
            changes.Add(new JsonObject
            {
                ["heard"] = change.Correction.Heard,
                ["corrected"] = change.Correction.Corrected,
                ["rejected"] = change.Rejected,
            });
        }
        fields["changes"] = changes;
    }

    private static void TrimIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= Limit) return;
        var bytes = File.ReadAllBytes(FilePath);
        var start = bytes.Length - Limit / 2;
        var newline = Array.IndexOf(bytes, (byte)'\n', start);
        if (newline < 0) return;
        File.WriteAllBytes(FilePath, bytes[(newline + 1)..]);
    }
}
