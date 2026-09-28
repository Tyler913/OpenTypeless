import SwiftUI
import TypelessCore

/// The first page: how much you've dictated, the time that saved over typing, what it cost, and a year of activity.
struct HomePage: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject var navigation: SettingsNavigation
    @ObservedObject var usage = UsageStore.shared
    @ObservedObject var prices = PriceStore.shared

    var body: some View {
        let ledger = usage.ledger
        let today = CalendarDay(Date())
        let all = ledger.totals()
        let todayTotals = ledger.today(today)
        let month = ledger.month(today)
        let wpm = min(300, max(10, settings.typingWordsPerMinute))
        let streaks = ledger.streaks(today: today)

        PageScaffold(page: .home) {
            if ledger.isEmpty {
                Banner(symbol: "info.circle.fill", color: .blue,
                       text: L("按住 \(settings.hotkey.displayName) 说话，你的统计就会出现在这里。",
                               "Hold \(settings.hotkey.displayName) and talk. Your stats will show up here.")) { EmptyView() }
            }

            VStack(alignment: .leading, spacing: 8) {
                Grid(horizontalSpacing: 12, verticalSpacing: 12) {
                    GridRow {
                        StatTile(symbol: "textformat", color: .indigo, title: L("总字数", "Words dictated"),
                                 value: UsageFormat.count(all.words),
                                 caption: L("今天 \(UsageFormat.count(todayTotals.words)) · 本月 \(UsageFormat.count(month.words))",
                                            "Today \(UsageFormat.count(todayTotals.words)) · This month \(UsageFormat.count(month.words))"))
                        StatTile(symbol: "stopwatch.fill", color: .green, title: L("节省的时间", "Time saved"),
                                 value: UsageFormat.duration(all.savedSeconds(wordsPerMinute: Double(wpm))),
                                 caption: L("和每分钟打 \(wpm) 字相比", "Compared with typing at \(wpm) wpm"))
                    }
                    GridRow {
                        let speed = all.speakingWordsPerMinute
                        StatTile(symbol: "waveform", color: .orange, title: L("说话速度", "Speaking speed"),
                                 value: speed.map { L("\(Int($0.rounded())) 字/分钟", "\(Int($0.rounded())) wpm") } ?? "—",
                                 caption: speed.map {
                                     let ratio = String(format: "%.1f", $0 / Double(wpm))
                                     return L("是打字的 \(ratio) 倍", "\(ratio)× your typing speed")
                                 } ?? L("说几句就能算出来", "Dictate a little to see it"))
                        StatTile(symbol: "calendar", color: .purple, title: L("听写次数", "Dictations"),
                                 value: UsageFormat.count(all.dictations),
                                 caption: streaks.current > 0
                                     ? L("已连续 \(streaks.current) 天 · 最长 \(streaks.longest) 天",
                                         "\(streaks.current)-day streak · Longest \(streaks.longest) days")
                                     : L("最长连续 \(streaks.longest) 天", "Longest streak \(streaks.longest) days"))
                    }
                }
                if all.words > 0 {
                    Text(L("说这 \(UsageFormat.count(all.words)) 个字用了 \(UsageFormat.duration(all.speakingSeconds))；按每分钟 \(wpm) 字打出来要 \(UsageFormat.duration(all.typingSeconds(wordsPerMinute: Double(wpm))))。打字速度可以在「通用」里修改。",
                           "Saying these \(UsageFormat.count(all.words)) words took \(UsageFormat.duration(all.speakingSeconds)); typing them at \(wpm) wpm would take \(UsageFormat.duration(all.typingSeconds(wordsPerMinute: Double(wpm)))). Change the typing speed under General."))
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, 4)
                }
            }

            // Spend takes a third of the width, the activity grid the rest.
            VStack(alignment: .leading, spacing: 8) {
                ProportionalRow(ratios: [1, 2], spacing: 14) {
                    TitledCard(title: L("花费", "Spend")) {
                        SpendLine(title: L("今天", "Today"), totals: todayTotals)
                        CardDivider(inset: 14)
                        SpendLine(title: L("本月", "This month"), totals: month)
                        CardDivider(inset: 14)
                        SpendLine(title: L("累计", "All time"), totals: all)
                    }
                    TitledCard(title: L("活跃度", "Activity")) {
                        ActivityHeatmap(ledger: ledger, today: today)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 12)
                    }
                }
                Text(spendFooter)
                    .font(.system(size: 11.5))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 4)
            }
            if all.unpriced > 0 {
                Banner(symbol: "exclamationmark.triangle.fill", color: .orange,
                       text: L("有 \(all.unpriced) 次请求的模型没有价格，没有计入花费。",
                               "\(all.unpriced) requests used a model with no price, so they aren't in the spend.")) {
                    Button(L("设置价格", "Set prices")) { navigation.page = .models }.controlSize(.small)
                }
            }
        }
        .task { prices.refreshIfStale(maxAge: 60 * 60) }
    }

    private var spendFooter: String {
        let catalog = prices.catalog
        let updated = catalog.isEmpty ? L("还没有下载", "not downloaded yet") : catalog.fetchedAt.shortStamp
        return L("OpenRouter 按每次请求实际扣费计算（价格表更新于 \(updated)）。其他服务商按你在「模型」里填写的价格计算。",
                 "OpenRouter requests count what OpenRouter billed for them (price list updated \(updated)). Other providers use the prices you set under Models.")
    }
}

/// One headline figure: an icon and label, the number, and a line of context.
private struct StatTile: View {
    let symbol: String
    let color: Color
    let title: String
    let value: String
    let caption: String

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    IconBadge(symbol: symbol, color: color, size: 22)
                    Text(title).font(.system(size: 12.5)).foregroundStyle(.secondary)
                }
                Text(value)
                    .font(.system(size: 28, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                Text(caption)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

/// One spend figure in the narrow Spend column: label, amount, and the split between the two steps.
private struct SpendLine: View {
    let title: String
    let totals: UsageTotals

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.system(size: 11.5)).foregroundStyle(.secondary)
            Text(UsageFormat.money(totals.cost))
                .font(.system(size: 20, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.6)
            if totals.cost > 0 {
                Text(L("转写 \(UsageFormat.money(totals.transcriptionCost)) · 整理 \(UsageFormat.money(totals.cleanupCost))",
                       "Speech \(UsageFormat.money(totals.transcriptionCost)) · Clean-up \(UsageFormat.money(totals.cleanupCost))"))
                    .font(.system(size: 10.5))
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A section title above a card that stretches to the height its row gives it (see `ProportionalRow`).
private struct TitledCard<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.secondary)
                .padding(.leading, 4)
            Card {
                VStack(alignment: .leading, spacing: 0) { content }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
    }
}

/// Children side by side with widths in proportion (1 : 2 gives a third and two thirds), all as tall as the tallest.
struct ProportionalRow: Layout {
    var ratios: [CGFloat]
    var spacing: CGFloat

    private func widths(_ total: CGFloat, count: Int) -> [CGFloat] {
        guard count > 0 else { return [] }
        let available = max(0, total - spacing * CGFloat(count - 1))
        let parts = (0..<count).map { $0 < ratios.count ? ratios[$0] : 1 }
        let sum = parts.reduce(0, +)
        return parts.map { available * $0 / sum }
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 600
        let heights = zip(subviews, widths(width, count: subviews.count)).map { subview, width in
            subview.sizeThatFits(ProposedViewSize(width: width, height: nil)).height
        }
        return CGSize(width: width, height: heights.max() ?? 0)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX
        for (subview, width) in zip(subviews, widths(bounds.width, count: subviews.count)) {
            subview.place(at: CGPoint(x: x, y: bounds.minY), anchor: .topLeading,
                          proposal: ProposedViewSize(width: width, height: bounds.height))
            x += width + spacing
        }
    }
}

/// GitHub-style grid of the last weeks: one column per week, one square per day, shaded by how much was dictated.
/// As many weeks as fit the width are shown, up to a year.
struct ActivityHeatmap: View {
    let ledger: UsageLedger
    let today: CalendarDay
    /// The square under the pointer, whose day is shown in a bubble right away (tooltips take a second and are
    /// easy to miss on squares this small).
    @State private var hovered: Spot?

    private struct Spot: Equatable {
        let week: Int
        let day: Int
    }

    private let cell: CGFloat = 11
    private let gap: CGFloat = 3
    private let labelWidth: CGFloat = 30
    private let monthRow: CGFloat = 14

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            GeometryReader { geometry in
                let weeks = max(4, min(53, Int((geometry.size.width - labelWidth) / (cell + gap))))
                let firstWeekday = Calendar.current.firstWeekday - 1
                let columns = ledger.heatmap(today: today, weeks: weeks, firstWeekday: firstWeekday)
                grid(columns, firstWeekday: firstWeekday)
                    .overlay(alignment: .topLeading) {
                        if let hovered, columns.indices.contains(hovered.week) {
                            bubble(for: columns[hovered.week][hovered.day], at: hovered, width: geometry.size.width)
                        }
                    }
            }
            .frame(height: monthRow + 7 * cell + 6 * gap)
            HStack(spacing: 3) {
                Spacer()
                Text(L("少", "Less")).font(.system(size: 10)).foregroundStyle(.secondary).padding(.trailing, 3)
                ForEach(0..<5) { level in
                    RoundedRectangle(cornerRadius: 2.5).fill(Self.shade(level)).frame(width: cell, height: cell)
                }
                Text(L("多", "More")).font(.system(size: 10)).foregroundStyle(.secondary).padding(.leading, 3)
            }
        }
    }

    private func grid(_ columns: [[HeatmapCell]], firstWeekday: Int) -> some View {
        let months = monthLabels(columns)
        return HStack(alignment: .top, spacing: gap) {
            VStack(alignment: .leading, spacing: gap) {
                Color.clear.frame(height: monthRow - gap)
                ForEach(0..<7) { row in
                    Text(row % 2 == 1 ? Self.weekdayName((firstWeekday + row) % 7) : "")
                        .font(.system(size: 9.5))
                        .foregroundStyle(.secondary)
                        .frame(height: cell)
                }
            }
            .frame(width: labelWidth - gap, alignment: .leading)
            ForEach(columns.indices, id: \.self) { week in
                VStack(alignment: .leading, spacing: gap) {
                    Text(months[week] ?? "")
                        .font(.system(size: 9.5))
                        .foregroundStyle(.secondary)
                        .fixedSize()
                        .frame(width: cell, height: monthRow - gap, alignment: .topLeading)
                    ForEach(0..<7) { day in
                        let item = columns[week][day]
                        RoundedRectangle(cornerRadius: 2.5)
                            .fill(item.isFuture ? Color.clear : Self.shade(item.level))
                            .frame(width: cell, height: cell)
                            .onHover { inside in
                                let spot = Spot(week: week, day: day)
                                if inside, !item.isFuture { hovered = spot } else if hovered == spot { hovered = nil }
                            }
                    }
                }
            }
        }
    }

    /// The hovered day's date, dictations and words, above the square (below it in the top rows).
    private func bubble(for item: HeatmapCell, at spot: Spot, width: CGFloat) -> some View {
        let x = labelWidth + CGFloat(spot.week) * (cell + gap) + cell / 2
        let y = monthRow + CGFloat(spot.day) * (cell + gap) + cell / 2
        return Text(Self.tooltip(item))
            .font(.system(size: 11, weight: .medium))
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(.regularMaterial))
            .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(Color.primary.opacity(0.1)))
            .shadow(color: .black.opacity(0.15), radius: 4, y: 1)
            .fixedSize()
            .position(x: min(max(x, 95), max(95, width - 95)), y: spot.day < 3 ? y + 22 : y - 22)
            .allowsHitTesting(false)
    }

    /// Month names above the first week that starts in that month (skipped when too close to the edge).
    private func monthLabels(_ columns: [[HeatmapCell]]) -> [Int: String] {
        var labels: [Int: String] = [:]
        var lastMonth = -1
        for (week, column) in columns.enumerated() {
            let first = column[0].date
            guard first.month != lastMonth, week < columns.count - 2 else { continue }
            if lastMonth != -1 || first.day <= 7 { labels[week] = Self.monthName(first.month) }
            lastMonth = first.month
        }
        return labels
    }

    /// Empty days in neutral grey, busier days in deeper shades of the accent colour.
    static func shade(_ level: Int) -> Color {
        switch level {
        case 0: return Color.gray.opacity(0.18)
        case 1: return Color.accentColor.opacity(0.3)
        case 2: return Color.accentColor.opacity(0.5)
        case 3: return Color.accentColor.opacity(0.75)
        default: return Color.accentColor
        }
    }

    private static let englishMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]

    static func monthName(_ month: Int) -> String { L("\(month)月", englishMonths[month - 1]) }

    static func weekdayName(_ weekday: Int) -> String {
        L("周" + String(Array("日一二三四五六")[weekday]), ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][weekday])
    }

    static func tooltip(_ item: HeatmapCell) -> String {
        let date = L("\(item.date.month)月\(item.date.day)日", "\(monthName(item.date.month)) \(item.date.day)")
        guard item.dictations > 0 else { return L("\(date)：没有听写", "\(date): no dictation") }
        return L("\(date)：\(item.dictations) 次听写 · \(UsageFormat.count(item.words)) 字",
                 "\(date): \(item.dictations) \(item.dictations == 1 ? "dictation" : "dictations") · \(UsageFormat.count(item.words)) words")
    }
}
