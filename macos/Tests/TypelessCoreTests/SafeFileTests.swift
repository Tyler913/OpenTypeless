import Foundation
import Testing
@testable import TypelessCore

/// The app's own files survive damage: one that can't be read is moved aside, not saved over.
@Suite struct SafeFileTests {
    private func temporaryFolder() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("safefile-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    @Test func movesADamagedFileAsideWithTheTime() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("usage.json")
        let date = Calendar.current.date(from: DateComponents(year: 2026, month: 10, day: 11, hour: 14, minute: 30, second: 5))!
        try Data("{broken".utf8).write(to: file)
        let aside = try #require(SafeFile.moveAside(file, now: date))
        #expect(aside.lastPathComponent == "usage.json.corrupt-20261011-143005")
        #expect(!FileManager.default.fileExists(atPath: file.path))
        #expect(try String(contentsOf: aside, encoding: .utf8) == "{broken")
        // A second one in the same second doesn't overwrite the first.
        try Data("{again".utf8).write(to: file)
        #expect(SafeFile.moveAside(file, now: date)?.lastPathComponent == "usage.json.corrupt-20261011-143005-2")
        #expect(SafeFile.moveAside(file, now: date) == nil) // nothing left to move
    }

    @Test func damagedLedgerIsKeptAsideNotOverwritten() throws {
        let folder = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("usage.json")
        try Data(count: 64).write(to: file) // what a power cut mid-write can leave
        let ledger = try #require(UsageLedger.load(from: file))
        #expect(ledger.isEmpty)
        let names = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        #expect(names.count == 1 && names[0].hasPrefix("usage.json.corrupt-"))
        ledger.save(to: file)
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).count == 2)
    }
}
