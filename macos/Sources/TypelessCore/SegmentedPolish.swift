import Foundation

/// The clean-up of one transcript: in one request, or in parts when it is long (see SegmentedPolish).
public struct SegmentedPolishResult: Sendable {
    public struct Part: Sendable {
        /// This part of the transcript, as spoken.
        public let transcript: String
        /// The clean-up's answer, when one came back. Kept even when it isn't used: it was billed all the same.
        public let answer: HedgedPolishResult?
        /// Why `transcript` goes in as spoken instead, or nil when the answer is used.
        public let fallback: PolishFallback?
        /// What went wrong when no answer came back.
        public let error: APIError?

        /// What this part contributes to the text.
        public var text: String { fallback == nil ? answer?.result.text ?? transcript : transcript }
    }

    /// The text to insert: each part's clean-up, or the part as spoken where that was unusable.
    public let text: String
    public let parts: [Part]
    /// From the start until the first token of the part that answered first.
    public let firstTokenSeconds: Double
    /// From the start until the last part was in.
    public let totalSeconds: Double

    /// Every answer that came back, used or not, for pricing.
    public var answers: [HedgedPolishResult] { parts.compactMap(\.answer) }
    /// The parts that go in as spoken.
    public var fallbacks: [Part] { parts.filter { $0.fallback != nil } }
    /// The model that answered (the first part's, should the backup have answered some parts).
    public var model: String { answers.first?.model ?? "" }
    public var usedBackup: Bool { answers.contains { $0.usedBackup } }
}

/// Long transcripts are cleaned up in parts. Asked to clean up a long dictation in one go, models summarise it instead:
/// of 54 real dictations of a minute or more, 15 came back with under 60% of their words (the worst 2,023 → 379), often
/// rewritten into numbered lists. A part of a few hundred words gets edited as asked. The parts are split at sentence
/// ends, cleaned up in parallel with the same prompt, checked one by one, and joined again, so a long dictation is also
/// ready sooner. Each part goes on its own: the prompt has no place for read-only context, and a neighbouring part
/// pasted in as such could end up cleaned up into the answer as well.
public enum SegmentedPolish {
    /// Up to this many words (see WordCount: a Chinese character is one) a transcript goes in one request: about a
    /// minute and a half of Chinese, or two and a half of English.
    public static let singleRequestLimit = 400
    /// What a part aims for when a transcript is split.
    static let partSize = 300

    public static func needsParts(_ transcript: String) -> Bool {
        WordCount.count(transcript) > singleRequestLimit
    }

    /// The parts `transcript` is cleaned up in: itself, untouched, unless it's long. A long one is split at sentence
    /// ends into parts of about the same size; a sentence too long to fit is split at commas, and only a run-on without
    /// either (speech recognition sometimes leaves Chinese unpunctuated) between words or characters.
    public static func parts(of transcript: String) -> [String] {
        partsAndBreaks(of: transcript).map(\.text)
    }

    /// `parts(of:)`, each with the number of line breaks that follow it in the transcript (none after the last).
    static func partsAndBreaks(of transcript: String) -> [(text: String, breaksAfter: Int)] {
        guard needsParts(transcript) else { return [(transcript, 0)] }
        let text = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        let total = WordCount.count(text)
        let count = (total + partSize - 1) / partSize
        let target = Double(total) / Double(count)
        let limit = partSize / 2
        let pieces = split(text, at: isSentenceEnd).flatMap { sentence -> [String] in
            guard WordCount.count(sentence) > limit else { return [sentence] }
            return split(sentence, at: isClauseEnd).flatMap { clause -> [String] in
                WordCount.count(clause) > limit ? split(clause, at: isWordEnd) : [clause]
            }
        }
        var parts: [(text: String, words: Int)] = []
        var current = ""
        var words = 0
        for piece in pieces {
            let size = WordCount.count(piece)
            // Ends the part here when that leaves it closer to the target than taking this piece as well.
            if words > 0, Double(words) + Double(size) / 2 > target {
                parts.append((current, words))
                current = ""
                words = 0
            }
            current += piece
            words += size
        }
        if !current.isEmpty { parts.append((current, words)) }
        // A short leftover goes with the part before it, as long as that doesn't make it too long.
        if parts.count > 1, let last = parts.last, last.words < partSize / 3,
           parts[parts.count - 2].words + last.words <= singleRequestLimit {
            parts.removeLast()
            parts[parts.count - 1].text += last.text
            parts[parts.count - 1].words += last.words
        }
        return parts.compactMap { part in
            let trimmed = part.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { return nil }
            // What ends a piece (the space and line breaks after a sentence) stays with it, so the breaks between two
            // parts are at the end of the first.
            return (trimmed, part.text.reversed().prefix(while: \.isWhitespace).filter(\.isNewline).count)
        }
    }

    /// Joins the parts' clean-ups the way transcripts are joined (see TranscriptJoiner), except that where either side
    /// is laid out over several lines (paragraphs, a list), they're kept a paragraph apart, so text doesn't run on from
    /// the last item of a list. `breaks`: the line breaks the speaker dictated before each text (see `SpokenLayout`),
    /// where a long transcript was split at them: exactly those go back in.
    public static func join(_ texts: [String], breaks: [Int] = []) -> String {
        var result = ""
        var previous = ""
        for (index, text) in texts.enumerated() {
            let part = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !part.isEmpty else { continue }
            let dictated = index < breaks.count ? breaks[index] : 0
            if !result.isEmpty, dictated > 0 {
                result += String(repeating: "\n", count: dictated) + part
            } else if !result.isEmpty, previous.contains("\n") || part.contains("\n") {
                result += "\n\n" + part
            } else {
                result = TranscriptJoiner.join([result, part])
            }
            previous = part
        }
        return result
    }

    /// The app's clean-up of `transcript`: each part hedged between the routes (see HedgedPolish) and asked a second
    /// time after a transient failure, but not after a timeout. A second wait as long as the first would only hold the
    /// dictation up; the part goes in as spoken instead.
    public static func run(
        transcript: String,
        primary: PolishRoute,
        backup: PolishRoute?,
        onPartial: (@Sendable (String) -> Void)? = nil
    ) async throws -> SegmentedPolishResult {
        let policy = RetryPolicy(maxAttempts: 2, retriesTimeouts: false)
        return try await run(transcript: transcript, polish: { part, partial in
            try await policy.run { _ in
                try await HedgedPolish.run(transcript: part, primary: primary, backup: backup, onPartial: partial)
            }
        }, onPartial: onPartial, keepsLineBreaks: primary.options.dictatedLineBreaks)
    }

    /// Cleans up the parts of `transcript` with `polish`, all at once, and puts each part's answer through
    /// `Prompts.fallback`: a part whose answer is unusable, or that got none, goes in as spoken. Throws only when no part
    /// got an answer (the first part's error), or when cancelled. `onPartial` gets the parts' text so far, joined.
    /// `keepsLineBreaks`: the transcript's line breaks were dictated, and those between two parts are put back as they were.
    public static func run(
        transcript: String,
        polish: @escaping @Sendable (String, (@Sendable (String) -> Void)?) async throws -> HedgedPolishResult,
        onPartial: (@Sendable (String) -> Void)? = nil,
        keepsLineBreaks: Bool = false
    ) async throws -> SegmentedPolishResult {
        let split = Self.partsAndBreaks(of: transcript)
        let texts = split.map(\.text)
        let breaks = keepsLineBreaks ? [0] + split.dropLast().map(\.breaksAfter) : []
        let start = ProcessInfo.processInfo.systemUptime
        let progress = PartialProgress(count: texts.count, onChange: onPartial)
        let outcomes = await withTaskGroup(of: (Int, Result<HedgedPolishResult, Error>).self) { group in
            for (index, text) in texts.enumerated() {
                group.addTask {
                    do {
                        return (index, .success(try await polish(text, { progress.update(index, $0) })))
                    } catch {
                        return (index, .failure(error))
                    }
                }
            }
            var collected = [Result<HedgedPolishResult, Error>?](repeating: nil, count: texts.count)
            for await (index, outcome) in group { collected[index] = outcome }
            return collected
        }
        if Task.isCancelled { throw APIError.cancelled }
        var results: [SegmentedPolishResult.Part] = []
        for (text, outcome) in zip(texts, outcomes) {
            switch outcome {
            case let .success(answer)?:
                let fallback = Prompts.fallback(input: text, output: answer.result.text, truncated: answer.result.truncated)
                results.append(.init(transcript: text, answer: answer, fallback: fallback, error: nil))
            case let .failure(error)?:
                let apiError = APIError.from(error)
                if apiError == .cancelled { throw apiError }
                results.append(.init(transcript: text, answer: nil, fallback: .failed, error: apiError))
            case nil:
                throw APIError.cancelled
            }
        }
        // Nothing came back at all: the clean-up failed, just as a single request would have.
        if results.allSatisfy({ $0.answer == nil }), let error = results.first?.error { throw error }
        return SegmentedPolishResult(text: join(results.map(\.text), breaks: breaks), parts: results,
                                     firstTokenSeconds: results.compactMap { $0.answer?.firstTokenSeconds }.min() ?? 0,
                                     totalSeconds: ProcessInfo.processInfo.systemUptime - start)
    }

    // MARK: - Splitting

    /// Cuts `text` after every character `isEnd` accepts (given the character after it). What follows an end (spaces,
    /// more punctuation, a closing quote or bracket) stays with the piece it ends.
    static func split(_ text: String, at isEnd: (Character, Character?) -> Bool) -> [String] {
        let characters = Array(text)
        var pieces: [String] = []
        var current = ""
        var ended = false
        for (index, character) in characters.enumerated() {
            if ended, !trails(character) {
                pieces.append(current)
                current = ""
                ended = false
            }
            current.append(character)
            if isEnd(character, index + 1 < characters.count ? characters[index + 1] : nil) { ended = true }
        }
        if !current.isEmpty { pieces.append(current) }
        return pieces
    }

    private static func trails(_ character: Character) -> Bool {
        if character.isWhitespace { return true }
        switch character.unicodeScalars.first?.properties.generalCategory {
        case .closePunctuation?, .finalPunctuation?, .otherPunctuation?: return true
        default: return false
        }
    }

    /// "." only ends a sentence before a space or at the end, so "3.5" and "macOS 27.1" stay whole.
    static func isSentenceEnd(_ character: Character, _ next: Character?) -> Bool {
        if character.isNewline || "。！？；…!?;".contains(character) { return true }
        return character == "." && (next?.isWhitespace ?? true)
    }

    /// Likewise "," and ":" only before a space, so "1,000" and "10:30" stay whole.
    static func isClauseEnd(_ character: Character, _ next: Character?) -> Bool {
        if "，、：".contains(character) { return true }
        return (character == "," || character == ":") && (next?.isWhitespace ?? true)
    }

    static func isWordEnd(_ character: Character, _ next: Character?) -> Bool {
        if character.isWhitespace { return true }
        return character.unicodeScalars.first.map { WordCount.isCJKWordCharacter($0.value) } ?? false
    }
}

/// Each part's text so far, passed on joined, so the progress bar moves with all of them together.
private final class PartialProgress: @unchecked Sendable {
    private let lock = NSLock()
    private var texts: [String]
    private let onChange: (@Sendable (String) -> Void)?

    init(count: Int, onChange: (@Sendable (String) -> Void)?) {
        texts = Array(repeating: "", count: count)
        self.onChange = onChange
    }

    func update(_ index: Int, _ text: String) {
        guard let onChange else { return }
        // Under the lock, so the joined texts are passed on in order.
        lock.withLock {
            texts[index] = text
            onChange(texts.count == 1 ? text : SegmentedPolish.join(texts))
        }
    }
}
