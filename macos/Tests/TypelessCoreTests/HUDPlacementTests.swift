import Foundation
import Testing
@testable import TypelessCore

@Suite struct HUDPlacementTests {
    private struct Metrics: Decodable {
        let inset: Double
        let gap: Double
        let margin: Double
    }

    private struct Expected: Decodable {
        let centerX: Double
        let edgeY: Double
        let growsDown: Bool
    }

    private struct AnchorCase: Decodable {
        let id: String
        let position: String
        let area: [Double]
        let extent: [Double]
        let metrics: String
        let caret: [Double]?
        let expect: Expected
    }

    private struct ScreenCase: Decodable {
        let id: String
        let screens: [[Double]]
        let caret: [Double]?
        let window: [Double]?
        let mouse: [Double]?
        let expect: Int?
    }

    private struct SavedCase: Decodable {
        let saved: String?
        let expect: String
    }

    private struct CaseFile: Decodable {
        let metrics: [String: Metrics]
        let anchor: [AnchorCase]
        let screen: [ScreenCase]
        let saved: [SavedCase]
    }

    /// testdata/hud-placement-cases.json, shared with the Windows tests so both apps place the capsule the same way.
    private static func cases() throws -> CaseFile {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("testdata/hud-placement-cases.json"))
        return try JSONDecoder().decode(CaseFile.self, from: data)
    }

    private static func rect(_ values: [Double]?) -> HUDRect? {
        guard let values, values.count == 4 else { return nil }
        return HUDRect(x: values[0], y: values[1], width: values[2], height: values[3])
    }

    @Test func sharedAnchorCases() throws {
        let file = try Self.cases()
        #expect(file.anchor.count >= 15)
        for c in file.anchor {
            let metrics = try #require(file.metrics[c.metrics], "\(c.id)")
            let area = try #require(Self.rect(c.area), "\(c.id)")
            let anchor = HUDPlacement.anchor(
                HUDPosition(saved: c.position), area: area, extent: HUDSize(width: c.extent[0], height: c.extent[1]),
                caret: Self.rect(c.caret), metrics: .init(inset: metrics.inset, gap: metrics.gap, margin: metrics.margin))
            let expected = HUDAnchor(centerX: c.expect.centerX, edgeY: c.expect.edgeY, growsDown: c.expect.growsDown)
            #expect(anchor == expected, "\(c.id)")
        }
    }

    @Test func sharedScreenCases() throws {
        let file = try Self.cases()
        #expect(file.screen.count >= 10)
        for c in file.screen {
            let screens = c.screens.compactMap { Self.rect($0) }
            let mouse = c.mouse.map { HUDPoint(x: $0[0], y: $0[1]) }
            let index = HUDPlacement.screen(screens, caret: Self.rect(c.caret), window: Self.rect(c.window), mouse: mouse)
            #expect(index == c.expect, "\(c.id)")
        }
    }

    @Test func sharedSavedValues() throws {
        for c in try Self.cases().saved {
            #expect(HUDPosition(saved: c.saved).rawValue == c.expect, "\(c.saved ?? "nil")")
        }
    }

    @Test func everyPositionKeepsItsName() {
        #expect(HUDPosition.allCases.map(\.rawValue) == ["bottom", "top", "caret"])
        for position in HUDPosition.allCases { #expect(HUDPosition(saved: position.rawValue) == position) }
    }

    @Test func theFrameGrowsAwayFromTheEdgeThatStaysPut() {
        #expect(HUDAnchor(centerX: 100, edgeY: 50, growsDown: true).frame(width: 40, height: 20)
            == HUDRect(x: 80, y: 50, width: 40, height: 20))
        #expect(HUDAnchor(centerX: 100, edgeY: 50, growsDown: false).frame(width: 40, height: 20)
            == HUDRect(x: 80, y: 30, width: 40, height: 20))
        // Taller (the preview, a notice that wraps): the edge stays where it was.
        #expect(HUDAnchor(centerX: 100, edgeY: 50, growsDown: true).frame(width: 40, height: 70).minY == 50)
        #expect(HUDAnchor(centerX: 100, edgeY: 50, growsDown: false).frame(width: 40, height: 70).maxY == 50)
    }

    /// Wherever the cursor is on the screen, the capsule at its largest stays on it and off the line typed into.
    @Test func nearTheCursorItNeverCoversTheLineAndStaysOnScreen() {
        let area = HUDRect(x: -1280, y: 40, width: 1280, height: 984)
        let extent = HUDSize(width: 420, height: 80)
        let metrics = HUDPlacement.Metrics(inset: 28, gap: 8, margin: 8)
        for y in stride(from: area.minY, to: area.maxY - 20, by: 7) {
            for x in stride(from: area.minX, to: area.maxX, by: 53) {
                let line = HUDRect(x: x, y: y, width: 2, height: 20)
                let anchor = HUDPlacement.anchor(.caret, area: area, extent: extent, caret: line, metrics: metrics)
                let frame = anchor.frame(width: extent.width, height: extent.height)
                #expect(frame.minX >= area.minX && frame.maxX <= area.maxX && frame.minY >= area.minY
                        && frame.maxY <= area.maxY, "\(line) → \(frame)")
                #expect(frame.maxY <= line.minY || frame.minY >= line.maxY, "\(line) → \(frame)")
            }
        }
    }
}
