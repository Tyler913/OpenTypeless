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

/// The app's gradient tile with the waveform glyph.
struct AppTile: View {
    var size: CGFloat = 32

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.26, style: .continuous)
            .fill(Theme.brand)
            .frame(width: size, height: size)
            .overlay(
                Image(systemName: "waveform")
                    .font(.system(size: size * 0.5, weight: .semibold))
                    .foregroundStyle(.white)
            )
            .shadow(color: Color(red: 0.3, green: 0.4, blue: 0.95).opacity(0.35), radius: size * 0.12, y: size * 0.05)
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

extension Date {
    /// "14:32" today, "Yesterday 09:10", otherwise "9/26 18:02".
    var shortStamp: String {
        let calendar = Calendar.current
        let time = formatted(date: .omitted, time: .shortened)
        if calendar.isDateInToday(self) { return time }
        if calendar.isDateInYesterday(self) { return L("昨天", "Yesterday") + " " + time }
        return formatted(.dateTime.month(.defaultDigits).day()) + " " + time
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
