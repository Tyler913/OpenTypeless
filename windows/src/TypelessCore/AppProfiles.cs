using System.Text.Json.Nodes;

namespace TypelessCore;

/// <summary>
/// Settings for one app. When a dictation starts with that app in front, its overrides apply to the whole dictation:
/// the speech-to-text language, whether and how it's cleaned up, and whether Enter is pressed after the paste. What a
/// profile leaves unset follows the global settings. Like VoiceInk's modes and Superwhisper's auto-switching, but matched
/// by app only: reading a browser's address needs Automation permission and can hang. Nothing about the app (its name,
/// its window title) goes to the model; only the instructions the user wrote do.
/// </summary>
public sealed record AppProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();
    /// <summary>
    /// The app it's for: a bundle ID on macOS ("com.tinyspeck.slackmacgap"), an executable name on Windows ("slack.exe").
    /// Compared as <see cref="MatchKey"/> does.
    /// </summary>
    public string App { get; init; } = "";
    /// <summary>The app's name, shown in Settings and History.</summary>
    public string Name { get; init; } = "";
    /// <summary>Clean-up preferences for this app, added after the global ones; "" adds none.</summary>
    public string Instructions { get; init; } = "";
    /// <summary>Clean up with AI; null follows the global setting.</summary>
    public bool? Polish { get; init; }
    /// <summary>Press Enter (Return on macOS) after the paste; null follows the global setting.</summary>
    public bool? PressEnter { get; init; }
    /// <summary>Spoken-language hint for speech-to-text, "" to auto-detect; null follows the global setting.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// How app identifiers compare: case-insensitively (bundle IDs are, and so are Windows file names), ignoring the
    /// whitespace around them and a trailing ".exe", so "Slack.exe", "slack.EXE" and "slack" are the same app.
    /// </summary>
    public static string MatchKey(string app)
    {
        var key = app.Trim().ToLowerInvariant();
        return key.EndsWith(".exe", StringComparison.Ordinal) ? key[..^4] : key;
    }

    /// <summary>Whether this profile is for <paramref name="app"/>. A profile with no app matches nothing.</summary>
    public bool Matches(string? app)
    {
        if (app is null) return false;
        var key = MatchKey(App);
        return key.Length > 0 && key == MatchKey(app);
    }

    /// <summary>The profile for <paramref name="app"/>: the first one that matches, null when none does or the app isn't known.</summary>
    public static AppProfile? Matching(string? app, IEnumerable<AppProfile> profiles) => profiles.FirstOrDefault(p => p.Matches(app));

    /// <summary>Whether every setting follows the global one.</summary>
    public bool IsEmpty => Polish is null && PressEnter is null && Language is null && Instructions.Trim().Length == 0;

    /// <summary>The same JSON as the macOS app's: unset overrides aren't written, so they stay "follow the global setting".</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["id"] = Id, ["app"] = App, ["name"] = Name, ["instructions"] = Instructions };
        if (Polish is { } polish) json["polish"] = polish;
        if (PressEnter is { } pressEnter) json["pressEnter"] = pressEnter;
        if (Language is { } language) json["language"] = language;
        return json;
    }

    /// <summary>
    /// Lenient, so a profile saved by another version still loads: a field that's missing or of the wrong type counts as
    /// unset rather than losing every profile. Null only when it isn't an object at all. A missing ID is left empty for
    /// <see cref="WithUniqueIds"/> to fill in once.
    /// </summary>
    public static AppProfile? FromJson(JsonNode? node)
    {
        if (node is not JsonObject json) return null;
        var app = Text(json["app"]) ?? "";
        return new AppProfile
        {
            Id = Text(json["id"]) ?? "",
            App = app,
            Name = Text(json["name"]) ?? app,
            Instructions = Text(json["instructions"]) ?? "",
            Polish = Flag(json["polish"]),
            PressEnter = Flag(json["pressEnter"]),
            Language = Text(json["language"]),
        };

        static string? Text(JsonNode? value) =>
            value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;

        static bool? Flag(JsonNode? value) =>
            value is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? v.GetValue<bool>()
                : null;
    }

    /// <summary>A saved list; whatever isn't a profile in it is skipped.</summary>
    public static List<AppProfile> ListFromJson(JsonNode? node) =>
        node is JsonArray array ? array.Select(FromJson).OfType<AppProfile>().ToList() : [];

    public static JsonArray ListToJson(IEnumerable<AppProfile> profiles) => new(profiles.Select(p => (JsonNode)p.ToJson()).ToArray());

    /// <summary>
    /// The profiles with an ID each can be found by again (by an edit, Remove, or a retry from History): one saved without
    /// an ID, or with another's, gets a new one. Done once when they're loaded, and saved, so the IDs stay put.
    /// </summary>
    public static List<AppProfile> WithUniqueIds(IEnumerable<AppProfile> profiles)
    {
        var seen = new HashSet<string>();
        var result = new List<AppProfile>();
        foreach (var profile in profiles)
        {
            var unique = profile.Id.Trim().Length == 0 || seen.Contains(profile.Id)
                ? profile with { Id = Guid.NewGuid().ToString().ToUpperInvariant() }
                : profile;
            seen.Add(unique.Id);
            result.Add(unique);
        }
        return result;
    }
}

/// <summary>
/// What one dictation runs with: the global settings, with the overrides of the profile for the app it was spoken in.
/// Taken once, when the recording starts, and used for everything that dictation does (its speech-to-text, a clean-up
/// started ahead of time, the backup model, the Enter after the paste, a retry from History), so another app coming to
/// the front before the key is released doesn't change what the text was spoken for.
/// </summary>
/// <param name="Language">Spoken-language hint for speech-to-text; "" auto-detects.</param>
/// <param name="ExtraInstructions">The clean-up preferences: the global ones, then the app's.</param>
public sealed record DictationSettings(bool PolishEnabled, bool PressEnter, string Language, string ExtraInstructions)
{
    /// <summary>The profile applied; null when none was (the global settings as they are).</summary>
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }

    /// <summary>These settings with <paramref name="profile"/>'s overrides on top; unchanged without one.</summary>
    public DictationSettings Applying(AppProfile? profile) => profile is null ? this : this with
    {
        PolishEnabled = profile.Polish ?? PolishEnabled,
        PressEnter = profile.PressEnter ?? PressEnter,
        Language = profile.Language ?? Language,
        ExtraInstructions = Combine(ExtraInstructions, profile.Instructions),
        ProfileId = profile.Id,
        ProfileName = profile.Name,
    };

    /// <summary>
    /// Whether these are the settings <paramref name="app"/> (the one in front when the key is released, or when the text
    /// goes in) gets: it has the same profile as the app the dictation was started in, or, like that one, none. Otherwise
    /// the text goes to the clipboard, and Enter isn't pressed: one app's profile never sends the text in another (a
    /// terminal, say), and an app's own profile isn't skipped. Without profiles on either side, the text goes where it
    /// always has.
    /// </summary>
    public bool Suit(string? app, IEnumerable<AppProfile> profiles) => AppProfile.Matching(app, profiles)?.Id == ProfileId;

    /// <summary>
    /// Whether transcripts saved with a recording can stand in for this run's: only when it's cut into the same chunks
    /// (<see cref="Chunker.Layout"/>) and transcribed with the same language hint, which a profile or the global setting
    /// may have changed since. A record from before the hint was saved (null) is taken to have auto-detected, the default,
    /// so only a run that auto-detects reuses its transcripts; any other sends its chunks again.
    /// </summary>
    public bool ReusesTranscripts(int? layout, string? savedLanguage) => layout == Chunker.Layout && (savedLanguage ?? "") == Language;

    /// <summary>
    /// The global preferences, then the app's on a line of their own; the prompt shows both as the speaker's own
    /// preferences, after the tuned instructions, never in their place.
    /// </summary>
    internal static string Combine(string global, string app) =>
        string.Join("\n", new[] { global, app }.Select(s => s.Trim()).Where(s => s.Length > 0));
}
