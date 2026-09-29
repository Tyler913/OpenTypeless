import AppKit
import SwiftUI
import TypelessCore

@MainActor
final class HUDModel: ObservableObject {
    enum Phase: Equatable {
        case hidden
        case recording
        /// Transcribing and cleaning up — one state, the user doesn't need the details: hopping dots, and a bar
        /// filling the capsule as the answers come back (`bar`).
        case working
        /// Nothing to paste into, so the text went to the clipboard.
        case copied
        /// New words were learned from the user's fixes to the last dictation.
        case learned(String)
        case error(String)
    }

    @Published var phase: Phase = .hidden
    @Published var levels: [Float] = Array(repeating: 0, count: 18)
    @Published var startedAt = Date()
    /// Live preview of what's being said (empty when it's off or hasn't heard anything yet).
    @Published var preview = ""
    let bar = ProcessingBar()

    func push(level: Float) {
        // Perceptual scaling so normal speech fills the bars.
        let scaled = min(1, sqrt(level) * 3.2)
        levels.removeFirst()
        levels.append(scaled)
    }

    func resetLevels() {
        levels = Array(repeating: 0, count: levels.count)
    }
}

/// The bar that fills the capsule while a dictation is transcribed and cleaned up (see `ProcessingProgress`). Its own
/// object, so its updates on every frame redraw only the bar.
@MainActor
final class ProcessingBar: ObservableObject {
    @Published private(set) var shown: Double = 0
    private var progress = ProcessingProgress(polishes: true)
    private var timer: Timer?
    private var lastTick: TimeInterval = 0

    /// Empties the bar and starts animating it.
    func start(polishes: Bool) {
        progress = ProcessingProgress(polishes: polishes)
        shown = 0
        lastTick = ProcessInfo.processInfo.systemUptime
        guard timer == nil else { return }
        let timer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func reach(_ milestone: ProcessingProgress.Milestone) {
        progress.reach(milestone)
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    /// Sets the bar directly, for the UI snapshots.
    func show(_ value: Double) {
        shown = value
    }

    private func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        shown = progress.tick(now - lastTick)
        lastTick = now
    }
}

/// Small Liquid Glass capsule near the bottom of the screen. Never takes focus or mouse clicks, so the
/// target text field stays focused for the paste.
@MainActor
final class HUDController {
    let model = HUDModel()
    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?
    /// Room for the capsule and, above it, the live preview line.
    private static let size = NSSize(width: 460, height: 112)

    func show(_ phase: HUDModel.Phase, autoHideAfter: Double? = nil) {
        hideWork?.cancel()
        if phase != .working { model.bar.stop() }
        withAnimation(.spring(duration: 0.32, bounce: 0.18)) { model.phase = phase }
        if phase == .hidden {
            panel?.orderOut(nil)
            return
        }
        let panel = self.panel ?? makePanel()
        self.panel = panel
        if !panel.isVisible { position(panel) }
        panel.orderFrontRegardless()

        if let autoHideAfter {
            let work = DispatchWorkItem { [weak self] in self?.show(.hidden) }
            hideWork = work
            DispatchQueue.main.asyncAfter(deadline: .now() + autoHideAfter, execute: work)
        }
    }

    /// Transcribing and cleaning up, with the bar starting from empty.
    func showWorking(polishes: Bool) {
        model.bar.start(polishes: polishes)
        show(.working)
    }

    /// The text went in: the bar runs to the end, then the capsule goes.
    func finishWorking() {
        guard model.phase == .working else { return show(.hidden) }
        model.bar.reach(.delivered)
        show(.working, autoHideAfter: 0.3)
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: Self.size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        let host = NSHostingView(rootView: HUDView(model: model))
        host.frame = NSRect(origin: .zero, size: Self.size)
        panel.contentView = host
        return panel
    }

    private func position(_ panel: NSPanel) {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        guard let frame = screen?.visibleFrame else { return }
        panel.setFrameOrigin(NSPoint(x: frame.midX - Self.size.width / 2, y: frame.minY + 28))
    }
}

struct HUDView: View {
    @ObservedObject var model: HUDModel
    @Namespace private var glass

    var body: some View {
        GlassEffectContainer {
            VStack(spacing: 8) {
                if model.phase == .recording, !model.preview.isEmpty {
                    Text(model.preview)
                        .font(.system(size: 13))
                        .lineLimit(1)
                        .truncationMode(.head)
                        .frame(maxWidth: 420)
                        .padding(.horizontal, 14)
                        .frame(height: 30)
                        .glassEffect(.regular, in: .capsule)
                        .transition(.opacity)
                }
                content
                    .font(.system(size: 13, weight: .medium))
                    .padding(.horizontal, 14)
                    .frame(height: 36)
                    .background {
                        if model.phase == .working { ProgressFill(bar: model.bar).transition(.opacity) }
                    }
                    .glassEffect(.regular, in: .capsule)
                    .glassEffectID("hud", in: glass)
            }
            .animation(.easeOut(duration: 0.15), value: model.preview.isEmpty)
        }
        // The capsule stays where it always was (18 pt above the bottom of the panel); the preview grows upwards.
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
        .padding(.bottom, 18)
        .opacity(model.phase == .hidden ? 0 : 1)
    }

    @ViewBuilder private var content: some View {
        switch model.phase {
        case .hidden:
            Color.clear.frame(width: 1)
        case .recording:
            HStack(spacing: 10) {
                Circle().fill(.red).frame(width: 7, height: 7)
                LevelBars(levels: model.levels)
                TimelineView(.periodic(from: .now, by: 0.5)) { context in
                    Text(elapsed(context.date)).monospacedDigit()
                }
            }
        case .working:
            // About as wide as the recording row, so the capsule keeps its size when the key is released.
            HoppingDots().frame(width: 132)
        case .copied:
            HStack(spacing: 7) {
                Image(systemName: "doc.on.clipboard").foregroundStyle(.secondary)
                Text(L("已复制到剪贴板", "Copied to clipboard"))
            }
        case let .learned(terms):
            HStack(spacing: 7) {
                Image(systemName: "character.book.closed.fill").foregroundStyle(.tint)
                Text(L("已加入词汇表：", "Added to vocabulary: ") + terms).lineLimit(1).truncationMode(.tail)
            }
            .frame(maxWidth: 340)
        case let .error(message):
            HStack(spacing: 7) {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                Text(message).lineLimit(1).truncationMode(.tail)
            }
            .frame(maxWidth: 340)
        }
    }

    private func elapsed(_ now: Date) -> String {
        let seconds = max(0, Int(now.timeIntervalSince(model.startedAt)))
        return String(format: "%d:%02d", seconds / 60, seconds % 60)
    }
}

/// Three dots hopping in turn, like a voice still talking, while the text is on its way.
struct HoppingDots: View {
    /// One hop of each dot, one after another, then a short rest.
    private static let period = 1.1
    private static let hop = 0.36
    private static let stagger = 0.14
    private static let height: CGFloat = 4.5

    var body: some View {
        TimelineView(.animation) { context in
            let time = context.date.timeIntervalSinceReferenceDate
            HStack(spacing: 5) {
                ForEach(0..<3, id: \.self) { index in
                    let lift = Self.lift(time - Double(index) * Self.stagger)
                    Circle()
                        .fill(.primary.opacity(0.6 + 0.3 * lift))
                        .frame(width: 6, height: 6)
                        .offset(y: -Self.height * CGFloat(lift))
                }
            }
            .frame(height: 18)
        }
    }

    /// 0 at rest, 1 at the top of a hop.
    private static func lift(_ time: Double) -> Double {
        let phase = time.truncatingRemainder(dividingBy: period)
        let t = (phase < 0 ? phase + period : phase) / hop
        return t < 1 ? sin(t * .pi) : 0
    }
}

/// The processing bar: a soft wash of the accent colour filling the capsule from the left.
struct ProgressFill: View {
    @ObservedObject var bar: ProcessingBar

    var body: some View {
        GeometryReader { proxy in
            LinearGradient(colors: [Color.accentColor.opacity(0.12), Color.accentColor.opacity(0.3)],
                           startPoint: .leading, endPoint: .trailing)
                .frame(width: proxy.size.width * bar.shown)
        }
        .clipShape(.capsule)
        .allowsHitTesting(false)
    }
}

struct LevelBars: View {
    let levels: [Float]

    var body: some View {
        HStack(alignment: .center, spacing: 2) {
            ForEach(Array(levels.enumerated()), id: \.offset) { _, level in
                Capsule()
                    .fill(.primary.opacity(0.85))
                    .frame(width: 2.5, height: max(3, CGFloat(level) * 18))
            }
        }
        .frame(height: 18)
        .animation(.linear(duration: 0.08), value: levels)
    }
}
