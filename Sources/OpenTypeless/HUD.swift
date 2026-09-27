import AppKit
import SwiftUI
import TypelessCore

@MainActor
final class HUDModel: ObservableObject {
    enum Phase: Equatable {
        case hidden
        case recording
        /// Transcribing and cleaning up — one state, the user doesn't need the details.
        case working
        /// Nothing to paste into, so the text went to the clipboard.
        case copied
        case error(String)
    }

    @Published var phase: Phase = .hidden
    @Published var levels: [Float] = Array(repeating: 0, count: 18)
    @Published var startedAt = Date()

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

/// Small Liquid Glass capsule near the bottom of the screen. Never takes focus or mouse clicks, so the
/// target text field stays focused for the paste.
@MainActor
final class HUDController {
    let model = HUDModel()
    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?
    private static let size = NSSize(width: 380, height: 72)

    func show(_ phase: HUDModel.Phase, autoHideAfter: Double? = nil) {
        hideWork?.cancel()
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
            content
                .font(.system(size: 13, weight: .medium))
                .padding(.horizontal, 14)
                .frame(height: 36)
                .glassEffect(.regular, in: .capsule)
                .glassEffectID("hud", in: glass)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
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
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text(L("处理中", "Working"))
            }
        case .copied:
            HStack(spacing: 7) {
                Image(systemName: "doc.on.clipboard").foregroundStyle(.secondary)
                Text(L("已复制到剪贴板", "Copied to clipboard"))
            }
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
