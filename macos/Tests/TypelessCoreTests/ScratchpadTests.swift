import Foundation
import Testing
@testable import TypelessCore

/// The Scratchpad's notes: titles, order, what a dictation with nowhere to go adds, and how the file is read and written.
@Suite struct ScratchpadTests {
    /// testdata/scratchpad-cases.json, shared with the Windows tests so both apps agree.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/scratchpad-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    private func date(_ text: String) -> Date { Scratchpad.parseDate(text)! }

    @Test func titlesFollowSharedCases() throws {
        for item in try Self.cases()["titles"] as! [[String: Any]] {
            #expect(Scratchpad.title(of: item["text"] as! String) == item["title"] as! String, "\(item)")
        }
    }

    @Test func appendingFollowsSharedCases() throws {
        for item in try Self.cases()["appending"] as! [[String: Any]] {
            #expect(Scratchpad.appending(item["addition"] as! String, to: item["existing"] as! String) == item["expected"] as! String,
                    "\(item)")
        }
    }

    @Test func captureDecisionFollowsSharedCases() throws {
        let cases = try Self.cases()["capture"] as! [[String: Any]]
        // Every outcome is covered, by its name on both platforms.
        #expect(Set(cases.map { $0["outcome"] as! String }) == Set(ScratchpadCapture.Outcome.allCases.map(\.rawValue)))
        for item in cases {
            let outcome = try #require(ScratchpadCapture.Outcome(rawValue: item["outcome"] as! String))
            #expect(ScratchpadCapture.saves(item["text"] as! String, after: outcome, enabled: item["enabled"] as! Bool)
                    == item["saves"] as! Bool, "\(item)")
        }
    }

    @Test func filesAreReadAsSharedCasesSay() throws {
        for item in try Self.cases()["files"] as! [[String: Any]] {
            let name = item["name"] as! String
            let now = date(item["now"] as! String)
            let decoded = Scratchpad.decode(Data((item["json"] as! String).utf8), now: now)
            guard let listed = item["listed"] as? [[String: Any]] else {
                #expect(decoded == nil, "\(name)")
                continue
            }
            guard var notes = decoded else {
                Issue.record("\(name): not read")
                continue
            }
            notes.purge(now: now)
            #expect(notes.entries.count == item["entries"] as! Int, "\(name)")
            #expect(Set(notes.entries.map(\.id)).count == notes.entries.count, "\(name): IDs are unique")
            #expect(notes.notes.count == listed.count, "\(name)")
            for (note, expected) in zip(notes.notes, listed) {
                if let id = expected["id"] as? String { #expect(note.id == id, "\(name)") } else { #expect(!note.id.isEmpty, "\(name)") }
                #expect(note.text == expected["text"] as! String, "\(name)")
                #expect(note.captureDay == expected["captureDay"] as? String, "\(name)")
                if let created = expected["created"] as? String { #expect(note.created == date(created), "\(name)") }
                if let modified = expected["modified"] as? String { #expect(note.modified == date(modified), "\(name)") }
            }
        }
    }

    @Test func roundTripsThroughTheFile() throws {
        let now = date("2026-11-07T09:30:00Z")
        var notes = Scratchpad()
        let first = notes.add(now: now, id: "first")
        notes.setText("Groceries\nmilk", of: first.id, now: now.addingTimeInterval(5))
        let second = notes.add(now: now.addingTimeInterval(10), id: "second")
        notes.setText("Call Sam — 会议", of: second.id, now: now.addingTimeInterval(10))
        notes.capture("Heard while nothing had focus.", day: "2026-11-07", now: now.addingTimeInterval(20), newID: "captured")
        notes.delete(first.id, now: now.addingTimeInterval(30))
        notes.add(now: now.addingTimeInterval(40), id: "blank") // never typed in: not saved

        let data = try #require(notes.jsonData())
        let loaded = try #require(Scratchpad.decode(data, now: now))
        #expect(loaded.entries == notes.entries.filter { $0.id != "blank" })
        #expect(loaded.notes.map(\.id) == ["captured", "second"])
        #expect(loaded.note("first")?.deleted == now.addingTimeInterval(30))
        #expect(loaded.note("captured")?.captureDay == "2026-11-07")
    }

    @Test func editsMoveANoteUpOnlyWhenTheTextChanges() {
        let now = date("2026-11-07T09:30:00Z")
        var notes = Scratchpad()
        notes.add(now: now, id: "a")
        notes.setText("A", of: "a", now: now)
        notes.add(now: now.addingTimeInterval(60), id: "b")
        notes.setText("B", of: "b", now: now.addingTimeInterval(60))
        #expect(notes.notes.map(\.id) == ["b", "a"])
        notes.setText("A", of: "a", now: now.addingTimeInterval(120)) // the same text: not an edit
        #expect(notes.notes.map(\.id) == ["b", "a"])
        notes.setText("A again", of: "a", now: now.addingTimeInterval(120.7))
        #expect(notes.notes.map(\.id) == ["a", "b"])
        #expect(notes.note("a")?.modified == now.addingTimeInterval(120)) // whole seconds, as the file keeps them
        #expect(notes.note("a")?.created == now)
    }

    @Test func dictationsWithNowhereToGoShareTheDaysNote() {
        let now = date("2026-11-07T09:30:00Z")
        var notes = Scratchpad()
        let first = notes.capture("One.", day: "2026-11-07", now: now, newID: "day1")
        let second = notes.capture("Two.", day: "2026-11-07", now: now.addingTimeInterval(60), newID: "unused")
        #expect(first == "day1" && second == "day1")
        #expect(notes.note("day1")?.text == "One.\n\nTwo.")
        #expect(notes.note("day1")?.modified == now.addingTimeInterval(60))
        // The next day starts a note of its own.
        #expect(notes.capture("Three.", day: "2026-11-08", now: now.addingTimeInterval(86_400), newID: "day2") == "day2")
        // A deleted day's note isn't added to: a new one takes its place, and Undo brings back the old one as it was.
        notes.delete("day2", now: now.addingTimeInterval(86_500))
        #expect(notes.capture("Four.", day: "2026-11-08", now: now.addingTimeInterval(86_600), newID: "day2b") == "day2b")
        notes.restore("day2")
        #expect(notes.notes.map(\.id) == ["day2b", "day2", "day1"])
        #expect(notes.note("day2")?.text == "Three.")
    }

    @Test func blankNotesAreDroppedButTheOneBeingEdited() {
        let now = date("2026-11-07T09:30:00Z")
        var notes = Scratchpad()
        notes.add(now: now, id: "kept")
        notes.setText("Text", of: "kept", now: now)
        notes.add(now: now, id: "blank")
        notes.add(now: now, id: "editing")
        notes.setText(" \n", of: "blank", now: now)
        notes.removeBlank(except: "editing")
        #expect(notes.entries.map(\.id) == ["kept", "editing"])
        notes.removeBlank()
        #expect(notes.entries.map(\.id) == ["kept"])
    }

    private func temporaryFolder() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("scratchpad-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    @Test func aDamagedFileIsMovedAsideNotOverwritten() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("scratchpad.json")
        #expect(Scratchpad.load(from: file).entries.isEmpty) // no file yet
        try Data(count: 64).write(to: file) // what a power cut mid-write can leave
        var notes = Scratchpad.load(from: file)
        #expect(notes.entries.isEmpty && !notes.isUnreadable)
        let names = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        #expect(names.count == 1 && names[0].hasPrefix("scratchpad.json.corrupt-"))
        notes.capture("Kept.", day: "2026-11-07")
        #expect(notes.save(to: file))
        #expect(Scratchpad.load(from: file).notes.map(\.text) == ["Kept."])
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).count == 2)
    }

    @Test func anUnreadableFileIsNeverSavedOver() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("scratchpad.json")
        let original = Data(#"{"notes":[{"id":"a","text":"Mine"}]}"#.utf8)
        try original.write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: file.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path) }
        var notes = Scratchpad.load(from: file)
        #expect(notes.isUnreadable)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        notes.capture("Would replace it.", day: "2026-11-07")
        #expect(!notes.save(to: file))
        #expect(try Data(contentsOf: file) == original)
    }
}
