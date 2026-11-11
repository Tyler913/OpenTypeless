import SwiftUI
import TypelessCore

/// The first page. It opens on how to dictate, then says what dictating has added up to: four figures side by side, a
/// year of activity and the spend, and the last few dictations. Before the first dictation there's nothing to count,
/// so it shows how the key works instead.
struct HomePage: View {
    @ObservedObject var settings: AppSettings
    @ObservedObject var navigation: SettingsNavigation
    @ObservedObject var usage = UsageStore.shared
    @ObservedObject var prices = PriceStore.shared
    @ObservedObject var history = HistoryStore.shared
    @ObservedObject private var permissions = PermissionsMonitor.shared

    var body: some View {
        let ledger = usage.ledger
        let today = CalendarDay(Date())
        let all = ledger.totals()
        let todayTotals = ledger.today(today)
        let month = ledger.month(today)
        let wpm = min(300, max(10, settings.typingWordsPerMinute))
        let streaks = ledger.streaks(today: today)

        PageScaffold(page: .home, maxWidth: Theme.dashboardWidth, showsTitle: false, form: false) {
            HomeHeadline(settings: settings)
            // What still stands in the way of a dictation, each with the page that fixes it.
            ForEach(SetupIssue.current(settings: settings, permissions: permissions)) { issue in
                Banner(symbol: "exclamationmark.triangle.fill", color: .orange, text: issue.text) {
                    Button(L("设置", "Fix")) { navigation.page = issue.page }.controlSize(.small)
                }
            }
            if ledger.isEmpty {
                // Before the first dictation: what the key does, instead of zeros and an empty grid.
                VStack(alignment: .leading, spacing: 8) {
                    DictationTips()
                    Text(L("按住 \(settings.hotkey.displayName) 说话，你的统计就会出现在这里。",
                           "Hold \(settings.hotkey.displayName) and talk. Your stats will show up here."))
                        .font(.system(size: 11.5))
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, 4)
                }
            } else {
                figures(all: all, todayTotals: todayTotals, month: month, wpm: wpm, streaks: streaks, ledger: ledger, today: today)
            }
            RecentDictations(history: history, navigation: navigation)
        }
        .task { prices.refreshIfStale(maxAge: 60 * 60) }
    }

    @ViewBuilder
    private func figures(all: UsageTotals, todayTotals: UsageTotals, month: UsageTotals, wpm: Int,
                         streaks: (current: Int, longest: Int), ledger: UsageLedger, today: CalendarDay) -> some View {
        let speed = all.speakingWordsPerMinute
        FiguresCard(
            hero: Stat(title: L("节省的时间", "Time saved"),
                       help: all.words > 0
                           ? L("说这 \(UsageFormat.count(all.words)) 个字用了 \(UsageFormat.duration(all.speakingSeconds))；按每分钟 \(wpm) 字打出来要 \(UsageFormat.duration(all.typingSeconds(wordsPerMinute: Double(wpm))))。打字速度可以在「通用」里修改。",
                               "Saying these \(UsageFormat.count(all.words)) words took \(UsageFormat.duration(all.speakingSeconds)); typing them at \(wpm) wpm would take \(UsageFormat.duration(all.typingSeconds(wordsPerMinute: Double(wpm)))). Change the typing speed under General.")
                           : nil,
                       value: UsageFormat.duration(all.savedSeconds(wordsPerMinute: Double(wpm))),
                       caption: L("按每分钟打 \(wpm) 字算", "vs. typing at \(wpm) wpm")),
            stats: [
                Stat(title: L("总字数", "Words dictated"), value: UsageFormat.count(all.words),
                     caption: L("今天 \(UsageFormat.count(todayTotals.words))", "\(UsageFormat.count(todayTotals.words)) today"),
                     tooltip: L("本月 \(UsageFormat.count(month.words))", "\(UsageFormat.count(month.words)) this month")),
                Stat(title: L("说话速度", "Speaking speed"),
                     value: speed.map { L("\(Int($0.rounded())) 字/分钟", "\(Int($0.rounded())) wpm") } ?? "—",
                     caption: speed.map {
                         let ratio = String(format: "%.1f", $0 / Double(wpm))
                         return L("是打字的 \(ratio) 倍", "\(ratio)× your typing speed")
                     } ?? L("说几句就能算出来", "Dictate a little to see it")),
                Stat(title: L("听写次数", "Dictations"), value: UsageFormat.count(all.dictations),
                     caption: streaks.current > 0
                         ? L("连续 \(streaks.current) 天 · 最长 \(streaks.longest) 天",
                             "\(streaks.current)-day streak · Best \(streaks.longest)")
                         : L("最长连续 \(streaks.longest) 天", "Longest streak \(streaks.longest) days")),
            ])

        // The activity grid takes two thirds of the width, the spend the rest.
        ProportionalRow(ratios: [2, 1], spacing: 12) {
            TitledCard(title: L("活跃度", "Activity")) {
                ActivityHeatmap(ledger: ledger, today: today)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 12)
            }
            TitledCard(title: L("花费", "Spend"), help: spendHelp) {
                SpendLine(title: L("今天", "Today"), totals: todayTotals)
                CardDivider()
                SpendLine(title: L("本月", "This month"), totals: month, limit: settings.activeSpendLimit)
                CardDivider()
                SpendLine(title: L("累计", "All time"), totals: all, showsSplit: true)
            }
        }
        if all.unpriced > 0 {
            Banner(symbol: "exclamationmark.triangle.fill", color: .orange,
                   text: L("有 \(all.unpriced) 次请求的模型没有价格，没有计入花费。",
                           "\(all.unpriced) requests used a model with no price, so they aren't in the spend.")) {
                Button(L("设置价格", "Set prices")) { navigation.page = .models }.controlSize(.small)
            }
        }
    }

    private var spendHelp: String {
        let catalog = prices.catalog
        let updated = catalog.isEmpty ? L("还没有下载", "not downloaded yet") : catalog.fetchedAt.shortStamp
        return L("OpenRouter 按每次请求实际扣费计算（价格表更新于 \(updated)）。其他服务商按你在「模型」里填写的价格计算。",
                 "OpenRouter requests count what OpenRouter billed for them (price list updated \(updated)). Other providers use the prices you set under Models.")
    }
}

/// What still stands between the user and a dictation, and the page that fixes it. Home and the menu-bar panel show the
/// same list.
struct SetupIssue: Identifiable {
    let text: String
    let page: SettingsPage

    var id: String { text }

    @MainActor
    static func current(settings: AppSettings, permissions: PermissionsMonitor) -> [SetupIssue] {
        var issues: [SetupIssue] = []
        if !settings.speechReady {
            issues.append(SetupIssue(text: L("还没有配置 \(settings.sttProvider.displayName) 的 API Key",
                                             "No API key for \(settings.sttProvider.displayName)"), page: .providers))
        }
        if !permissions.accessibilityGranted {
            issues.append(SetupIssue(text: L("需要辅助功能权限", "Accessibility permission needed"), page: .general))
        }
        if !permissions.microphoneGranted {
            issues.append(SetupIssue(text: L("需要麦克风权限", "Microphone permission needed"), page: .general))
        }
        return issues
    }
}

/// Home's title: how to dictate, with the shortcut's keys as they are on the keyboard, and the Scratchpad to try it in.
private struct HomeHeadline: View {
    @ObservedObject var settings: AppSettings

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 9) {
                    Text(L("按住", "Hold"))
                    KeyCaps(caps: settings.hotkey.keyCaps, large: true)
                    Text(L("说话", "and speak"))
                }
                .font(.system(size: 24, weight: .bold))
                .lineLimit(1)
                .accessibilityElement(children: .combine)
                .accessibilityAddTraits(.isHeader)
                Text(L("轻点一下进入免手持 · Esc 取消", "Tap once for hands-free · Esc to cancel"))
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 12)
            Button { ScratchpadWindowController.shared.show() } label: {
                Label(L("打开草稿本", "Open Scratchpad"), systemImage: ShortcutAction.openScratchpad.symbol)
            }
            .controlSize(.large)
        }
        .padding(.bottom, 4)
    }
}

/// What the dictation key does, in three rows: hold it, tap it, and Esc. The onboarding ends on the same card.
struct DictationTips: View {
    var body: some View {
        Card {
            CardRow(icon: "hand.point.down.fill", title: L("按住说话", "Hold to talk"),
                    subtitle: L("按住快捷键说话，松开后自动整理并插入到光标处",
                                "Hold the shortcut while you speak; release to clean up and insert")) { EmptyView() }
            CardDivider(inset: 50)
            CardRow(icon: "hand.tap.fill", title: L("轻点一下：免手持", "Tap once: hands-free"),
                    subtitle: L("适合长段口述，说完再按一次结束", "For long dictation — press again when done")) { EmptyView() }
            CardDivider(inset: 50)
            CardRow(icon: "escape", title: L("Esc 取消", "Esc to cancel"),
                    subtitle: L("录音或处理过程中随时取消", "Cancel any time while recording or processing")) { EmptyView() }
        }
    }
}

/// One headline figure: its label, the number, and a line of context.
private struct Stat: Identifiable {
    let title: String
    var help: String? = nil
    let value: String
    let caption: String
    /// More of the same figure, shown while the pointer rests on it.
    var tooltip: String? = nil

    var id: String { title }
}

/// What dictating has added up to, on one card: the time it saved in large figures, then the other three side by side
/// under a hairline, read across like a line of a table rather than as four boxes.
private struct FiguresCard: View {
    let hero: Stat
    let stats: [Stat]

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 5) {
                    Text(hero.title)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(.secondary)
                    if let help = hero.help { HelpTip(text: help, topic: hero.title) }
                }
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Text(hero.value)
                        .font(.system(size: 40, weight: .bold, design: .rounded))
                        .tracking(-0.5)
                        .monospacedDigit()
                        .lineLimit(1)
                        .minimumScaleFactor(0.5)
                    Text(hero.caption)
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.75)
                }
                .accessibilityElement(children: .combine)
            }
            .padding(.horizontal, 18)
            .padding(.top, 16)
            .padding(.bottom, 14)
            .frame(maxWidth: .infinity, alignment: .leading)

            Rectangle().fill(Theme.hairline).frame(height: 1).padding(.horizontal, 18)

            HStack(alignment: .top, spacing: 0) {
                ForEach(stats) { stat in StatCell(stat: stat) }
            }
            .padding(.horizontal, 2)
        }
    }
}

/// One of the smaller figures: the number, what it counts, and a line of context.
private struct StatCell: View {
    let stat: Stat

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(stat.value)
                .font(.system(size: 22, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.5)
            HStack(spacing: 5) {
                Text(stat.title)
                    .font(.system(size: 12, weight: .medium))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                if let help = stat.help { HelpTip(text: help, topic: stat.title) }
            }
            Text(stat.caption)
                .font(.system(size: 11.5))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .minimumScaleFactor(0.75)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .accessibilityElement(children: .combine)
        .help(stat.tooltip ?? "")
    }
}

/// The last few dictations, a line each: when, the text, how long it took to say. A click copies one; History has the rest.
private struct RecentDictations: View {
    @ObservedObject var history: HistoryStore
    @ObservedObject var navigation: SettingsNavigation
    @State private var copiedID: String?

    var body: some View {
        let records = Array(history.records.lazy.filter { !$0.outputText.isEmpty }.prefix(5))
        if !records.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .firstTextBaseline) {
                    SectionTitle(title: L("最近", "Recent"))
                    Spacer(minLength: 8)
                    Button { navigation.page = .history } label: {
                        HStack(spacing: 3) {
                            Text(SettingsPage.history.title)
                            Image(systemName: "chevron.right").font(.system(size: 9, weight: .semibold))
                        }
                        .font(.system(size: 12))
                        .foregroundStyle(.secondary)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .padding(.trailing, 4)
                }
                Card {
                    ForEach(Array(records.enumerated()), id: \.element.id) { index, record in
                        if index > 0 { CardDivider() }
                        Button { copy(record) } label: {
                            RecentDictationRow(record: record, copied: copiedID == record.id)
                        }
                        .buttonStyle(HoverButtonStyle(cornerRadius: 0))
                        .help(L("点击复制", "Click to copy"))
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: Theme.cardRadius, style: .continuous))
            }
        }
    }

    private func copy(_ record: DictationRecord) {
        TextInserter.copyToClipboard(record.outputText)
        copiedID = record.id
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
            if copiedID == record.id { copiedID = nil }
        }
    }
}

private struct RecentDictationRow: View {
    let record: DictationRecord
    let copied: Bool
    @State private var hovering = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(record.date.shortStamp)
                .font(.system(size: 11.5))
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
                .frame(width: 108, alignment: .leading)
            // One line: a dictation's own line breaks would otherwise end it at its first.
            Text(record.outputText.split(whereSeparator: \.isNewline).joined(separator: " "))
                .font(.system(size: 13))
                .foregroundStyle(.primary)
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(record.duration.durationLabel)
                .font(.system(size: 11.5))
                .monospacedDigit()
                .foregroundStyle(.tertiary)
            Image(systemName: copied ? "checkmark" : "doc.on.doc")
                .font(.system(size: 11))
                .foregroundStyle(copied ? Color.green : Color.secondary)
                .frame(width: 14)
                .opacity(copied || hovering ? 1 : 0)
                .accessibilityHidden(true)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .onHover { hovering = $0 }
    }
}

/// One line of the Spend card: label and amount, with the split between the two steps on a tooltip, or under it.
private struct SpendLine: View {
    let title: String
    let totals: UsageTotals
    var showsSplit = false
    /// The monthly spending limit, for this month's line: how much of it is spent, as a bar.
    var limit: Double? = nil

    private var split: String {
        L("转写 \(UsageFormat.money(totals.transcriptionCost)) · 整理 \(UsageFormat.money(totals.cleanupCost))",
          "Speech \(UsageFormat.money(totals.transcriptionCost)) · Clean-up \(UsageFormat.money(totals.cleanupCost))")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline) {
                Text(title).font(.system(size: 12)).foregroundStyle(.secondary)
                Spacer(minLength: 8)
                Text(UsageFormat.money(totals.cost))
                    .font(.system(size: 15, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            }
            if showsSplit, totals.cost > 0 {
                Text(split)
                    .font(.system(size: 10.5))
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let limit { LimitBar(spent: totals.cost, limit: limit).padding(.top, 4) }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .frame(maxWidth: .infinity, alignment: .leading)
        .help(totals.cost > 0 ? split : "")
    }
}

/// This month's spend against the monthly limit: a bar that turns orange at the reminder's share and red at the limit,
/// and how much of the limit that is. The menu-bar panel shows the same.
struct LimitBar: View {
    let spent: Double
    let limit: Double

    var body: some View {
        let level = SpendLimit.level(spent: spent, limit: limit)
        let percent = SpendLimit.percent(spent: spent, limit: limit)
        let cap = UsageFormat.money(limit)
        VStack(alignment: .leading, spacing: 3) {
            ProgressView(value: SpendLimit.progress(spent: spent, limit: limit))
                .progressViewStyle(.linear)
                .controlSize(.small)
                .tint(level == .reached ? Color.red : level == .warning ? Color.orange : Color.accentColor)
            Text(L("上限 \(cap) 的 \(percent)%", "\(percent)% of your \(cap) limit"))
                .font(.system(size: 10.5))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .accessibilityElement(children: .combine)
    }
}

/// A section title above a card that stretches to the height its row gives it (see `ProportionalRow`).
private struct TitledCard<Content: View>: View {
    let title: String
    var help: String? = nil
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle(title: title, help: help)
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
                let labels = DayLabels(AppLanguage.resolved)
                grid(columns, firstWeekday: firstWeekday, labels: labels)
                    .overlay(alignment: .topLeading) {
                        if let hovered, columns.indices.contains(hovered.week) {
                            bubble(for: columns[hovered.week][hovered.day], at: hovered, width: geometry.size.width,
                                   labels: labels)
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

    private func grid(_ columns: [[HeatmapCell]], firstWeekday: Int, labels: DayLabels) -> some View {
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
    private func bubble(for item: HeatmapCell, at spot: Spot, width: CGFloat, labels: DayLabels) -> some View {
        let x = labelWidth + CGFloat(spot.week) * (cell + gap) + cell / 2
        let y = monthRow + CGFloat(spot.day) * (cell + gap) + cell / 2
        return Text(Self.tooltip(item, labels: labels))
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
        guard item.dictations > 0 else { return L("\(date)：没有听写", "\(date): no dictation") }
        let words = UsageFormat.count(item.words)
        return item.dictations == 1
            ? L("\(date)：1 次听写 · \(words) 字", "\(date): 1 dictation · \(words) words")
            : L("\(date)：\(item.dictations) 次听写 · \(words) 字", "\(date): \(item.dictations) dictations · \(words) words")
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
