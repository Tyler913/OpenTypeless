import AppKit
import SwiftUI
import TypelessCore

/// Shared visual building blocks for the settings window and the menu-bar popover.
enum Theme {
    static let brand = LinearGradient(
        colors: [Color(red: 0.36, green: 0.30, blue: 0.95), Color(red: 0.12, green: 0.62, blue: 0.98)],
        startPoint: .topLeading, endPoint: .bottomTrailing
    )
    /// macOS 26 grouped-content radius.
    static let cardRadius: CGFloat = 16
}

/// The app's mark: three voice bars running into a text caret, so what you say becomes text at your cursor. The same
/// geometry and colours as the app icon (`scripts/make_icon.py` draws that), in tile units (0…1, y down).
enum Logo {
    /// Centre x and height of each voice bar; the bars and the caret are all `stroke` wide and centred on y = 0.5.
    static let bars: [(x: CGFloat, height: CGFloat)] = [(0.270, 0.24), (0.405, 0.46), (0.540, 0.32)]
    static let caret: (x: CGFloat, height: CGFloat) = (0.705, 0.58)
    static let stroke: CGFloat = 0.088

    static let tile = LinearGradient(colors: [rgb(0x2A2566), rgb(0x0C0B20)],
                                     startPoint: UnitPoint(x: 0.2, y: 0), endPoint: UnitPoint(x: 0.8, y: 1))
    static let glow = rgb(0x5A5CFF)
    /// Across the bars, from the first one's left edge to the last one's right edge.
    static let barGradient = LinearGradient(colors: [rgb(0x8A78FF), rgb(0x37C3FF)],
                                            startPoint: UnitPoint(x: bars[0].x - stroke / 2, y: 0.5),
                                            endPoint: UnitPoint(x: bars[bars.count - 1].x + stroke / 2, y: 0.5))

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
                let tile = Logo.frame(centredIn: rect, caretHeight: 14)
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

/// The voice bars of the mark, or its caret, drawn across the tile's frame.
struct LogoShape: Shape {
    var caret = false

    func path(in rect: CGRect) -> Path {
        var path = Path()
        for r in Logo.capsules(caret ? [Logo.caret] : Logo.bars, in: rect) {
            path.addRoundedRect(in: r, cornerSize: CGSize(width: r.width / 2, height: r.width / 2), style: .circular)
        }
        return path
    }
}

/// The app's tile: the mark on its deep indigo tile, the same artwork as the app icon.
struct AppTile: View {
    var size: CGFloat = 32

    var body: some View {
        let tile = RoundedRectangle(cornerRadius: size * 0.26, style: .continuous)
        ZStack {
            tile.fill(Logo.tile)
            tile.fill(RadialGradient(colors: [Logo.glow.opacity(0.38), Logo.glow.opacity(0)],
                                     center: UnitPoint(x: 0.40, y: 0.52), startRadius: 0, endRadius: size * 0.48))
            tile.strokeBorder(LinearGradient(stops: [.init(color: .white.opacity(0.22), location: 0),
                                                     .init(color: .white.opacity(0.04), location: 0.5),
                                                     .init(color: .white.opacity(0.08), location: 1)],
                                             startPoint: .top, endPoint: .bottom),
                              lineWidth: max(0.5, size * 0.006))
            LogoShape().fill(Logo.barGradient)
            LogoShape(caret: true).fill(.white)
        }
        .frame(width: size, height: size)
        .shadow(color: Color(red: 0.10, green: 0.08, blue: 0.30).opacity(0.35), radius: size * 0.12, y: size * 0.05)
    }
}

/// Coloured rounded square with a white SF Symbol, like System Settings.
struct IconBadge: View {
    let symbol: String
    let color: Color
    var size: CGFloat = 26

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.27, style: .continuous)
            .fill(LinearGradient(colors: [color.opacity(0.85), color], startPoint: .top, endPoint: .bottom))
            .frame(width: size, height: size)
            .overlay(
                Image(systemName: symbol)
                    .font(.system(size: size * 0.5, weight: .semibold))
                    .foregroundStyle(.white)
            )
    }
}

/// A grouped container with a hairline border.
struct Card<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) { content }
            .background(
                RoundedRectangle(cornerRadius: Theme.cardRadius, style: .continuous)
                    .fill(Color(nsColor: .controlBackgroundColor))
            )
            .overlay(
                RoundedRectangle(cornerRadius: Theme.cardRadius, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.08))
            )
            .shadow(color: .black.opacity(0.04), radius: 2, y: 1)
    }
}

struct CardSection<Content: View>: View {
    let title: String
    var footer: String? = nil
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.secondary)
                .padding(.leading, 4)
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

struct CardRow<Trailing: View>: View {
    var icon: String? = nil
    var iconColor: Color = .accentColor
    let title: String
    var subtitle: String? = nil
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 12) {
            if let icon { IconBadge(symbol: icon, color: iconColor, size: 24) }
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13))
                if let subtitle {
                    Text(subtitle)
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 12)
            trailing
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(minHeight: 44)
    }
}

extension CardRow where Trailing == RowSwitch {
    /// A row ending in an on/off switch.
    init(icon: String? = nil, iconColor: Color = .accentColor, title: String, subtitle: String? = nil, isOn: Binding<Bool>) {
        self.init(icon: icon, iconColor: iconColor, title: title, subtitle: subtitle) { RowSwitch(title: title, isOn: isOn) }
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

struct CardDivider: View {
    var inset: CGFloat = 14

    var body: some View {
        Rectangle().fill(Color.primary.opacity(0.07)).frame(height: 1).padding(.leading, inset)
    }
}

/// A keyboard key cap, drawn as a small piece of Liquid Glass.
struct KeyCap: View {
    let text: String
    var large = false

    var body: some View {
        Text(text)
            .font(.system(size: large ? 22 : 12, weight: .semibold, design: .rounded))
            .padding(.horizontal, large ? 18 : 7)
            .frame(minWidth: large ? 56 : 22, minHeight: large ? 50 : 22)
            .glassEffect(.regular, in: .rect(cornerRadius: large ? 14 : 6))
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

/// Tinted inline banner for warnings and hints.
struct Banner<Trailing: View>: View {
    let symbol: String
    let color: Color
    let text: String
    @ViewBuilder var trailing: Trailing

    var body: some View {
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
