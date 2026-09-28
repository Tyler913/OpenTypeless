using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypelessCore;

/// <summary>
/// Counts words the way a reader would across languages: every Chinese character and Japanese kana is a word, and
/// every run of letters or digits in other scripts is one word ("don't", "e-mail" and "3.5" count once).
/// </summary>
public static class WordCount
{
    public static int Count(string text)
    {
        var runes = text.EnumerateRunes().Select(r => r.Value).ToArray();
        var count = 0;
        var inWord = false;
        for (var i = 0; i < runes.Length; i++)
        {
            var scalar = runes[i];
            if (IsCjkWordCharacter(scalar))
            {
                count++;
                inWord = false;
            }
            else if (IsWordCharacter(scalar))
            {
                if (!inWord) count++;
                inWord = true;
            }
            else if (inWord && IsJoiner(scalar) && i + 1 < runes.Length && IsWordCharacter(runes[i + 1]) && !IsCjkWordCharacter(runes[i + 1]))
            {
                // Inside a word: "don't", "e-mail", "3.5", "1,000".
            }
            else
            {
                inWord = false;
            }
        }
        return count;
    }

    /// <summary>Han ideographs and Japanese kana, where each character is a word. (Korean separates words with spaces.)</summary>
    public static bool IsCjkWordCharacter(int scalar) =>
        scalar is >= 0x4E00 and <= 0x9FFF        // CJK Unified Ideographs
            or >= 0x3400 and <= 0x4DBF           // Extension A
            or >= 0x20000 and <= 0x3134F         // Extensions B–G
            or >= 0xF900 and <= 0xFAFF           // Compatibility Ideographs
            or >= 0x3040 and <= 0x309F           // Hiragana
            or >= 0x30A0 and <= 0x30FF;          // Katakana

    private static bool IsWordCharacter(int scalar) => Rune.GetUnicodeCategory(new Rune(scalar)) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
        or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    private static bool IsJoiner(int scalar) => scalar is '\'' or '’' or '-' or '.' or ',' or '_';
}

/// <summary>One day's dictation: what was said, how long it took to say, and what it cost.</summary>
public sealed class DayUsage
{
    [JsonPropertyName("words")] public int Words { get; set; }
    [JsonPropertyName("dictations")] public int Dictations { get; set; }
    /// <summary>Recording time, pauses included.</summary>
    [JsonPropertyName("seconds")] public double SpeakingSeconds { get; set; }
    [JsonPropertyName("sttCost")] public double TranscriptionCost { get; set; }
    [JsonPropertyName("polishCost")] public double CleanupCost { get; set; }
    /// <summary>Requests with no known price (a model without a price entered), so the cost shown is a lower bound.</summary>
    [JsonPropertyName("unpriced")] public int Unpriced { get; set; }
}

/// <summary>Totals over a range of days, with the figures the Home page shows.</summary>
public sealed record UsageTotals(int Words, int Dictations, double SpeakingSeconds, double TranscriptionCost, double CleanupCost, int Unpriced)
{
    /// <summary>Typing speed the time saved is measured against, in words per minute.</summary>
    public const int DefaultTypingWordsPerMinute = 100;

    public double Cost => TranscriptionCost + CleanupCost;

    /// <summary>How long typing the same words would have taken.</summary>
    public double TypingSeconds(double wordsPerMinute) => wordsPerMinute > 0 ? Words / wordsPerMinute * 60 : 0;

    /// <summary>Typing time minus speaking time (never negative).</summary>
    public double SavedSeconds(double wordsPerMinute) => Math.Max(0, TypingSeconds(wordsPerMinute) - SpeakingSeconds);

    /// <summary>Words per minute while recording, once there's at least a second of it.</summary>
    public double? SpeakingWordsPerMinute => SpeakingSeconds >= 1 && Words > 0 ? Words / (SpeakingSeconds / 60) : null;
}

public sealed record HeatmapCell(DateOnly Date, int Words, int Dictations, int Level, bool IsFuture);

/// <summary>
/// Daily totals of words, speaking time and spend since the app was first used. Kept in its own file
/// (<c>usage.json</c>) rather than derived from History, which drops old dictations and lets the user delete them.
/// </summary>
public sealed class UsageLedger
{
    public const int Version = 1;

    private readonly SortedDictionary<string, DayUsage> _days = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, DayUsage> Days => _days;

    public bool IsEmpty => _days.Count == 0;

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? ParseKey(string key) =>
        DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    public DayUsage? Day(DateOnly day) => _days.GetValueOrDefault(Key(day));

    private DayUsage Entry(DateOnly day)
    {
        var key = Key(day);
        if (!_days.TryGetValue(key, out var entry)) _days[key] = entry = new DayUsage();
        return entry;
    }

    /// <summary>A finished dictation (counted once, however often it's re-transcribed).</summary>
    public void AddDictation(DateOnly day, int words, double seconds)
    {
        var entry = Entry(day);
        entry.Words += Math.Max(0, words);
        entry.Dictations += 1;
        entry.SpeakingSeconds += Math.Max(0, seconds);
    }

    /// <summary>What the requests of one processing run cost (a retry costs again, and is counted again).</summary>
    public void AddCost(DateOnly day, double transcription, double cleanup, int unpriced)
    {
        if (transcription <= 0 && cleanup <= 0 && unpriced <= 0) return;
        var entry = Entry(day);
        entry.TranscriptionCost += Math.Max(0, transcription);
        entry.CleanupCost += Math.Max(0, cleanup);
        entry.Unpriced += Math.Max(0, unpriced);
    }

    /// <summary>Totals from <paramref name="from"/> to <paramref name="to"/>, inclusive; null ends are open.</summary>
    public UsageTotals Totals(DateOnly? from = null, DateOnly? to = null)
    {
        var fromKey = from is { } f ? Key(f) : null;
        var toKey = to is { } t ? Key(t) : null;
        int words = 0, dictations = 0, unpriced = 0;
        double seconds = 0, transcription = 0, cleanup = 0;
        foreach (var (key, day) in _days)
        {
            if (fromKey != null && string.CompareOrdinal(key, fromKey) < 0) continue;
            if (toKey != null && string.CompareOrdinal(key, toKey) > 0) continue;
            words += day.Words;
            dictations += day.Dictations;
            seconds += day.SpeakingSeconds;
            transcription += day.TranscriptionCost;
            cleanup += day.CleanupCost;
            unpriced += day.Unpriced;
        }
        return new UsageTotals(words, dictations, seconds, transcription, cleanup, unpriced);
    }

    public UsageTotals Today(DateOnly today) => Totals(today, today);

    public UsageTotals Month(DateOnly today) => Totals(new DateOnly(today.Year, today.Month, 1), today);

    /// <summary>
    /// The current run of days with dictation (still alive when today has none yet, like GitHub's), and the longest.
    /// </summary>
    public (int Current, int Longest) Streaks(DateOnly today)
    {
        bool Active(DateOnly day) => Day(day) is { Dictations: > 0 };
        var current = 0;
        var cursor = Active(today) ? today : today.AddDays(-1);
        while (Active(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }
        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var (key, day) in _days)
        {
            if (day.Dictations <= 0 || ParseKey(key) is not { } date) continue;
            run = previous is { } p && p.AddDays(1) == date ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = date;
        }
        return (current, Math.Max(longest, current));
    }

    /// <summary>
    /// GitHub-style activity grid: <paramref name="weeks"/> columns of seven days ending with the week that holds
    /// <paramref name="today"/>, each week starting on <paramref name="firstWeekday"/> (0 = Sunday). Level 0 means no
    /// dictation; 1–4 are the quartiles of the busy days shown, so the shading adapts to how much you talk.
    /// </summary>
    public List<HeatmapCell[]> Heatmap(DateOnly today, int weeks, int firstWeekday)
    {
        weeks = Math.Max(1, weeks);
        var offset = ((int)today.DayOfWeek - firstWeekday % 7 + 7) % 7;
        var start = today.AddDays(-offset - 7 * (weeks - 1));
        var words = new int[weeks * 7];
        for (var i = 0; i < words.Length; i++) words[i] = Day(start.AddDays(i))?.Words ?? 0;
        var thresholds = Thresholds(words.Where((_, i) => start.AddDays(i) <= today));
        var columns = new List<HeatmapCell[]>();
        for (var week = 0; week < weeks; week++)
        {
            var column = new HeatmapCell[7];
            for (var day = 0; day < 7; day++)
            {
                var index = week * 7 + day;
                var date = start.AddDays(index);
                column[day] = new HeatmapCell(date, words[index], Day(date)?.Dictations ?? 0, Level(words[index], thresholds), date > today);
            }
            columns.Add(column);
        }
        return columns;
    }

    /// <summary>Upper bounds of levels 1–3: the 25th, 50th and 75th percentile of the non-zero counts.</summary>
    public static int[] Thresholds(IEnumerable<int> counts)
    {
        var busy = counts.Where(c => c > 0).Order().ToArray();
        if (busy.Length == 0) return [0, 0, 0];
        int At(double p) => busy[(int)Math.Floor(p * (busy.Length - 1))];
        return [At(0.25), At(0.5), At(0.75)];
    }

    public static int Level(int words, int[] thresholds)
    {
        if (words <= 0) return 0;
        if (words <= thresholds[0]) return 1;
        if (words <= thresholds[1]) return 2;
        if (words <= thresholds[2]) return 3;
        return 4;
    }

    // MARK: - Storage

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class Stored
    {
        [JsonPropertyName("version")] public int Version { get; set; } = UsageLedger.Version;
        [JsonPropertyName("days")] public Dictionary<string, DayUsage> Days { get; set; } = new();
    }

    public string ToJson() => JsonSerializer.Serialize(new Stored { Days = new Dictionary<string, DayUsage>(_days) }, Json);

    public static UsageLedger FromJson(string json)
    {
        var ledger = new UsageLedger();
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json, Json);
            foreach (var (key, day) in stored?.Days ?? [])
            {
                if (ParseKey(key) != null && day != null) ledger._days[key] = day;
            }
        }
        catch (JsonException) { }
        return ledger;
    }

    /// <summary>Null when there's no file yet (first run of a version that keeps it).</summary>
    public static UsageLedger? Load(string path)
    {
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return new UsageLedger();
        }
    }

    public void Save(string path)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
