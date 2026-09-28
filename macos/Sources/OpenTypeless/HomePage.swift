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

            CardSection(title: L("花费", "Spend"), footer: spendFooter) {
                SpendRow(symbol: "sun.max.fill", color: .blue, title: L("今天", "Today"), totals: todayTotals)
                CardDivider(inset: 50)
                SpendRow(symbol: "calendar", color: .teal, title: L("本月", "This month"), totals: month)
                CardDivider(inset: 50)
                SpendRow(symbol: "dollarsign", color: .green, title: L("累计", "All time"), totals: all)
            }
            if all.unpriced > 0 {
                Banner(symbol: "exclamationmark.triangle.fill", color: .orange,
                       text: L("有 \(all.unpriced) 次请求的模型没有价格，没有计入花费。",
                               "\(all.unpriced) requests used a model with no price, so they aren't in the spend.")) {
                    Button(L("设置价格", "Set prices")) { navigation.page = .models }.controlSize(.small)
                }
            }

            CardSection(title: L("活跃度", "Activity"),
                        footer: L("颜色越深，那天说得越多。", "The darker the square, the more you dictated that day.")) {
                ActivityHeatmap(ledger: ledger, today: today)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 12)
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

private struct SpendRow: View {
    let symbol: String
    let color: Color
    let title: String
    let totals: UsageTotals

    var body: some View {
        CardRow(icon: symbol, iconColor: color, title: title,
                subtitle: totals.cost > 0
                    ? L("语音转文字 \(UsageFormat.money(totals.transcriptionCost)) · 文字整理 \(UsageFormat.money(totals.cleanupCost))",
                        "Speech-to-text \(UsageFormat.money(totals.transcriptionCost)) · Clean-up \(UsageFormat.money(totals.cleanupCost))")
                    : nil) {
            Text(UsageFormat.money(totals.cost)).font(.system(size: 15, weight: .semibold)).monospacedDigit()
        }
    }
}

/// GitHub-style grid of the last weeks: one column per week, one square per day, shaded by how much was dictated.
/// As many weeks as fit the width are shown, up to a year.
struct ActivityHeatmap: View {
    let ledger: UsageLedger
    let today: CalendarDay

    private let cell: CGFloat = 11
    private let gap: CGFloat = 3
    private let labelWidth: CGFloat = 30
    private let monthRow: CGFloat = 14

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            GeometryReader { geometry in
                grid(weeks: max(4, min(53, Int((geometry.size.width - labelWidth) / (cell + gap)))))
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

    private func grid(weeks: Int) -> some View {
        let firstWeekday = Calendar.current.firstWeekday - 1
        let columns = ledger.heatmap(today: today, weeks: weeks, firstWeekday: firstWeekday)
        let labels = DayLabels(AppLanguage.resolved)
        let months = monthLabels(columns, labels: labels)
        return HStack(alignment: .top, spacing: gap) {
            VStack(alignment: .leading, spacing: gap) {
                Color.clear.frame(height: monthRow - gap)
                ForEach(0..<7) { row in
                    Text(row % 2 == 1 ? labels.weekday((firstWeekday + row) % 7) : "")
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
                            .help(item.isFuture ? "" : Self.tooltip(item, labels: labels))
                    }
                }
            }
        }
    }

    /// Month names above the first week that starts in that month (skipped when too close to the edge).
    private func monthLabels(_ columns: [[HeatmapCell]], labels names: DayLabels) -> [Int: String] {
        var labels: [Int: String] = [:]
        var lastMonth = -1
        for (week, column) in columns.enumerated() {
            let first = column[0].date
            guard first.month != lastMonth, week < columns.count - 2 else { continue }
            if lastMonth != -1 || first.day <= 7 { labels[week] = names.month(first.month) }
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

    static func tooltip(_ item: HeatmapCell, labels: DayLabels) -> String {
        let date = labels.date(item.date)
        return item.words > 0
            ? L("\(date)：\(UsageFormat.count(item.words)) 字", "\(date): \(UsageFormat.count(item.words)) words")
            : L("\(date)：没有听写", "\(date): no dictation")
    }
}

/// Heatmap labels in the UI language: "Sep" / "9月", "Mon" / "周一", "Sep 28" / "9月28日".
struct DayLabels {
    private let formatter = DateFormatter()

    init(_ language: AppLanguage) {
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = language.locale
        formatter.setLocalizedDateFormatFromTemplate("MMMd")
    }

    func month(_ month: Int) -> String { formatter.shortStandaloneMonthSymbols[month - 1] }

    func weekday(_ weekday: Int) -> String { formatter.shortStandaloneWeekdaySymbols[weekday] }

    func date(_ day: CalendarDay) -> String {
        let noon = DateComponents(year: day.year, month: day.month, day: day.day, hour: 12)
        return formatter.calendar.date(from: noon).map(formatter.string) ?? day.description
    }
}
