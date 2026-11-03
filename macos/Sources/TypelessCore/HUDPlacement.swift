import Foundation

/// Where the recording capsule shows while you dictate (General → Capsule position). Saved by its raw value, the same
/// on both platforms.
public enum HUDPosition: String, CaseIterable, Sendable {
    /// Centred near the bottom of the screen, above the Dock or the taskbar: where it always was, and the default.
    case bottom
    /// Centred near the top of the screen, below the menu bar; the live preview and notices open downwards.
    case top
    /// Just below the line being typed into, or above it when there's no room below, where Accessibility / UI
    /// Automation says where that is; at the bottom of the screen where it doesn't.
    case caret

    /// The saved choice; the bottom for none, or for one this version can't read (saved by a newer one, or damaged).
    public init(saved: String?) {
        self = saved.flatMap(HUDPosition.init(rawValue:)) ?? .bottom
    }

    public var title: String {
        switch self {
        case .bottom: return L("屏幕底部", "Bottom of the screen")
        case .top: return L("屏幕顶部", "Top of the screen")
        case .caret: return L("光标附近", "Near the text cursor")
        }
    }
}

/// A rectangle on the screens with y growing downwards from the top of the main screen, as Accessibility and Windows
/// give them: points on macOS (which flips AppKit's frames into it), physical pixels on Windows.
public struct HUDRect: Equatable, Sendable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    public var minX: Double { x }
    public var maxX: Double { x + width }
    public var minY: Double { y }
    public var maxY: Double { y + height }
    public var midX: Double { x + width / 2 }
    public var midY: Double { y + height / 2 }

    /// The area the two have in common; 0 when they don't overlap.
    public func overlap(_ other: HUDRect) -> Double {
        max(0, min(maxX, other.maxX) - max(minX, other.minX)) * max(0, min(maxY, other.maxY) - max(minY, other.minY))
    }

    /// Whether the point is in it: its left and top edges are, its right and bottom edges belong to the next one.
    public func contains(_ point: HUDPoint) -> Bool {
        minX <= point.x && point.x < maxX && minY <= point.y && point.y < maxY
    }
}

public struct HUDPoint: Equatable, Sendable {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }
}

public struct HUDSize: Equatable, Sendable {
    public var width: Double
    public var height: Double

    public init(width: Double, height: Double) {
        self.width = width
        self.height = height
    }
}

/// Where the capsule goes: centred on `centerX`, with one edge on `edgeY`, growing away from it. A capsule at the top
/// (or below the text cursor) hangs from its top edge, so the live preview and a notice that wraps open downwards; one
/// at the bottom (or above the cursor) stands on its bottom edge and they open upwards. Either way the edge nearest the
/// screen's edge, or the line being typed into, stays put.
public struct HUDAnchor: Equatable, Sendable {
    public var centerX: Double
    public var edgeY: Double
    /// The capsule's top edge is on `edgeY` and it grows downwards, the live preview below it; else its bottom edge is,
    /// and it grows upwards, the preview above it.
    public var growsDown: Bool

    public init(centerX: Double, edgeY: Double, growsDown: Bool) {
        self.centerX = centerX
        self.edgeY = edgeY
        self.growsDown = growsDown
    }

    /// The capsule's frame at its size now.
    public func frame(width: Double, height: Double) -> HUDRect {
        HUDRect(x: centerX - width / 2, y: growsDown ? edgeY : edgeY - height, width: width, height: height)
    }
}

/// Where the recording capsule goes on the screen: which screen, and where on it. Pure arithmetic, the same on both
/// platforms (testdata/hud-placement-cases.json); each app measures the screens, the capsule and the text cursor
/// itself.
public enum HUDPlacement {
    /// The platform's distances, in the units of the rectangles.
    public struct Metrics: Equatable, Sendable {
        /// From the edge of the screen's visible frame (below the menu bar, above the Dock or taskbar) to the capsule, at
        /// the top or the bottom.
        public var inset: Double
        /// Between the line being typed into and the capsule.
        public var gap: Double
        /// Kept clear inside the visible frame on every side, near the text cursor.
        public var margin: Double

        public init(inset: Double, gap: Double, margin: Double) {
            self.inset = inset
            self.gap = gap
            self.margin = margin
        }
    }

    /// The anchor for `position` on the screen whose visible frame is `area`.
    ///
    /// `extent`: the most room the capsule takes from its edge, with the live preview or a notice of three lines, and
    /// whatever else the platform draws round it. Near the text cursor all of it must fit on the side it goes: below
    /// `caret` (the line being typed into, the selection, or the focused field where that's all there is), else above
    /// it, so the capsule never covers it, and centred on it as far as the screen's sides allow. Without a cursor on this
    /// screen, or with no room on either side (a field as tall as the screen), it's the bottom of the screen.
    public static func anchor(_ position: HUDPosition, area: HUDRect, extent: HUDSize, caret: HUDRect?,
                              metrics: Metrics) -> HUDAnchor {
        let bottom = HUDAnchor(centerX: area.midX, edgeY: area.maxY - metrics.inset, growsDown: false)
        switch position {
        case .bottom:
            return bottom
        case .top:
            return HUDAnchor(centerX: area.midX, edgeY: area.minY + metrics.inset, growsDown: true)
        case .caret:
            guard let caret, isUsable(caret),
                  area.minX <= caret.midX, caret.midX <= area.maxX, area.minY <= caret.midY, caret.midY <= area.maxY
            else { return bottom }
            let low = area.minX + metrics.margin + extent.width / 2
            let high = area.maxX - metrics.margin - extent.width / 2
            let centerX = low <= high ? min(max(caret.midX, low), high) : area.midX
            let below = caret.maxY + metrics.gap
            if below + extent.height <= area.maxY - metrics.margin {
                return HUDAnchor(centerX: centerX, edgeY: below, growsDown: true)
            }
            let above = caret.minY - metrics.gap
            if above - extent.height >= area.minY + metrics.margin {
                return HUDAnchor(centerX: centerX, edgeY: above, growsDown: false)
            }
            return bottom
        }
    }

    /// Which of `screens` (their whole frames) the capsule goes on: the one with the text cursor in it, else the one
    /// holding most of the focused window, else the one under the mouse. nil when none of them does: the caller takes
    /// the main screen.
    public static func screen(_ screens: [HUDRect], caret: HUDRect?, window: HUDRect?, mouse: HUDPoint?) -> Int? {
        if let caret, isUsable(caret),
           let index = screens.firstIndex(where: { $0.contains(HUDPoint(x: caret.midX, y: caret.midY)) }) {
            return index
        }
        if let window, window.width > 0, window.height > 0 {
            var best: Int?
            var most = 0.0
            for (index, screen) in screens.enumerated() {
                let shared = screen.overlap(window)
                if shared > most {
                    most = shared
                    best = index
                }
            }
            if let best { return best }
        }
        if let mouse { return screens.firstIndex { $0.contains(mouse) } }
        return nil
    }

    /// Whether a rectangle read for the text cursor says anything: finite, and as tall as a line at least a little. An
    /// app that can't tell often answers with an empty rectangle instead.
    private static func isUsable(_ rect: HUDRect) -> Bool {
        rect.x.isFinite && rect.y.isFinite && rect.width.isFinite && rect.height.isFinite && rect.width >= 0
            && rect.height > 0
    }
}
