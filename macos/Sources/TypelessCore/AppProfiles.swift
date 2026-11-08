import Foundation

/// Settings for one app. When a dictation starts with that app in front, its overrides apply to the whole dictation:
/// the speech-to-text language, whether and how it's cleaned up, and whether Return is pressed after the paste. What a
/// profile leaves unset follows the global settings. Like VoiceInk's modes and Superwhisper's auto-switching, but matched
/// by app only: reading a browser's address needs Automation permission and can hang. Nothing about the app (its name,
/// its window title) goes to the model; only the instructions the user wrote do.
public struct AppProfile: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    /// The app it's for: a bundle ID on macOS ("com.tinyspeck.slackmacgap"), an executable name on Windows ("slack.exe").
    /// Compared as `matchKey` does.
    public var app: String
    /// The app's name, shown in Settings and History.
    public var name: String
    /// Clean-up preferences for this app, added after the global ones; "" adds none.
    public var instructions: String
    /// How heavily the text is cleaned up, `.off` for not at all; nil follows the recommended settings for the app, if
    /// any, else the global setting (see `DictationSettings.applying`).
    public var strength: PolishStrength?
    /// Press Return (Enter on Windows) after the paste; nil follows the global setting.
    public var pressEnter: Bool?
    /// Spoken-language hint for speech-to-text, "" to auto-detect; nil follows the global setting.
    public var language: String?
    /// How much structure the cleaned-up text gets; nil follows the app's built-in layout (see `TextLayout.builtIn`).
    public var layout: TextLayout?

    public init(id: String = UUID().uuidString, app: String, name: String, instructions: String = "",
                strength: PolishStrength? = nil, pressEnter: Bool? = nil, language: String? = nil, layout: TextLayout? = nil) {
        self.id = id
        self.app = app
        self.name = name
        self.instructions = instructions
        self.strength = strength
        self.pressEnter = pressEnter
        self.language = language
        self.layout = layout
    }

    enum CodingKeys: String, CodingKey {
        case id, app, name, instructions, polish, strength, pressEnter, language, layout
    }

    /// Lenient, so a profile saved by another version still loads: a field that's missing or of the wrong type counts
    /// as unset rather than losing every profile. A missing ID is left empty for `withUniqueIDs` to fill in once.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = ((try? c.decodeIfPresent(String.self, forKey: .id)) ?? nil) ?? ""
        app = ((try? c.decodeIfPresent(String.self, forKey: .app)) ?? nil) ?? ""
        name = ((try? c.decodeIfPresent(String.self, forKey: .name)) ?? nil) ?? app
        instructions = ((try? c.decodeIfPresent(String.self, forKey: .instructions)) ?? nil) ?? ""
        // Saved by an older version as on or off, which `strength` refines (see `PolishStrength.stored`).
        strength = PolishStrength.stored(enabled: (try? c.decodeIfPresent(Bool.self, forKey: .polish)) ?? nil,
                                         level: (try? c.decodeIfPresent(String.self, forKey: .strength)) ?? nil)
        pressEnter = (try? c.decodeIfPresent(Bool.self, forKey: .pressEnter)) ?? nil
        language = (try? c.decodeIfPresent(String.self, forKey: .language)) ?? nil
        // One this version doesn't know counts as unset too.
        layout = ((try? c.decodeIfPresent(String.self, forKey: .layout)) ?? nil).flatMap(TextLayout.init(rawValue:))
    }

    /// Unset overrides aren't written. The strength goes with the on/off switch older versions read, so they still clean
    /// up, or don't, as it says.
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(app, forKey: .app)
        try c.encode(name, forKey: .name)
        try c.encode(instructions, forKey: .instructions)
        if let strength {
            try c.encode(strength != .off, forKey: .polish)
            try c.encode(strength.rawValue, forKey: .strength)
        }
        try c.encodeIfPresent(pressEnter, forKey: .pressEnter)
        try c.encodeIfPresent(language, forKey: .language)
        try c.encodeIfPresent(layout, forKey: .layout)
    }

    /// How app identifiers compare: case-insensitively (bundle IDs are, and so are Windows file names), ignoring the
    /// whitespace around them and a trailing ".exe", so "Slack.exe", "slack.EXE" and "slack" are the same app.
    public static func matchKey(_ app: String) -> String {
        var key = app.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if key.hasSuffix(".exe") { key.removeLast(4) }
        return key
    }

    /// Whether this profile is for `app`. A profile with no app matches nothing.
    public func matches(_ app: String?) -> Bool {
        guard let app else { return false }
        let key = Self.matchKey(self.app)
        return !key.isEmpty && key == Self.matchKey(app)
    }

    /// The profile for `app`: the first one that matches, nil when none does or the app isn't known.
    public static func matching(_ app: String?, in profiles: [AppProfile]) -> AppProfile? {
        profiles.first { $0.matches(app) }
    }

    /// Whether every setting follows the global one.
    public var isEmpty: Bool {
        strength == nil && pressEnter == nil && language == nil && layout == nil
            && instructions.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// A saved list. Whatever in it isn't a profile is skipped rather than losing the others (and having the next edit
    /// save over them); nil when it isn't a list at all.
    public static func list(from data: Data) -> [AppProfile]? {
        (try? JSONDecoder().decode([Lenient].self, from: data))?.compactMap(\.profile)
    }

    /// One entry of a saved list: a profile, or nothing when it isn't one.
    private struct Lenient: Decodable {
        let profile: AppProfile?
        init(from decoder: Decoder) throws { profile = try? AppProfile(from: decoder) }
    }

    /// The profiles with an ID each can be found by again (by an edit, Remove, or a retry from History): one saved
    /// without an ID, or with another's, gets a new one. Done once when they're loaded, and saved, so the IDs stay put.
    public static func withUniqueIDs(_ profiles: [AppProfile]) -> [AppProfile] {
        var seen = Set<String>()
        return profiles.map { profile in
            var profile = profile
            if profile.id.trimmingCharacters(in: .whitespaces).isEmpty || seen.contains(profile.id) { profile.id = UUID().uuidString }
            seen.insert(profile.id)
            return profile
        }
    }
}

/// What one dictation runs with: the global settings, with the overrides of the profile for the app it was spoken in.
/// Taken once, when the recording starts, and used for everything that dictation does (its speech-to-text, a clean-up
/// started ahead of time, the backup model, the Return after the paste, a retry from History), so another app coming to
/// the front before the key is released doesn't change what the text was spoken for.
public struct DictationSettings: Equatable, Sendable {
    public var polishEnabled: Bool
    public var pressEnter: Bool
    /// Spoken-language hint for speech-to-text; "" auto-detects.
    public var language: String
    /// The clean-up preferences: the global ones, then the app's.
    public var extraInstructions: String
    /// The profile applied; nil when none was (the global settings as they are).
    public var profileID: String?
    public var profileName: String?
    /// How much structure the cleaned-up text gets (see `TextLayout`).
    public var layout: TextLayout = .natural
    /// How heavily the text is cleaned up while `polishEnabled`: never `.off`, which `polishEnabled` says.
    public var strength: PolishStrength = .standard
    /// The kind of app whose recommended strength applied (see `RecommendedApps`); nil when none did.
    public var recommendation: AppCategory?

    public init(polishEnabled: Bool, pressEnter: Bool, language: String, extraInstructions: String) {
        self.polishEnabled = polishEnabled
        self.pressEnter = pressEnter
        self.language = language
        self.extraInstructions = extraInstructions
    }

    /// The clean-up as it runs at `strength`: on at that strength, or off.
    public func withStrength(_ strength: PolishStrength) -> DictationSettings {
        var settings = self
        settings.polishEnabled = strength != .off
        if strength != .off { settings.strength = strength }
        return settings
    }

    /// How heavily the text is cleaned up: `strength`, or `.off`.
    public var effectiveStrength: PolishStrength { polishEnabled ? strength : .off }

    /// These settings with `profile`'s overrides on top; unchanged without one. The layout is the profile's, else the
    /// built-in one of the app (`app`, or the profile's own), else as it was. `recommendation`: the kind of app it is,
    /// when the recommended settings are on (see `RecommendedApps`), whose strength applies unless the profile sets one;
    /// it only picks how heavily, never whether, so with clean-up off it changes nothing.
    public func applying(_ profile: AppProfile?, app: String? = nil, recommendation: AppCategory? = nil) -> DictationSettings {
        var settings = self
        settings.layout = profile?.layout ?? TextLayout.builtIn(for: app ?? profile?.app) ?? layout
        if let recommendation, polishEnabled, profile?.strength == nil {
            settings.strength = recommendation.strength
            settings.recommendation = recommendation
        }
        guard let profile else { return settings }
        if let strength = profile.strength { settings = settings.withStrength(strength) }
        settings.pressEnter = profile.pressEnter ?? pressEnter
        settings.language = profile.language ?? language
        settings.extraInstructions = Self.combine(extraInstructions, profile.instructions)
        settings.profileID = profile.id
        settings.profileName = profile.name
        return settings
    }

    /// Whether these are the settings `app` (the one in front when the key is released, or when the text goes in) gets:
    /// it has the same profile as the app the dictation was started in, or, like that one, none. Otherwise the text goes
    /// to the clipboard, and Return isn't pressed: one app's profile never sends the text in another (a terminal, say),
    /// and an app's own profile isn't skipped. Without profiles on either side, the text goes where it always has.
    public func suit(_ app: String?, in profiles: [AppProfile]) -> Bool {
        AppProfile.matching(app, in: profiles)?.id == profileID
    }

    /// Whether transcripts saved with a recording can stand in for this run's: only when it's cut into the same chunks
    /// (`Chunker.layout`) and transcribed with the same language hint, which a profile or the global setting may have
    /// changed since. A record from before the hint was saved (nil) is taken to have auto-detected, the default, so only
    /// a run that auto-detects reuses its transcripts; any other sends its chunks again.
    public func reusesTranscripts(layout: Int?, language savedLanguage: String?) -> Bool {
        layout == Chunker.layout && (savedLanguage ?? "") == language
    }

    /// The global preferences, then the app's on a line of their own; the prompt shows both as the speaker's own
    /// preferences, after the tuned instructions, never in their place.
    static func combine(_ global: String, _ app: String) -> String {
        [global, app].map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            .joined(separator: "\n")
    }
}
