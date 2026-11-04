import Foundation
import Testing
@testable import TypelessCore

/// The monthly spending limit's reminders: when they're due, once a month each, and how what was shown is saved.
@Suite struct SpendLimitTests {
    /// testdata/spend-limit-cases.json, shared with the Windows tests so both apps remind at the same points.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/spend-limit-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    /// The cases' amounts as written, negative ones included (RequestUsage.number would turn those away).
    private func number(_ value: Any?) -> Double { (value as! NSNumber).doubleValue }

    private func level(_ value: Any?) -> SpendLimit.Level? { (value as? String).flatMap(SpendLimit.Level.init(name:)) }

    @Test func levelsMatchSharedCases() throws {
        for item in try Self.cases()["levels"] as! [[String: Any]] {
            let spent = number(item["spent"]), limit = number(item["limit"])
            #expect(SpendLimit.level(spent: spent, limit: limit) == level(item["level"]), "\(item)")
            #expect(abs(SpendLimit.progress(spent: spent, limit: limit) - number(item["progress"])) < 1e-9, "\(item)")
            #expect(SpendLimit.percent(spent: spent, limit: limit) == item["percent"] as! Int, "\(item)")
        }
    }

    @Test func monthKeysMatchSharedCases() throws {
        for item in try Self.cases()["monthKeys"] as! [[String: Any]] {
            let day = try #require(CalendarDay(key: item["date"] as! String))
            #expect(SpendLimit.monthKey(day) == item["month"] as! String, "\(item)")
        }
    }

    @Test func storedStatesMatchSharedCases() throws {
        for item in try Self.cases()["stored"] as! [[String: Any]] {
            let state = SpendLimit.State(stored: item["stored"] as! String)
            #expect(state.month == item["month"] as! String, "\(item)")
            #expect(state.reminded == level(item["reminded"]), "\(item)")
            #expect(state.stored == item["roundTrip"] as! String, "\(item)")
        }
    }

    @Test func checksMatchSharedCases() throws {
        for item in try Self.cases()["checks"] as! [[String: Any]] {
            let outcome = SpendLimit.check(spentBefore: number(item["spentBefore"]), spent: number(item["spent"]),
                                           limit: number(item["limit"]), month: item["month"] as! String,
                                           state: SpendLimit.State(stored: item["state"] as! String))
            let name = item["name"] as! String
            #expect(outcome.reminder == level(item["reminder"]), "\(name)")
            #expect(outcome.state.stored == item["after"] as! String, "\(name)")
        }
    }

    @Test func sequencesMatchSharedCases() throws {
        for sequence in try Self.cases()["sequences"] as! [[String: Any]] {
            let name = sequence["name"] as! String
            var state = SpendLimit.State(stored: "")
            for (index, step) in (sequence["steps"] as! [[String: Any]]).enumerated() {
                // Through the saved form, as the app keeps it between dictations.
                let outcome = SpendLimit.check(spentBefore: number(step["spentBefore"]), spent: number(step["spent"]),
                                               limit: number(step["limit"]), month: step["month"] as! String, state: state)
                #expect(outcome.reminder == level(step["reminder"]), "\(name), step \(index + 1)")
                state = SpendLimit.State(stored: outcome.state.stored)
            }
        }
    }

    @Test func eachReminderComesOnceAMonthHoweverTheSpendGrows() {
        var state = SpendLimit.State()
        var shown: [SpendLimit.Level] = []
        var spent = 0.0
        // A hundred small dictations of a cent each against a $0.50 limit.
        for _ in 0..<100 {
            let before = spent
            spent += 0.01
            let outcome = SpendLimit.check(spentBefore: before, spent: spent, limit: 0.5, month: "2026-11", state: state)
            if let reminder = outcome.reminder { shown.append(reminder) }
            state = outcome.state
        }
        #expect(shown == [.warning, .reached])
        #expect(state == SpendLimit.State(month: "2026-11", reminded: .reached))
    }

    @Test func levelsAreOrdered() {
        #expect(SpendLimit.Level.none < .warning && SpendLimit.Level.warning < .reached)
        for level in [SpendLimit.Level.none, .warning, .reached] {
            #expect(SpendLimit.Level(name: level.name) == level)
        }
    }
}
