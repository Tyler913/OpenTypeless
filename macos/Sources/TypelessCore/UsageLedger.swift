import Foundation

/// Counts words the way a reader would across languages: every Chinese character and Japanese kana is a word, and
/// every run of letters or digits in other scripts is one word ("don't", "e-mail" and "3.5" count once).
public enum WordCount {
    public static func count(_ text: String) -> Int {
        let scalars = Array(text.unicodeScalars)
        var count = 0
        var inWord = false
        for (i, scalar) in scalars.enumerated() {
            if isCJKWordCharacter(scalar.value) {
                count += 1
                inWord = false
            } else if isWordCharacter(scalar) {
                if !inWord { count += 1 }
                inWord = true
            } else if inWord, isJoiner(scalar), i + 1 < scalars.count, isWordCharacter(scalars[i + 1]),
                      !isCJKWordCharacter(scalars[i + 1].value) {
                // Inside a word: "don't", "e-mail", "3.5", "1,000".
            } else {
                inWord = false
            }
        }
        return count
    }

    /// Han ideographs and Japanese kana, where each character is a word. (Korean separates words with spaces.)
    public static func isCJKWordCharacter(_ scalar: UInt32) -> Bool {
        switch scalar {
        case 0x4E00...0x9FFF,      // CJK Unified Ideographs
             0x3400...0x4DBF,      // Extension A
             0x20000...0x3134F,    // Extensions B–G
             0xF900...0xFAFF,      // Compatibility Ideographs
             0x3040...0x309F,      // Hiragana
             0x30A0...0x30FF:      // Katakana
            return true
        default:
            return false
        }
    }

    private static func isWordCharacter(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter,
             .nonspacingMark, .spacingMark, .enclosingMark,
             .decimalNumber, .letterNumber, .otherNumber:
            return true
        default:
            return false
        }
    }

    private static func isJoiner(_ scalar: Unicode.Scalar) -> Bool {
        ["'", "\u{2019}", "-", ".", ",", "_"].contains(scalar)
    }
}

/// A calendar date with no time or time zone, e.g. the local day a dictation happened on.
public struct CalendarDay: Hashable, Comparable, Sendable, CustomStringConvertible {
    public let year: Int
    public let month: Int
    public let day: Int

    public init(year: Int, month: Int, day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    /// The day `date` falls on in `calendar`'s time zone (the user's, by default).
    public init(_ date: Date, calendar: Calendar = .current) {
        let parts = calendar.dateComponents([.year, .month, .day], from: date)
        self.init(year: parts.year ?? 1970, month: parts.month ?? 1, day: parts.day ?? 1)
    }

    /// Day arithmetic happens in UTC, where every day is 24 hours.
    private static let utc: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar
    }()

    private var utcDate: Date { Self.utc.date(from: DateComponents(year: year, month: month, day: day))! }

    public func adding(days: Int) -> CalendarDay {
        CalendarDay(Self.utc.date(byAdding: .day, value: days, to: utcDate)!, calendar: Self.utc)
    }

    /// 0 = Sunday … 6 = Saturday.
    public var weekday: Int { Self.utc.component(.weekday, from: utcDate) - 1 }

    public var firstOfMonth: CalendarDay { CalendarDay(year: year, month: month, day: 1) }

    /// Midnight at the start of this day in `calendar`'s time zone, for formatting.
    public func date(in calendar: Calendar = .current) -> Date {
        calendar.date(from: DateComponents(year: year, month: month, day: day)) ?? utcDate
    }

    /// "2026-09-28"
    public var key: String { String(format: "%04d-%02d-%02d", year, month, day) }

    public init?(key: String) {
        let parts = key.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3, (1...12).contains(parts[1]), (1...31).contains(parts[2]) else { return nil }
        self.init(year: parts[0], month: parts[1], day: parts[2])
    }

    public var description: String { key }

    public static func < (a: CalendarDay, b: CalendarDay) -> Bool { (a.year, a.month, a.day) < (b.year, b.month, b.day) }
}

/// One day's dictation: what was said, how long it took to say, and what it cost.
public struct DayUsage: Codable, Equatable, Sendable {
    public var words = 0
    public var dictations = 0
    /// Recording time, pauses included.
    public var speakingSeconds: Double = 0
    public var transcriptionCost: Double = 0
    public var cleanupCost: Double = 0
    /// Requests with no known price (a model without a price entered), so the cost shown is a lower bound.
    public var unpriced = 0

    enum CodingKeys: String, CodingKey {
        case words, dictations, speakingSeconds = "seconds", transcriptionCost = "sttCost", cleanupCost = "polishCost", unpriced
    }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        words = try c.decodeIfPresent(Int.self, forKey: .words) ?? 0
        dictations = try c.decodeIfPresent(Int.self, forKey: .dictations) ?? 0
        speakingSeconds = try c.decodeIfPresent(Double.self, forKey: .speakingSeconds) ?? 0
        transcriptionCost = try c.decodeIfPresent(Double.self, forKey: .transcriptionCost) ?? 0
        cleanupCost = try c.decodeIfPresent(Double.self, forKey: .cleanupCost) ?? 0
        unpriced = try c.decodeIfPresent(Int.self, forKey: .unpriced) ?? 0
    }
}

/// Totals over a range of days, with the figures the Home page shows.
public struct UsageTotals: Equatable, Sendable {
    /// Typing speed the time saved is measured against, in words per minute.
    public static let defaultTypingWordsPerMinute = 100

    public var words = 0
    public var dictations = 0
    public var speakingSeconds: Double = 0
    public var transcriptionCost: Double = 0
    public var cleanupCost: Double = 0
    public var unpriced = 0

    public init(words: Int = 0, dictations: Int = 0, speakingSeconds: Double = 0, transcriptionCost: Double = 0,
                cleanupCost: Double = 0, unpriced: Int = 0) {
        self.words = words
        self.dictations = dictations
        self.speakingSeconds = speakingSeconds
        self.transcriptionCost = transcriptionCost
        self.cleanupCost = cleanupCost
        self.unpriced = unpriced
    }

    public var cost: Double { transcriptionCost + cleanupCost }

    /// How long typing the same words would have taken.
    public func typingSeconds(wordsPerMinute: Double) -> Double {
        wordsPerMinute > 0 ? Double(words) / wordsPerMinute * 60 : 0
    }

    /// Typing time minus speaking time (never negative).
    public func savedSeconds(wordsPerMinute: Double) -> Double {
        max(0, typingSeconds(wordsPerMinute: wordsPerMinute) - speakingSeconds)
    }

    /// Words per minute while recording, once there's at least a second of it.
    public var speakingWordsPerMinute: Double? {
        speakingSeconds >= 1 && words > 0 ? Double(words) / (speakingSeconds / 60) : nil
    }
}

public struct HeatmapCell: Equatable, Sendable {
    public let date: CalendarDay
    public let words: Int
    public let dictations: Int
    public let level: Int
    public let isFuture: Bool
}

/// Daily totals of words, speaking time and spend since the app was first used. Kept in its own file
/// (`usage.json`) rather than derived from History, which drops old dictations and lets the user delete them.
public struct UsageLedger: Equatable, Sendable {
    public static let version = 1

    public private(set) var days: [String: DayUsage] = [:]

    public init() {}

    public var isEmpty: Bool { days.isEmpty }

    public func day(_ day: CalendarDay) -> DayUsage? { days[day.key] }

    /// A finished dictation (counted once, however often it's re-transcribed).
    public mutating func addDictation(on day: CalendarDay, words: Int, seconds: Double) {
        days[day.key, default: DayUsage()].words += max(0, words)
        days[day.key, default: DayUsage()].dictations += 1
        days[day.key, default: DayUsage()].speakingSeconds += max(0, seconds)
    }

    /// What the requests of one processing run cost (a retry costs again, and is counted again).
    public mutating func addCost(on day: CalendarDay, transcription: Double, cleanup: Double, unpriced: Int) {
        guard transcription > 0 || cleanup > 0 || unpriced > 0 else { return }
        days[day.key, default: DayUsage()].transcriptionCost += max(0, transcription)
        days[day.key, default: DayUsage()].cleanupCost += max(0, cleanup)
        days[day.key, default: DayUsage()].unpriced += max(0, unpriced)
    }

    /// Totals from `from` to `to`, inclusive; nil ends are open.
    public func totals(from: CalendarDay? = nil, to: CalendarDay? = nil) -> UsageTotals {
        var totals = UsageTotals()
        for (key, day) in days {
            if let from, key < from.key { continue }
            if let to, key > to.key { continue }
            totals.words += day.words
            totals.dictations += day.dictations
            totals.speakingSeconds += day.speakingSeconds
            totals.transcriptionCost += day.transcriptionCost
            totals.cleanupCost += day.cleanupCost
            totals.unpriced += day.unpriced
        }
        return totals
    }

    public func today(_ today: CalendarDay) -> UsageTotals { totals(from: today, to: today) }

    public func month(_ today: CalendarDay) -> UsageTotals { totals(from: today.firstOfMonth, to: today) }

    /// The current run of days with dictation (still alive when today has none yet, like GitHub's), and the longest.
    public func streaks(today: CalendarDay) -> (current: Int, longest: Int) {
        func active(_ day: CalendarDay) -> Bool { (self.day(day)?.dictations ?? 0) > 0 }
        var current = 0
        var cursor = active(today) ? today : today.adding(days: -1)
        while active(cursor) {
            current += 1
            cursor = cursor.adding(days: -1)
        }
        var longest = 0
        var run = 0
        var previous: CalendarDay?
        for key in days.keys.sorted() {
            guard (days[key]?.dictations ?? 0) > 0, let date = CalendarDay(key: key) else { continue }
            run = previous.map { $0.adding(days: 1) == date } == true ? run + 1 : 1
            longest = max(longest, run)
            previous = date
        }
        return (current, max(longest, current))
    }

    /// GitHub-style activity grid: `weeks` columns of seven days ending with the week that holds `today`, each week
    /// starting on `firstWeekday` (0 = Sunday). Level 0 means no dictation; 1–4 are the quartiles of the busy days
    /// shown, so the shading adapts to how much you talk.
    public func heatmap(today: CalendarDay, weeks: Int, firstWeekday: Int) -> [[HeatmapCell]] {
        let weeks = max(1, weeks)
        let offset = ((today.weekday - firstWeekday % 7) + 7) % 7
        let start = today.adding(days: -offset - 7 * (weeks - 1))
        let dates = (0..<(weeks * 7)).map { start.adding(days: $0) }
        let words = dates.map { day($0)?.words ?? 0 }
        let thresholds = Self.thresholds(zip(dates, words).filter { $0.0 <= today }.map(\.1))
        return (0..<weeks).map { week in
            (0..<7).map { weekday in
                let index = week * 7 + weekday
                return HeatmapCell(date: dates[index], words: words[index], dictations: day(dates[index])?.dictations ?? 0,
                                   level: Self.level(words[index], thresholds: thresholds), isFuture: dates[index] > today)
            }
        }
    }

    /// Upper bounds of levels 1–3: the 25th, 50th and 75th percentile of the non-zero counts.
    public static func thresholds(_ counts: [Int]) -> [Int] {
        let busy = counts.filter { $0 > 0 }.sorted()
        guard !busy.isEmpty else { return [0, 0, 0] }
        func at(_ p: Double) -> Int { busy[Int((p * Double(busy.count - 1)).rounded(.down))] }
        return [at(0.25), at(0.5), at(0.75)]
    }

    public static func level(_ words: Int, thresholds: [Int]) -> Int {
        if words <= 0 { return 0 }
        if words <= thresholds[0] { return 1 }
        if words <= thresholds[1] { return 2 }
        if words <= thresholds[2] { return 3 }
        return 4
    }

    // MARK: - Storage

    private struct Stored: Codable {
        var version = UsageLedger.version
        var days: [String: DayUsage]
    }

    public func jsonData() -> Data? {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try? encoder.encode(Stored(days: days))
    }

    /// An unreadable file gives an empty ledger rather than blocking the app.
    public init(jsonData: Data) {
        let stored = try? JSONDecoder().decode(Stored.self, from: jsonData)
        days = (stored?.days ?? [:]).filter { CalendarDay(key: $0.key) != nil }
    }

    /// Nil when there's no file yet (first run of a version that keeps it).
    public static func load(from url: URL) -> UsageLedger? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        guard let data = try? Data(contentsOf: url) else { return UsageLedger() }
        return UsageLedger(jsonData: data)
    }

    public func save(to url: URL) {
        guard let data = jsonData() else { return }
        try? data.write(to: url, options: .atomic)
    }
}
