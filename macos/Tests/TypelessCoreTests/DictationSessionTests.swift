import Foundation
import Testing
@testable import TypelessCore

/// What each key, click and system event does to a dictation: the decisions the app's SessionController carries out.
@Suite struct DictationSessionTests {
    /// testdata/session-cases.json, shared with the Windows tests so both apps decide the same way.
    static func cases() throws -> [String: Any] {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/session-cases.json"))
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    /// One effect of every kind, by the name the cases know it by.
    private static let everyEffect: [SessionEffect] = [.startRecording, .finishRecording]
        + RecordingMode.allCases.filter { $0 != .dictation }.map(SessionEffect.switchMode)
        + [.cancel(silently: false), .cancel(silently: true), .keepCancelled, .pasteLastDropped]
        + ShortcutAction.allCases.map(SessionEffect.perform)

    /// Runs one step, and returns what was wrong with it (nothing, when it went as the case says).
    private func run(_ step: [String: Any], on session: inout DictationSession) throws -> [String] {
        var problems: [String] = []
        let name = step["do"] as! String
        func time() -> TimeInterval { (step["at"] as! NSNumber).doubleValue }
        func seconds() -> Double { (step["seconds"] as! NSNumber).doubleValue }
        func mode() throws -> RecordingMode { try #require(RecordingMode(rawValue: step["mode"] as! String)) }

        var effects: [SessionEffect]?
        var returned: Any?
        switch name {
        case "hotkeyDown": effects = session.hotkeyDown()
        case "hotkeyUp": effects = session.hotkeyUp(at: time())
        case "otherKey": effects = session.otherKey((step["key"] as! NSNumber).int64Value, at: time())
        case "finishRequested": effects = session.finishRequested()
        case "sendKeyDown": effects = session.sendKeyDown(try #require(SendKey(rawValue: step["sendKey"] as! String)))
        case "escape": effects = session.escape(recordedSeconds: seconds())
        case "locked": effects = session.locked(recordedSeconds: seconds())
        case "action": effects = session.actionPressed(try #require(ShortcutAction(rawValue: step["action"] as! String)))
        case "selectionRefused": effects = session.selectionRefused(generation: step["generation"] as! Int)
        case "unlocked": returned = session.unlocked()
        case "beginPasteLast": returned = session.beginPasteLast() as Any? ?? NSNull()
        case "endPasteLast": session.endPasteLast(step["id"] as! Int)
        case "began": session.began(pressedAt: time())
        case "switched": session.switched(to: try mode())
        case "processing": session.processing()
        case "retrying": session.retrying(paste: step["paste"] as! Bool, mode: try mode())
        case "deliverToClipboard":
            session.deliverToClipboard(because: try #require(ScratchpadCapture.Outcome(rawValue: step["reason"] as! String)))
        case "ended": session.ended()
        default: problems.append("unknown step \(name)")
        }

        if let effects {
            // Every deciding step says what it expects, even when that's nothing.
            let expected = step["effects"] as? [String]
            if expected == nil { problems.append("no \"effects\"") }
            if let expected, effects.map(\.name) != expected { problems.append("effects \(effects.map(\.name))") }
        } else if step["effects"] != nil {
            problems.append("\"effects\" on a step that decides nothing")
        }
        if let expected = step["returns"] {
            let matches: Bool
            switch (expected, returned) {
            case (is NSNull, is NSNull): matches = true
            case let (expected as NSNumber, returned as Bool): matches = expected.boolValue == returned
            case let (expected as NSNumber, returned as Int): matches = expected.intValue == returned
            default: matches = false
            }
            if !matches { problems.append("returned \(String(describing: returned))") }
        } else if returned != nil {
            problems.append("no \"returns\"")
        }

        for (key, value) in step["then"] as? [String: Any] ?? [:] {
            let actual: String
            let wanted: String
            switch key {
            case "phase": (actual, wanted) = (session.phase.rawValue, value as! String)
            case "mode": (actual, wanted) = (session.mode.rawValue, value as! String)
            case "sendKey": (actual, wanted) = (session.sendKey.rawValue, value as! String)
            case "noPasteReason": (actual, wanted) = (session.noPasteReason.rawValue, value as! String)
            case "generation": (actual, wanted) = ("\(session.generation)", "\(value as! Int)")
            case "handsFree": (actual, wanted) = ("\(session.handsFree)", "\(value as! Bool)")
            case "locked": (actual, wanted) = ("\(session.isLocked)", "\(value as! Bool)")
            case "byPaste": (actual, wanted) = ("\(session.deliversByPaste)", "\(value as! Bool)")
            case "pasteLast":
                (actual, wanted) = (session.pendingPasteLast.map(String.init) ?? "none", (value as? Int).map(String.init) ?? "none")
            case "isCurrent":
                let asked = (value as! [String: Bool]).sorted { $0.key < $1.key }
                (actual, wanted) = ("\(asked.map { session.isCurrent(Int($0.key)!) })", "\(asked.map(\.value))")
            case "pasteLastIsCurrent":
                let asked = (value as! [String: Bool]).sorted { $0.key < $1.key }
                (actual, wanted) = ("\(asked.map { session.pasteLastIsCurrent(Int($0.key)!) })", "\(asked.map(\.value))")
            default: (actual, wanted) = ("?", "unknown check \(key)")
            }
            if actual != wanted { problems.append("\(key) is \(actual), not \(wanted)") }
        }
        return problems
    }

    @Test func sessionsFollowSharedCases() throws {
        let cases = try Self.cases()["cases"] as! [[String: Any]]
        #expect(cases.count >= 30)
        for item in cases {
            let name = item["name"] as! String
            var session = DictationSession()
            for (key, mode) in item["chords"] as? [String: String] ?? [:] {
                session.chords.register(try #require(Int64(key)), mode: try #require(RecordingMode(rawValue: mode)))
            }
            for (index, step) in (item["steps"] as! [[String: Any]]).enumerated() {
                let problems = try run(step, on: &session)
                #expect(problems.isEmpty, "\(name), step \(index + 1) (\(step["do"] as! String)): \(problems)")
            }
        }
    }

    /// The cases name every phase, mode and effect as both apps do, and try every effect at least once.
    @Test func theCasesCoverEveryNameAndEffect() throws {
        let cases = try Self.cases()
        #expect(cases["phases"] as! [String] == DictationSession.Phase.allCases.map(\.rawValue))
        #expect(cases["modes"] as! [String] == RecordingMode.allCases.map(\.rawValue))
        #expect(cases["effects"] as! [String] == Self.everyEffect.map(\.name))
        let seen = Set((cases["cases"] as! [[String: Any]]).flatMap { $0["steps"] as! [[String: Any]] }
            .flatMap { $0["effects"] as? [String] ?? [] })
        #expect(seen == Set(Self.everyEffect.map(\.name)))
    }

    @Test func anAccidentalTapFollowsSharedCases() throws {
        let cases = try Self.cases()
        #expect((cases["accidentalSeconds"] as! NSNumber).doubleValue == DictationSession.accidentalSeconds)
        for item in cases["accidental"] as! [[String: Any]] {
            #expect(DictationSession.isAccidental(recordedSeconds: (item["seconds"] as! NSNumber).doubleValue)
                    == item["accidental"] as! Bool, "\(item)")
        }
    }

    /// A new session is between dictations, pastes, and knows no chords.
    @Test func aNewSessionIsIdle() {
        let session = DictationSession()
        #expect(session.phase == .idle)
        #expect(session.generation == 0)
        #expect(session.pressedAt == nil)
        #expect(!session.handsFree && !session.isLocked && session.deliversByPaste)
        #expect(session.mode == .dictation && session.sendKey == .off)
        #expect(session.pendingPasteLast == nil)
        #expect(session.chords.isEmpty)
    }

    /// The press's own time decides tap or hold, whenever the app got round to reporting that the microphone opened.
    @Test func thePressTimeIsTheKeyEventsOwn() {
        var session = DictationSession()
        _ = session.hotkeyDown()
        // A Bluetooth microphone took a second to open: the key, pressed at 100, was let go of at 100.2 meanwhile.
        session.began(pressedAt: 100)
        #expect(session.pressedAt == 100)
        #expect(session.hotkeyUp(at: 100.2).isEmpty)
        #expect(session.handsFree)
        session.ended()
        #expect(session.pressedAt == nil)
    }
}
