import Foundation
import Testing
@testable import TypelessCore

/// Which app profile a dictation picks up, what it changes, and how profiles are saved.
@Suite struct AppProfileTests {
    /// testdata/app-profile-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/app-profile-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    /// A profile decoded the way the settings store decodes it.
    private func decode(_ object: Any) throws -> AppProfile {
        try JSONDecoder().decode(AppProfile.self, from: JSONSerialization.data(withJSONObject: object))
    }

    @Test func matchingFollowsSharedCases() throws {
        for item in try Self.cases()["matching"] as! [[String: Any]] {
            let profiles = (item["apps"] as! [String]).enumerated().map { AppProfile(id: "\($0.offset)", app: $0.element, name: $0.element) }
            let app = item["app"] as? String
            let expected = (item["expected"] as? Int).map(String.init)
            #expect(AppProfile.matching(app, in: profiles)?.id == expected, "\(item)")
        }
    }

    @Test func resolutionFollowsSharedCases() throws {
        for item in try Self.cases()["resolution"] as! [[String: Any]] {
            let g = item["global"] as! [String: Any]
            let global = DictationSettings(polishEnabled: g["polishEnabled"] as! Bool, pressEnter: g["pressEnter"] as! Bool,
                                           language: g["language"] as! String, extraInstructions: g["extraInstructions"] as! String)
            let profile = try (item["profile"] as? [String: Any]).map(decode)
            let resolved = global.applying(profile)
            let e = item["expected"] as! [String: Any]
            let name = item["name"] as! String
            #expect(resolved.polishEnabled == e["polishEnabled"] as! Bool, "\(name)")
            #expect(resolved.pressEnter == e["pressEnter"] as! Bool, "\(name)")
            #expect(resolved.language == e["language"] as! String, "\(name)")
            #expect(resolved.extraInstructions == e["extraInstructions"] as! String, "\(name)")
            #expect(resolved.profileID == e["profileID"] as? String, "\(name)")
            #expect(resolved.profileName == e["profileName"] as? String, "\(name)")
        }
    }

    @Test func roundTripsThroughJSON() throws {
        let profiles = [
            AppProfile(id: "A", app: "com.tinyspeck.slackmacgap", name: "Slack", instructions: "Short messages\nNo lists",
                       polish: false, pressEnter: true, language: ""),
            AppProfile(id: "B", app: "com.apple.mail", name: "Mail"),
        ]
        let data = try JSONEncoder().encode(profiles)
        #expect(try JSONDecoder().decode([AppProfile].self, from: data) == profiles)
        // Unset overrides aren't written, so "follow the global setting" survives as such (and "" stays auto-detect).
        let objects = try JSONSerialization.jsonObject(with: data) as! [[String: Any]]
        #expect(objects[0]["language"] as? String == "")
        #expect(objects[0]["polish"] as? Bool == false)
        #expect(objects[1]["polish"] == nil && objects[1]["pressEnter"] == nil && objects[1]["language"] == nil)
    }

    @Test func decodesWhatOtherVersionsWrote() throws {
        // A minimal profile, and one with a field this version doesn't know.
        let profiles = try JSONDecoder().decode([AppProfile].self, from: Data(#"""
            [{"id":"X","app":"com.apple.Safari"},{"id":"Y","app":"slack.exe","name":"Slack","pressEnter":true,"url":"x.com"}]
            """#.utf8))
        #expect(profiles.count == 2)
        #expect(profiles[0] == AppProfile(id: "X", app: "com.apple.Safari", name: "com.apple.Safari"))
        #expect(profiles[0].isEmpty)
        #expect(profiles[1].pressEnter == true && profiles[1].name == "Slack" && !profiles[1].isEmpty)
        // One saved without an ID gets one, so it can still be found again by a retry.
        #expect(!(try decode(["app": "code.exe"])).id.isEmpty)
    }

    @Test func profileInstructionsReachThePromptAfterTheGlobalOnes() {
        let global = DictationSettings(polishEnabled: true, pressEnter: false, language: "", extraInstructions: "Never use bullet lists")
        let resolved = global.applying(AppProfile(app: "com.apple.mail", name: "Mail", instructions: "Use a formal tone"))
        let prompt = Prompts.polishSystemPrompt(vocabulary: [], extraInstructions: resolved.extraInstructions)
        #expect(prompt.hasPrefix(Prompts.basePolishPrompt))
        #expect(prompt.hasSuffix("## Additional preferences from the speaker\nNever use bullet lists\nUse a formal tone"))
        // The app itself is never named to the model.
        #expect(!prompt.contains("Mail") && !prompt.contains("com.apple.mail"))
    }
}
