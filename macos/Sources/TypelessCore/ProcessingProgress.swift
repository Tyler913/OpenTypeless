import Foundation

/// How far a dictation has come once the key is released, for the bar that fills the HUD capsule.
///
/// Real events set where the bar stands: speech-to-text chunks coming back, the whole transcript, the clean-up's
/// first token and how much of it has streamed, and the result being delivered. Between two events the bar creeps
/// part of the way towards the next one, so a slow request still looks alive, but it never gets there on its own
/// and it only ever moves right.
public struct ProcessingProgress: Sendable, Equatable {
    public enum Milestone: Sendable, Equatable {
        /// `done` of the `total` speech-to-text chunks are back.
        case transcribing(done: Int, total: Int)
        /// The whole transcript is in.
        case transcribed
        /// The clean-up is streaming: `received` characters of an answer about `expected` long.
        case polishing(received: Int, expected: Int)
        /// The text is ready to paste: the bar fills.
        case delivered
    }

    /// Where speech-to-text ends and the clean-up starts, as a share of the bar.
    static let transcribedAt = 0.45
    /// The clean-up's first token, and its last (the rest is left for the paste).
    static let polishStartsAt = 0.6
    static let polishEndsAt = 0.95
    /// The bar starts a little filled, so releasing the key visibly starts something.
    static let startsAt = 0.05
    /// Share of the gap to the next milestone the creep covers at most, and how fast (seconds to cover half of it).
    static let creepShare = 0.7
    static let creepHalfLife = 1.5
    /// Seconds for the bar to glide most of the way to a new position (instead of jumping).
    static let glide = 0.12

    /// Whether a clean-up follows the transcript.
    public let polishes: Bool
    /// Where the last milestone put the bar.
    public private(set) var reached: Double
    /// Where the next milestone will put it; the creep stays short of it.
    public private(set) var next: Double
    /// What the bar shows now, 0…1.
    public private(set) var shown: Double = 0
    private var sinceMilestone: Double = 0

    public init(polishes: Bool) {
        self.polishes = polishes
        reached = Self.startsAt
        next = Self.transcriptEnd(polishes: polishes)
    }

    /// Moves the bar to a milestone.
    public mutating func reach(_ milestone: Milestone) {
        let (value, following) = position(of: milestone)
        // A milestone behind the bar (a chunk sent again, a retry round) changes nothing.
        guard value > reached || following > next else { return }
        // Carries on from where the creep had got to, if that's further.
        reached = max(reached, value, goal)
        next = max(next, following, reached)
        sinceMilestone = 0
    }

    /// Advances the animation by `seconds` and returns where the bar is now.
    @discardableResult
    public mutating func tick(_ seconds: Double) -> Double {
        guard seconds > 0, seconds.isFinite else { return shown }
        sinceMilestone += seconds
        let step = 1 - exp(-seconds / Self.glide)
        shown = min(1, max(shown, shown + (goal - shown) * step))
        return shown
    }

    /// Without a clean-up, speech-to-text takes most of the bar.
    private static func transcriptEnd(polishes: Bool) -> Double { polishes ? transcribedAt : 0.9 }

    /// Where the bar is heading: the last milestone plus the creep towards the next.
    private var goal: Double {
        let creep = Self.creepShare * (1 - pow(0.5, sinceMilestone / Self.creepHalfLife))
        return min(1, reached + (next - reached) * creep)
    }

    /// The bar position for a milestone, and for the milestone after it.
    private func position(of milestone: Milestone) -> (Double, Double) {
        let transcribed = Self.transcriptEnd(polishes: polishes)
        switch milestone {
        case let .transcribing(done, total):
            let total = max(1, total)
            let done = min(max(0, done), total)
            let band = transcribed - Self.startsAt
            return (Self.startsAt + band * Double(done) / Double(total),
                    Self.startsAt + band * Double(min(done + 1, total)) / Double(total))
        case .transcribed:
            return (transcribed, polishes ? Self.polishStartsAt : 1)
        case let .polishing(received, expected):
            let share = expected > 0 ? min(1, Double(max(0, received)) / Double(expected)) : 0
            return (Self.polishStartsAt + (Self.polishEndsAt - Self.polishStartsAt) * share, Self.polishEndsAt)
        case .delivered:
            return (1, 1)
        }
    }
}
