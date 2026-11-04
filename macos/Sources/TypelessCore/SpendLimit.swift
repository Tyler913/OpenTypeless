import Foundation

/// The monthly spending limit (set on the Models page, off by default): a reminder when this calendar month's spend, as
/// the Home page counts it, first reaches `warningFraction` of the limit, and another when it reaches the limit. Only a
/// reminder: dictation never stops for it, and nothing is dropped. Each reminder comes at most once a month; what has
/// been shown is saved (`State`), a new month starts over, and a limit raised above what has been spent arms its
/// reminders again.
public enum SpendLimit {
    /// The share of the limit at which the first reminder comes.
    public static let warningFraction = 0.8

    /// How far the month's spend has gone towards the limit, in order.
    public enum Level: Int, Comparable, Sendable {
        case none = 0
        /// `warningFraction` of the limit or more.
        case warning = 1
        /// The limit or more.
        case reached = 2

        public static func < (a: Level, b: Level) -> Bool { a.rawValue < b.rawValue }

        /// The name it's saved under.
        public var name: String {
            switch self {
            case .none: return "none"
            case .warning: return "warning"
            case .reached: return "reached"
            }
        }

        public init?(name: String) {
            switch name {
            case "none": self = .none
            case "warning": self = .warning
            case "reached": self = .reached
            default: return nil
            }
        }
    }

    /// Where `spent` stands against `limit`. `.none` for a limit that isn't a positive number (no limit).
    public static func level(spent: Double, limit: Double) -> Level {
        guard limit.isFinite, limit > 0, !spent.isNaN else { return .none }
        if spent >= limit { return .reached }
        if spent >= limit * warningFraction { return .warning }
        return .none
    }

    /// The share of the limit spent, from 0 to 1, for a progress bar; 0 without a limit.
    public static func progress(spent: Double, limit: Double) -> Double {
        guard limit.isFinite, limit > 0, spent.isFinite, spent > 0 else { return 0 }
        return min(1, spent / limit)
    }

    /// The share of the limit spent in whole percent, rounded down (so 79.9 % doesn't read 80 % before the reminder
    /// says so) and held at a million; 0 without a limit.
    public static func percent(spent: Double, limit: Double) -> Int {
        guard limit.isFinite, limit > 0, spent.isFinite, spent > 0 else { return 0 }
        return Int(min(1_000_000, (spent / limit * 100).rounded(.down)))
    }

    /// The month a day falls in, as `State` keeps it: "2026-11".
    public static func monthKey(_ day: CalendarDay) -> String { String(day.key.prefix(7)) }

    /// Which reminder this month has had: saved with the settings, so each comes at most once a month.
    public struct State: Equatable, Sendable {
        /// "2026-11"; "" before any.
        public var month: String
        /// The furthest reminder shown that month.
        public var reminded: Level

        public init(month: String = "", reminded: Level = .none) {
            self.month = month
            self.reminded = reminded
        }

        /// "2026-11:warning"; "" for none.
        public var stored: String { month.isEmpty ? "" : month + ":" + reminded.name }

        /// The state saved as `stored`; anything else (nothing saved yet, or a value this version can't read) is none,
        /// which at worst shows a reminder again.
        public init(stored: String) {
            let parts = stored.split(separator: ":", omittingEmptySubsequences: false)
            guard parts.count == 2, SpendLimit.isMonthKey(String(parts[0])), let level = Level(name: String(parts[1])) else {
                self.init()
                return
            }
            self.init(month: String(parts[0]), reminded: level)
        }
    }

    /// "2026-11": four digits, a dash and a month from 01 to 12.
    static func isMonthKey(_ text: String) -> Bool {
        let scalars = Array(text.unicodeScalars)
        guard scalars.count == 7, scalars[4] == "-" else { return false }
        let digits = scalars.enumerated().filter { $0.offset != 4 }.map(\.element)
        guard digits.allSatisfy({ $0.value >= 48 && $0.value <= 57 }) else { return false }
        let month = Int(scalars[5].value - 48) * 10 + Int(scalars[6].value - 48)
        return (1...12).contains(month)
    }

    /// What `check` found: the reminder to show now, if any, and the state to save.
    public struct Outcome: Equatable, Sendable {
        /// `.warning` or `.reached`; nil when no reminder is due.
        public let reminder: Level?
        public let state: State

        public init(reminder: Level?, state: State) {
            self.reminder = reminder
            self.state = state
        }
    }

    /// After a request's cost was added to the month: which reminder is due, if any, and the state to save in place of
    /// `state`. `spentBefore` is the month's spend before that cost and `spent` after it, both in USD; `month` is
    /// `monthKey` of today.
    ///
    /// A reminder from another month doesn't count. One the spend was below before this cost (with the limit as it is
    /// now, so after the limit was raised above it) is armed again. When a cost takes the spend past both thresholds at
    /// once, only the limit's reminder is shown.
    public static func check(spentBefore: Double, spent: Double, limit: Double, month: String, state: State) -> Outcome {
        var reminded = state.month == month ? state.reminded : .none
        reminded = min(reminded, level(spent: spentBefore, limit: limit))
        let now = level(spent: spent, limit: limit)
        let reminder: Level? = now > reminded ? now : nil
        return Outcome(reminder: reminder, state: State(month: month, reminded: max(reminded, now)))
    }
}
