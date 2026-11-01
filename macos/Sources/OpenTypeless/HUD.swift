import AppKit
import SwiftUI
import TypelessCore

@MainActor
final class HUDModel: ObservableObject {
    enum Phase: Equatable {
        case hidden
        case recording
        /// Transcribing and cleaning up — one state, the user doesn't need the details: dots riding a wave, and a ring
        /// round the ■ button filling as the answers come back (`bar`).
        case working
        /// Nothing to paste into, so the text went to the clipboard.
        case copied
        /// New words were learned from the user's fixes to the last dictation.
        case learned(String)
        /// The answer to a question about the selection (see `SelectionCommand.Reply`), also on the clipboard.
        case answer(String)
        case error(String)

        /// The text a notice shows (its length decides how long it stays up).
        var message: String? {
            switch self {
            case let .learned(terms): return L("已加入词汇表：", "Added to vocabulary: ") + terms
            case let .answer(answer): return L("答案已复制：", "Answer copied: ") + answer
            case let .error(message): return message
            default: return nil
            }
        }

        /// Whether the capsule shows its ✕ and ↑ / ■ buttons (and so takes clicks on itself).
        var hasButtons: Bool { self == .recording || self == .working }
    }

    @Published var phase: Phase = .hidden
    /// Live preview of what's being said (empty when it's off or hasn't heard anything yet).
    @Published var preview = ""
    /// The mode the dictation is in, when it isn't plain dictation ("→ EN" for a translation into English).
    @Published var badge: String?
    /// Counts the recordings started, so the glow behind the capsule blooms once at the start of each.
    @Published private(set) var recordingStarts = 0
    let bar = ProcessingBar()
    let levels = InputLevels()

    /// The ✕ button, and the ■ while the text is on its way: the same as Esc (set by the session).
    var onCancel: @MainActor () -> Void = {}
    /// The ↑ button: ends the recording as letting go of the key would (set by the session).
    var onFinish: @MainActor () -> Void = {}
    /// Where the capsule is in the panel (SwiftUI's top-left coordinates), reported by the view whenever it moves or
    /// changes size; the panel takes clicks only while the mouse is over it (see `HUDController.updateMouse`).
    var capsuleFrame: CGRect? {
        didSet { if capsuleFrame != oldValue { onLayout() } }
    }
    var onLayout: @MainActor () -> Void = {}

    func push(level: Float) {
        levels.push(level)
    }

    func resetLevels() {
        levels.reset()
    }

    func recordingStarted() {
        recordingStarts += 1
    }
}

/// The recent input levels the capsule's waveform shows. Its own object, so the ~20 updates a second redraw only the
/// waveform, not the whole capsule.
@MainActor
final class InputLevels: ObservableObject {
    @Published var values: [Float] = Array(repeating: 0, count: 18)

    func push(_ level: Float) {
        // Perceptual scaling so normal speech fills the bars.
        let scaled = min(1, sqrt(level) * 3.2)
        values.removeFirst()
        values.append(scaled)
    }

    func reset() {
        values = Array(repeating: 0, count: values.count)
    }

    /// How much each bar of the waveform shrinks with its distance from the middle one.
    private static let taper: [Float] = [1, 0.86, 0.72, 0.58, 0.46]

    /// The capsule's 9 waveform bars (0…1) from the recent levels: the newest level in the middle and
    /// older ones further out on both sides, so speech ripples outwards, tapering towards the ends. The right side runs
    /// half a step behind the left (each bar the mean of two levels), so the two sides don't mirror each other. The same
    /// as the Windows capsule.
    static func waveform(_ levels: [Float]) -> [Float] {
        let middle = taper.count - 1
        func level(age: Int) -> Float { age < levels.count ? levels[levels.count - 1 - age] : 0 }
        return (0...(2 * middle)).map { index in
            let distance = abs(index - middle)
            let value = index <= middle ? level(age: distance) : (level(age: distance) + level(age: distance + 1)) / 2
            return value * taper[distance]
        }
    }
}

/// How far a dictation being transcribed and cleaned up has got, shown as the ring round the capsule's ■ button (see
/// `ProcessingProgress`). Its own object, so its updates on every frame redraw only the ring.
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

/// Small tinted Liquid Glass capsule near the bottom of the screen: ✕, a waveform and ↑. Never takes
/// focus, so the target text field stays focused for the paste. Clicks go through to what's below, except on the
/// capsule itself while it shows its ✕ and ↑ / ■ buttons.
@MainActor
final class HUDController {
    let model = HUDModel()
    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?
    private var mouseMonitors: [Any] = []
    /// Room for the capsule with its glow and, above it, the live preview line.
    private static let size = NSSize(width: 460, height: 124)

    init() {
        model.onLayout = { [weak self] in self?.updateMouse() }
        // Made up front, so its view is already there to see the first recording start (and bloom for it).
        panel = makePanel()
    }

    func show(_ phase: HUDModel.Phase, autoHideAfter: Double? = nil) {
        hideWork?.cancel()
        if phase != .working { model.bar.stop() }
        if phase == .recording, model.phase != .recording { model.recordingStarted() }
        withAnimation(.spring(duration: 0.32, bounce: 0.18)) { model.phase = phase }
        if phase == .hidden {
            stopWatchingMouse()
            panel?.orderOut(nil)
            return
        }
        guard let panel else { return }
        if !panel.isVisible { position(panel) }
        panel.orderFrontRegardless()
        if phase.hasButtons { watchMouse() } else { stopWatchingMouse() }

        if let autoHideAfter {
            let work = DispatchWorkItem { [weak self] in self?.show(.hidden) }
            hideWork = work
            let delay = phase.message.map { Self.displayTime(for: $0, requested: autoHideAfter) } ?? autoHideAfter
            DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
        }
    }

    /// How long a notice stays up: what the caller asked for, plus reading time (about 15 characters a second) for
    /// whatever goes past the first line, up to 12 s. Chinese, Japanese and Korean characters count double: they are
    /// twice as wide. The same rule as the Windows capsule.
    static func displayTime(for message: String, requested: Double) -> Double {
        let width = message.unicodeScalars.reduce(0) { $0 + (isWide($1.value) ? 2 : 1) }
        let extra = Double(max(0, width - 50)) / 15
        return min(requested + extra, max(requested, 12))
    }

    private static func isWide(_ scalar: UInt32) -> Bool {
        (0x1100...0x115F).contains(scalar) || (0x2E80...0xA4CF).contains(scalar) || (0xAC00...0xD7A3).contains(scalar)
            || (0xF900...0xFAFF).contains(scalar) || (0xFE30...0xFE4F).contains(scalar) || (0xFF00...0xFF60).contains(scalar)
            || (0xFFE0...0xFFE6).contains(scalar)
    }

    /// Transcribing and cleaning up, with the ring starting from empty.
    func showWorking(polishes: Bool) {
        model.bar.start(polishes: polishes)
        show(.working)
    }

    /// The text went in: the ring runs to the end, then the capsule goes.
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
        // Click-through until the mouse is over the capsule's buttons (see `updateMouse`).
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        let host = FirstClickHostingView(rootView: HUDView(model: model))
        host.frame = NSRect(origin: .zero, size: Self.size)
        panel.contentView = host
        return panel
    }

    private func position(_ panel: NSPanel) {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        guard let frame = screen?.visibleFrame else { return }
        // The capsule's bottom edge 46 pt above the Dock, as it always was (the view keeps 24 pt below it for the glow).
        panel.setFrameOrigin(NSPoint(x: frame.midX - Self.size.width / 2, y: frame.minY + 22))
    }

    // MARK: - Clicks on the buttons

    /// Follows the mouse while the buttons show. Moves over other apps reach the global monitor, moves over the panel
    /// (once it takes clicks) the local one.
    private func watchMouse() {
        if mouseMonitors.isEmpty {
            if let global = NSEvent.addGlobalMonitorForEvents(matching: .mouseMoved, handler: { [weak self] _ in
                MainActor.assumeIsolated { self?.updateMouse() }
            }) {
                mouseMonitors.append(global)
            }
            if let local = NSEvent.addLocalMonitorForEvents(matching: .mouseMoved, handler: { [weak self] event in
                MainActor.assumeIsolated { self?.updateMouse() }
                return event
            }) {
                mouseMonitors.append(local)
            }
        }
        updateMouse()
    }

    private func stopWatchingMouse() {
        for monitor in mouseMonitors { NSEvent.removeMonitor(monitor) }
        mouseMonitors.removeAll()
        panel?.ignoresMouseEvents = true
    }

    /// The panel takes clicks only while the mouse is over the capsule and it shows its buttons, so a click anywhere
    /// else still lands in the app below. The panel never becomes key and never activates the app, so the text field
    /// keeps its focus.
    private func updateMouse() {
        guard let panel else { return }
        var over = false
        if panel.isVisible, model.phase.hasButtons, let frame = model.capsuleFrame {
            let capsule = NSRect(x: panel.frame.minX + frame.minX, y: panel.frame.maxY - frame.maxY,
                                 width: frame.width, height: frame.height)
            over = capsule.contains(NSEvent.mouseLocation)
        }
        if panel.ignoresMouseEvents == over { panel.ignoresMouseEvents = !over }
    }
}

/// Takes the first click: the panel is never key, and the click on ✕ or ↑ shouldn't only make it so.
private final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

struct HUDView: View {
    @ObservedObject var model: HUDModel
    @Namespace private var glass

    /// The capsule's height; a notice that wraps grows it into a rounded rectangle with the same corners.
    static let height: CGFloat = 44
    /// The glass is tinted with the accent colour, lightly, so the black ✕ and the waveform stay easy to see. The UI
    /// snapshots' offscreen rendering can't draw Liquid Glass at all, and tinted it leaves the whole view blank there,
    /// so they get the plain glass (`--live` snapshots, drawn by the window server, show the tint).
    static var glass: Glass {
        UISnapshots.active && !UISnapshots.live ? .regular : .regular.tint(Color.accentColor.opacity(0.3))
    }

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
                        .glassEffect(Self.glass, in: .capsule)
                        .transition(.opacity)
                }
                capsule
            }
            .animation(.easeOut(duration: 0.15), value: model.preview.isEmpty)
        }
        // The capsule stays where it always was; the preview grows upwards.
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
        .padding(.bottom, 24)
        .opacity(model.phase == .hidden ? 0 : 1)
    }

    private var capsule: some View {
        content
            .frame(minHeight: Self.height)
            .glassEffect(Self.glass, in: .rect(cornerRadius: Self.height / 2))
            .glassEffectID("hud", in: glass)
            .background { RecordingGlow(trigger: model.recordingStarts) }
            .onGeometryChange(for: CGRect.self) { proxy in
                proxy.frame(in: .global)
            } action: { frame in
                model.capsuleFrame = frame
            }
    }

    @ViewBuilder private var content: some View {
        switch model.phase {
        case .hidden:
            Color.clear.frame(width: 1)
        case .recording, .working:
            controls(working: model.phase == .working)
        case .copied:
            HStack(spacing: 7) {
                Image(systemName: "doc.on.clipboard").foregroundStyle(.secondary)
                Text(L("已复制到剪贴板", "Copied to clipboard"))
            }
            .noticeStyle()
        case .learned:
            NoticeRow(symbol: "character.book.closed.fill", tint: .tint, text: model.phase.message ?? "")
                .noticeStyle()
        case .answer:
            // The start of it; the whole answer is on the clipboard.
            NoticeRow(symbol: "text.bubble.fill", tint: .tint, text: model.phase.message ?? "")
                .noticeStyle()
        case let .error(message):
            // Up to three lines: the end of an error is often what to do about it ("… → Privacy & Security → Microphone").
            NoticeRow(symbol: "exclamationmark.triangle.fill", tint: .orange, text: message)
                .noticeStyle()
        }
    }

    /// ✕, the waveform (dots riding a wave once the key is let go) and ↑ (■ while the text is on its way).
    /// Recording and working are the same size, so the capsule doesn't move when the key is released.
    private func controls(working: Bool) -> some View {
        HStack(spacing: 0) {
            CancelButton { model.onCancel() }
            HStack(spacing: 10) {
                ZStack {
                    if working {
                        WaveDots().transition(.opacity)
                    } else {
                        LiveWaveform(levels: model.levels).transition(.opacity)
                    }
                }
                .frame(width: 64)
                if let badge = model.badge { ModeBadge(text: badge).transition(.opacity) }
            }
            .padding(.horizontal, 14)
            SendButton(working: working, bar: model.bar) {
                if working { model.onCancel() } else { model.onFinish() }
            }
        }
        .padding(.horizontal, 9)
        .frame(height: Self.height)
    }
}

private extension View {
    func noticeStyle() -> some View {
        font(.system(size: 13, weight: .medium))
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
    }
}

/// The ✕ on the left: a solid black circle (white in dark mode).
private struct CancelButton: View {
    let action: @MainActor () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark")
                .font(.system(size: 11, weight: .bold))
                .foregroundStyle(Color(nsColor: .textBackgroundColor))
                .frame(width: 26, height: 26)
                .background(Circle().fill(Color.primary))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(L("取消", "Cancel"))
    }
}

/// The button on the right: ↑ while recording; ■ once the key is let go, with a ring filling round it as the text
/// comes back (see `ProcessingBar`).
private struct SendButton: View {
    let working: Bool
    @ObservedObject var bar: ProcessingBar
    let action: @MainActor () -> Void

    var body: some View {
        Button(action: action) {
            ZStack {
                Circle().fill(Color.accentColor.opacity(0.3))
                if working {
                    Circle()
                        .trim(from: 0, to: bar.shown)
                        .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .padding(1)
                        .opacity(bar.shown > 0 ? 1 : 0)
                }
                Image(systemName: working ? "stop" : "arrow.up")
                    .font(.system(size: working ? 10 : 12, weight: .bold))
                    .foregroundStyle(Color.primary)
                    .contentTransition(.symbolEffect(.replace))
            }
            .frame(width: 26, height: 26)
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(working ? L("停止", "Stop") : L("完成", "Done"))
    }
}

/// A soft bloom of blue, teal, green and yellow behind the capsule as a recording starts, seen through the glass; it fades within
/// two seconds. It's only in the view while it shows: blurred, it's work to draw, and the snapshots' offscreen
/// rendering can't draw it at all.
private struct RecordingGlow: View {
    /// `HUDModel.recordingStarts`: each new value blooms once.
    let trigger: Int
    @State private var shown = false
    @State private var glow = 0.0
    /// The bloom running now, so the end of an earlier one doesn't take it away.
    @State private var bloom = 0

    private static let colors: [Color] = [
        Color(red: 0.26, green: 0.52, blue: 0.96), // blue
        Color(red: 0.14, green: 0.76, blue: 0.88), // teal
        Color(red: 0.20, green: 0.66, blue: 0.33), // green
        Color(red: 0.98, green: 0.74, blue: 0.02), // yellow
        Color(red: 0.26, green: 0.52, blue: 0.96),
    ]

    var body: some View {
        ZStack {
            if shown {
                Capsule()
                    .fill(AngularGradient(colors: Self.colors, center: .center))
                    .padding(-8)
                    .blur(radius: 12)
                    .opacity(glow)
                    .scaleEffect(0.9 + 0.1 * glow)
            }
        }
        .allowsHitTesting(false)
        // `initial`: the panel's view may first be drawn for the recording that has just started.
        .onChange(of: trigger, initial: true) {
            if trigger > 0 { start() }
        }
    }

    private func start() {
        let current = bloom + 1
        bloom = current
        // Shown in the same animation, so it fades in (a view only just added has no earlier opacity to animate from).
        withAnimation(.easeOut(duration: 0.35)) {
            shown = true
            glow = 0.9
        } completion: {
            withAnimation(.easeInOut(duration: 1.1).delay(0.35)) { glow = 0 } completion: {
                if bloom == current { shown = false }
            }
        }
    }
}

/// Three dots riding a wave from left to right while the text is on its way.
struct WaveDots: View {
    /// One wave, and how far each dot runs behind the one before it, as a share of the wave.
    private static let period = 1.15
    private static let lag = 0.2
    private static let amplitude: CGFloat = 4.5

    var body: some View {
        TimelineView(.animation) { context in
            let time = context.date.timeIntervalSinceReferenceDate
            HStack(spacing: 3.5) {
                ForEach(0..<3, id: \.self) { index in
                    Circle()
                        .fill(Color.primary.opacity(0.9))
                        .frame(width: 4, height: 4)
                        .offset(y: Self.offset(time, index: index))
                }
            }
            .frame(height: 20)
        }
    }

    /// The same wave as the Windows dots.
    private static func offset(_ time: Double, index: Int) -> CGFloat {
        -amplitude * CGFloat(sin(2 * .pi * (time / period - Double(index) * lag)))
    }
}

/// The dictation's mode in the capsule ("→ EN"): a small pill in the accent colour, like the status pills elsewhere.
private struct ModeBadge: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .semibold))
            .lineLimit(1)
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(Capsule().fill(Color.accentColor.opacity(0.18)))
            .foregroundStyle(Color.accentColor)
    }
}

/// A notice in the capsule: an icon and up to three lines of text.
private struct NoticeRow<Tint: ShapeStyle>: View {
    let symbol: String
    let tint: Tint
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 7) {
            Image(systemName: symbol).foregroundStyle(tint)
            Text(text).lineLimit(3).truncationMode(.tail).fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: 340)
    }
}

/// The waveform, watching the levels themselves (see `InputLevels`).
private struct LiveWaveform: View {
    @ObservedObject var levels: InputLevels

    var body: some View {
        Waveform(levels: InputLevels.waveform(levels.values))
    }
}

/// The waveform: thin bars that are dots while it's quiet and grow with the voice.
struct Waveform: View {
    let levels: [Float]
    private static let barWidth: CGFloat = 2.5
    private static let maxHeight: CGFloat = 20

    var body: some View {
        HStack(alignment: .center, spacing: 4) {
            ForEach(levels.indices, id: \.self) { index in
                Capsule()
                    .fill(Color.primary.opacity(0.9))
                    .frame(width: Self.barWidth, height: max(Self.barWidth, CGFloat(levels[index]) * Self.maxHeight))
            }
        }
        .frame(height: Self.maxHeight)
        .animation(.easeOut(duration: 0.1), value: levels)
    }
}
