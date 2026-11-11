import AppKit
import SwiftUI
import TypelessCore

/// Shared visual building blocks for the settings window and the menu-bar popover.
enum Theme {
    /// macOS 26 grouped-content radius.
    static let cardRadius: CGFloat = 14
    /// How wide a page of settings gets: past this, rows only put more space between a title and its control.
    static let formWidth: CGFloat = 720
    /// How wide Home gets: past this, its figures and lists only drift apart.
    static let dashboardWidth: CGFloat = 900
    /// The fill of a card: the window's background a shade towards the text colour, the tint a grouped form gives its
    /// sections, so Home and History sit on the page the way a page of settings does. Opaque, so that what stays put in
    /// a card while its rows scroll (History's day headings) can be filled with it too.
    static let cardFill = Color(nsColor: NSColor(name: nil) { appearance in
        let dark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        var base = NSColor.windowBackgroundColor
        appearance.performAsCurrentDrawingAppearance {
            base = NSColor.windowBackgroundColor.usingColorSpace(.sRGB) ?? base
        }
        return base.blended(withFraction: dark ? 0.05 : 0.04, of: dark ? .white : .black) ?? base
    })
    /// A hairline inside a card: between rows, columns and panes.
    static let hairline = Color.primary.opacity(0.08)
}

/// The app's mark: three voice bars running into a text caret, so what you say becomes text at your cursor. The same
/// geometry and colours as the app icon (`scripts/make_icon.py` draws that), in tile units (0…1, y down).
enum Logo {
    /// Centre x and height of each voice bar; the bars and the caret are all `stroke` wide and centred on y = 0.5.
    static let bars: [(x: CGFloat, height: CGFloat)] = [(0.245, 0.26), (0.395, 0.50), (0.545, 0.34)]
    static let caret: (x: CGFloat, height: CGFloat) = (0.725, 0.62)
    static let stroke: CGFloat = 0.104

    /// Two colours, one per part: amber for the voice, crimson for the caret.
    static let voiceColor = rgb(0xFFA000)
    static let caretColor = rgb(0xE8173F)
    static let tile = LinearGradient(colors: [rgb(0xFFFFFF), rgb(0xEEEEF2)], startPoint: .top, endPoint: .bottom)

    private static func rgb(_ hex: Int) -> Color {
        Color(red: Double(hex >> 16 & 0xFF) / 255, green: Double(hex >> 8 & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }

    /// The bars (or the caret) laid out in `rect`, the tile's frame.
    static func capsules(_ items: [(x: CGFloat, height: CGFloat)], in rect: CGRect) -> [CGRect] {
        items.map { item in
            CGRect(x: rect.minX + (item.x - stroke / 2) * rect.width, y: rect.minY + (0.5 - item.height / 2) * rect.height,
                   width: stroke * rect.width, height: item.height * rect.height)
        }
    }

    /// The menu-bar icon, a template image: the voice bars at half strength and the caret solid; while recording, the
    /// whole mark is cut out of a filled disc.
    static func menuBarImage(recording: Bool) -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { rect in
            if recording {
                let path = NSBezierPath(ovalIn: rect.insetBy(dx: 0.5, dy: 0.5))
                for r in Logo.capsules(Logo.bars + [Logo.caret], in: Logo.frame(centredIn: rect, caretHeight: 9.5)) {
                    path.appendRoundedRect(r, xRadius: r.width / 2, yRadius: r.width / 2)
                }
                path.windingRule = .evenOdd // the capsules don't overlap, so each one is a hole
                NSColor.black.setFill()
                path.fill()
            } else {
                let tile = Logo.frame(centredIn: rect, caretHeight: 13.5)
                let layers: [(items: [(x: CGFloat, height: CGFloat)], alpha: CGFloat)] = [(Logo.bars, 0.5), ([Logo.caret], 1)]
                for layer in layers {
                    NSColor.black.withAlphaComponent(layer.alpha).setFill()
                    for r in Logo.capsules(layer.items, in: tile) {
                        NSBezierPath(roundedRect: r, xRadius: r.width / 2, yRadius: r.width / 2).fill()
                    }
                }
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "OpenTypeless"
        return image
    }

    /// The tile frame that centres the mark in `rect` with the caret `caretHeight` points tall.
    private static func frame(centredIn rect: CGRect, caretHeight: CGFloat) -> CGRect {
        let side = caretHeight / caret.height
        let markCentre = (bars[0].x - stroke / 2 + caret.x + stroke / 2) / 2
        return CGRect(x: rect.midX - markCentre * side, y: rect.midY - side / 2, width: side, height: side)
    }
}

/// One stroke of the mark (a voice bar or the caret) drawn across the tile's frame.
struct LogoStroke: Shape {
    let item: (x: CGFloat, height: CGFloat)

    func path(in rect: CGRect) -> Path {
        var path = Path()
        for r in Logo.capsules([item], in: rect) {
            path.addRoundedRect(in: r, cornerSize: CGSize(width: r.width / 2, height: r.width / 2), style: .circular)
        }
        return path
    }
}

/// The app's tile: the mark on a white tile, the same artwork as the macOS app icon.
struct AppTile: View {
    var size: CGFloat = 32

    var body: some View {
        let tile = RoundedRectangle(cornerRadius: size * 0.26, style: .continuous)
        ZStack {
            tile.fill(Logo.tile)
            tile.strokeBorder(Color.black.opacity(0.07), lineWidth: max(0.5, size * 0.006))
            ForEach(Logo.bars.indices, id: \.self) { i in
                LogoStroke(item: Logo.bars[i]).fill(Logo.voiceColor)
            }
            LogoStroke(item: Logo.caret).fill(Logo.caretColor)
        }
        .frame(width: size, height: size)
        .shadow(color: .black.opacity(0.18), radius: size * 0.1, y: size * 0.04)
    }
}

/// An SF Symbol in the secondary colour, centred in a square of `size`: one colour for every icon, so colour is left
/// to mean state (a warning, a recording, a selection).
struct IconBadge: View {
    let symbol: String
    var size: CGFloat = 26

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: size * 0.62))
            .foregroundStyle(.secondary)
            .frame(width: size, height: size)
    }
}

/// True inside a settings page's grouped `Form`: the building blocks below then become the form's own sections and rows,
/// drawn by the system like System Settings, instead of drawing cards of their own.
private struct InSettingsFormKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var inSettingsForm: Bool {
        get { self[InSettingsFormKey.self] }
        set { self[InSettingsFormKey.self] = newValue }
    }
}

extension View {
    /// Padding for a card's own rows, which a grouped form's rows already have.
    func rowPadding(_ edges: Edge.Set = .all, _ length: CGFloat) -> some View {
        modifier(RowPadding(edges: edges, length: length))
    }
}

private struct RowPadding: ViewModifier {
    let edges: Edge.Set
    let length: CGFloat
    @Environment(\.inSettingsForm) private var inForm

    func body(content: Content) -> some View {
        content.padding(edges, inForm ? 0 : length)
    }
}

/// A grouped container, filled like a grouped form's section; in a settings form, a section of its own.
struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    @Environment(\.inSettingsForm) private var inForm

    var body: some View {
        if inForm {
            Section { content }
        } else {
            VStack(alignment: .leading, spacing: 0) { content }
                .background(
                    RoundedRectangle(cornerRadius: Theme.cardRadius, style: .continuous)
                        .fill(Theme.cardFill)
                )
        }
    }
}

/// A "?" beside a title, holding what the page doesn't spell out. Pointing at it shows the text; clicking keeps it open
/// until the next click, for reading at leisure (and for the keyboard and VoiceOver, which can't hover).
struct HelpTip: View {
    let text: String
    /// What it explains, which VoiceOver says.
    var topic: String? = nil
    /// False keeps the "?" out of sight (still there for the keyboard and VoiceOver) until this turns true: a row's
    /// tip shows while the pointer is over the row, so a page of rows isn't a column of question marks.
    var revealed = true
    @State private var hovering = false
    @State private var pinned = false
    /// The change of `hovering` under way: passing over a "?" on the way elsewhere doesn't open it, and the pointer
    /// crossing the gap between the "?" and the text doesn't close it.
    @State private var settling: Task<Void, Never>?
    /// When the popover last went away by itself, and whether it was pinned. A click outside a popover closes it as the
    /// button goes down and then still reaches what's under it, and that may be this "?".
    @State private var dismissed: (at: Date, wasPinned: Bool)?

    var body: some View {
        Button(action: click) {
            Image(systemName: "questionmark.circle")
                .font(.system(size: 12))
                .foregroundStyle(hovering || pinned ? .secondary : .tertiary)
                .contentShape(Rectangle().inset(by: -3))
        }
        .buttonStyle(.plain)
        .opacity(revealed || hovering || pinned ? 1 : 0)
        .animation(.easeOut(duration: 0.12), value: revealed)
        .accessibilityLabel(topic.map { L("关于「\($0)」", "About \($0)") } ?? L("说明", "More information"))
        .accessibilityHint(text)
        .onHover(perform: setHovering)
        .popover(isPresented: Binding(get: { hovering || pinned }, set: { if !$0 { dismiss() } }), arrowEdge: .top) {
            Text(text)
                .font(.system(size: 12))
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 280, alignment: .leading)
                .padding(12)
                .onHover(perform: setHovering)
        }
    }

    private func setHovering(_ inside: Bool) {
        settling?.cancel()
        settling = Task {
            try? await Task.sleep(nanoseconds: inside ? 120_000_000 : 150_000_000)
            if !Task.isCancelled { hovering = inside }
        }
    }

    private func dismiss() {
        dismissed = (Date(), pinned)
        hovering = false
        pinned = false
    }

    private func click() {
        if let dismissed, Date().timeIntervalSince(dismissed.at) < 0.5 {
            pinned = !dismissed.wasPinned
        } else {
            pinned.toggle()
        }
        dismissed = nil
    }
}

/// A section's title, with its explanation behind a "?".
struct SectionTitle: View {
    let title: String
    var help: String? = nil
    @Environment(\.inSettingsForm) private var inForm

    var body: some View {
        HStack(spacing: 5) {
            if inForm {
                // The form's own section-header style.
                Text(title)
            } else {
                Text(title)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.secondary)
            }
            if let help { HelpTip(text: help, topic: title) }
        }
        .padding(.leading, inForm ? 0 : 4)
    }
}

/// `help` explains the section; `footer` is for what needs noticing now, such as a warning.
struct CardSection<Content: View>: View {
    let title: String
    var help: String? = nil
    var footer: String? = nil
    @ViewBuilder var content: Content
    @Environment(\.inSettingsForm) private var inForm

    var body: some View {
        if inForm {
            Section {
                content
            } header: {
                SectionTitle(title: title, help: help)
            } footer: {
                if let footer {
                    Text(footer)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        } else {
            VStack(alignment: .leading, spacing: 8) {
                SectionTitle(title: title, help: help)
                Card { content }
                if let footer {
                    Text(footer)
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, 4)
                }
            }
        }
    }
}

struct CardRow<Trailing: View>: View {
    var icon: String? = nil
    let title: String
    var subtitle: String? = nil
    var help: String? = nil
    @ViewBuilder var trailing: Trailing
    @Environment(\.inSettingsForm) private var inForm
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 12) {
            if let icon { IconBadge(symbol: icon, size: 24) }
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 5) {
                    Text(title).font(.system(size: 13))
                    if let help { HelpTip(text: help, topic: title, revealed: hovering) }
                }
                if let subtitle {
                    Text(subtitle)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 12)
            trailing
        }
        .padding(.horizontal, inForm ? 0 : 14)
        .padding(.vertical, inForm ? 1 : 9)
        .frame(minHeight: inForm ? 22 : 42)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

extension CardRow where Trailing == RowSwitch {
    /// A row ending in an on/off switch.
    init(icon: String? = nil, title: String, subtitle: String? = nil, help: String? = nil, isOn: Binding<Bool>) {
        self.init(icon: icon, title: title, subtitle: subtitle, help: help) { RowSwitch(title: title, isOn: isOn) }
    }
}

/// The switch at the end of a row. It carries the row's title as its hidden label, so VoiceOver says what it turns on
/// (the title the row shows is a separate text).
struct RowSwitch: View {
    let title: String
    @Binding var isOn: Bool

    var body: some View {
        Toggle(title, isOn: $isOn).toggleStyle(.switch).labelsHidden()
    }
}

/// A hairline between a card's rows; a grouped form draws its own.
struct CardDivider: View {
    var inset: CGFloat = 14
    @Environment(\.inSettingsForm) private var inForm

    var body: some View {
        if !inForm {
            Rectangle().fill(Theme.hairline).frame(height: 1).padding(.leading, inset)
        }
    }
}

/// A keyboard key cap, drawn as a small piece of Liquid Glass.
struct KeyCap: View {
    let text: String
    var large = false

    var body: some View {
        Text(text)
            .font(.system(size: large ? 17 : 12, weight: .semibold, design: .rounded))
            .padding(.horizontal, large ? 14 : 7)
            .frame(minWidth: large ? 44 : 22, minHeight: large ? 38 : 22)
            .glassEffect(.regular, in: .rect(cornerRadius: large ? 11 : 6))
    }
}

struct KeyCaps: View {
    let caps: [String]
    var large = false

    var body: some View {
        GlassEffectContainer(spacing: large ? 8 : 4) {
            HStack(spacing: large ? 8 : 4) {
                ForEach(Array(caps.enumerated()), id: \.offset) { _, cap in KeyCap(text: cap, large: large) }
            }
        }
    }
}

struct StatusPill: View {
    let text: String
    let color: Color
    var pulsing = false

    var body: some View {
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 6, height: 6)
            Text(text).font(.system(size: 11, weight: .medium))
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 3)
        .background(Capsule().fill(color.opacity(0.13)))
        .foregroundStyle(color)
    }
}

/// Tinted inline banner for warnings and hints; in a settings form, a row of its own with the symbol in colour.
struct Banner<Trailing: View>: View {
    let symbol: String
    let color: Color
    let text: String
    @ViewBuilder var trailing: Trailing
    @Environment(\.inSettingsForm) private var inForm

    var body: some View {
        if inForm {
            Section {
                HStack(alignment: .center, spacing: 10) {
                    Image(systemName: symbol)
                        .symbolRenderingMode(.multicolor)
                        .foregroundStyle(color)
                        .font(.system(size: 16))
                        .frame(width: 22)
                    Text(text).fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 8)
                    trailing
                }
            }
        } else {
            HStack(alignment: .center, spacing: 10) {
                Image(systemName: symbol).foregroundStyle(color).font(.system(size: 13, weight: .semibold))
                Text(text).font(.system(size: 12)).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                trailing
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(color.opacity(0.1)))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(color.opacity(0.22)))
        }
    }
}

/// Plain button with a hover highlight, for list rows and footer actions.
struct HoverButtonStyle: ButtonStyle {
    var cornerRadius: CGFloat = 8

    func makeBody(configuration: Configuration) -> some View {
        HoverBody(configuration: configuration, cornerRadius: cornerRadius)
    }

    private struct HoverBody: View {
        let configuration: Configuration
        let cornerRadius: CGFloat
        @State private var hovering = false

        var body: some View {
            configuration.label
                .contentShape(Rectangle())
                .background(
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .fill(Color.primary.opacity(configuration.isPressed ? 0.1 : hovering ? 0.055 : 0))
                )
                .onHover { hovering = $0 }
        }
    }
}

extension Locale {
    /// The locale dates and times are written in: the system's own format when it's in the app's language (so an
    /// English UI in the UK still writes 26/09 and 24-hour time), otherwise the usual one for the app's language.
    static var app: Locale {
        let language = AppLanguage.resolved
        return AppLanguage.matching(Locale.current.identifier) == language ? .current : language.locale
    }
}

extension Date {
    /// "14:32" today, "Yesterday 09:10", otherwise "9/26 18:02" (in the app's language: "26.9. 18:02" in German).
    var shortStamp: String {
        let calendar = Calendar.current
        let time = appFormatted(time: .shortened)
        if calendar.isDateInToday(self) { return time }
        if calendar.isDateInYesterday(self) { return L("昨天", "Yesterday") + " " + time }
        return formatted(.dateTime.month(.defaultDigits).day().locale(.app)) + " " + time
    }

    /// The date and/or the time in the app's language (see `Locale.app`).
    func appFormatted(date: Date.FormatStyle.DateStyle = .omitted, time: Date.FormatStyle.TimeStyle = .omitted) -> String {
        formatted(Date.FormatStyle(date: date, time: time, locale: .app))
    }
}

/// A horizontal input level bar (green → red) with a peak marker.
struct LevelMeter: View {
    let level: Float
    let peak: Float

    var body: some View {
        GeometryReader { geometry in
            let width = geometry.size.width
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.08))
                Capsule()
                    .fill(LinearGradient(colors: [.green, .green, .yellow, .orange, .red], startPoint: .leading, endPoint: .trailing))
                    .mask(alignment: .leading) {
                        Rectangle().frame(width: width * CGFloat(min(1, max(0, level))))
                    }
                Capsule()
                    .fill(Color.primary.opacity(0.55))
                    .frame(width: 2)
                    .offset(x: max(0, width * CGFloat(min(1, peak)) - 2))
                    .opacity(peak > 0.02 ? 1 : 0)
            }
        }
        .frame(height: 8)
        .animation(.linear(duration: 0.08), value: level)
    }
}
