import Foundation

/// What Esc does to a dictation. A long recording is too much to lose to a stray key press, so from
/// `keepAfterSeconds` on it is kept, transcribed but not cleaned up or inserted, and shown in History as
/// cancelled for `keptFor`; shorter ones are thrown away as before.
public enum CancelPolicy {
    public static let keepAfterSeconds: Double = 10
    public static let keptFor: TimeInterval = 24 * 60 * 60

    /// Whether cancelling a recording of this length keeps it.
    public static func keeps(recordedSeconds: Double) -> Bool { recordedSeconds >= keepAfterSeconds }

    /// When a cancelled dictation made at `date` is removed.
    public static func expiry(of date: Date) -> Date { date.addingTimeInterval(keptFor) }

    public static func isExpired(recordedAt date: Date, now: Date = Date()) -> Bool { now >= expiry(of: date) }
}
