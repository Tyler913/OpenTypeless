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
    public internal(set) var text: String
    public let parts: [Part]
    /// From the start until the first token of the part that answered first.
    public let firstTokenSeconds: Double
    /// From the start until the last part was in.
    public let totalSeconds: Double
    /// The answer to laying the joined parts out again as a whole (see SegmentedPolish), when one was asked for and came
    /// back; kept even when it isn't used, since it was billed all the same.
    public internal(set) var relayout: HedgedPolishResult? = nil
    /// Whether `text` is the whole laid out again, rather than the parts as they were joined.
    public internal(set) var relaid = false

    /// Every answer that came back, used or not, for pricing.
    public var answers: [HedgedPolishResult] { parts.compactMap(\.answer) + (relayout.map { [$0] } ?? []) }
    /// The parts that go in as spoken.
    public var fallbacks: [Part] { parts.filter { $0.fallback != nil } }
    /// The model that answered (the first part's, should the backup have answered some parts).
    public var model: String { answers.first?.model ?? "" }
    public var usedBackup: Bool { answers.contains { $0.usedBackup } }
}

/// The end of the part before one part of a long transcript and the start of the one after it, as spoken; nil where
/// there's none.
public struct SurroundingParts: Sendable, Equatable {
    public var before: String?
    public var after: String?

    public init(before: String?, after: String?) {
        self.before = before
        self.after = after
    }
}

/// Long transcripts are cleaned up in parts. Asked to clean up a long dictation in one go, models summarise it instead:
/// of 54 real dictations of a minute or more, 15 came back with under 60% of their words (the worst 2,023 → 379), often
/// rewritten into numbered lists. A part of a few hundred words gets edited as asked. The parts are split at sentence
/// ends, cleaned up in parallel with the same prompt, checked one by one, and joined again, so a long dictation is also
/// ready sooner. The text around the cursor (see CursorContext), when there is some, goes with every part, since each
/// is a request of its own and a name can come up in any of them; it's at most a few hundred tokens, and only long
/// dictations have more than one part.
///
/// A part can't see the others, so a correction of something in the part before, a list that goes on across the split or
/// the layout of the whole was lost at the seams. So each part of a dictation also gets, as reference, the end of the part
/// before it and the start of the one after (`surroundingWords` words each, as spoken), in blocks of their own with a note
/// (see `Prompts.polishUserMessage`). An answer that copied text from them beyond a name is caught like one that copied
/// the text around the cursor (`Prompts.copiedContext`), and that part goes in as spoken. And where the text's layout is
/// the point (`Prompts.relayouts`), the joined parts are laid out again as a whole by one more request that may only
/// change line breaks and list markers: its answer is used only when it is the joined text exactly, but for those
/// (`Prompts.onlyLayoutChanged`), so it can't reword anything, and only when it comes back in time (`relayoutTimeout`).
public enum SegmentedPolish {
    /// Up to this many words (see WordCount: a Chinese character is one) a transcript goes in one request: about a
    /// minute and a half of Chinese, or two and a half of English.
    public static let singleRequestLimit = 400
    /// What a part aims for when a transcript is split.
    static let partSize = 300

    public static func needsParts(_ transcript: String) -> Bool {
        WordCount.count(transcript) > singleRequestLimit
    }

    /// How much of each neighbouring part goes with a part, at most, in words.
    static let surroundingWords = partSize / 2

    /// The longest joined text that is laid out again (about five minutes of speech): the request writes all of it out
    /// again, so a longer one would keep the dictation waiting.
    static let relayoutLimit = 2000

    /// How long laying the joined text out again may take before the text goes in as joined: a few seconds for the answer
    /// to start, then the time a fast model takes to write it all out again.
    public static func relayoutTimeout(_ text: String) -> Double { min(20, 3 + Double(text.utf16.count) * 0.006) }

    /// The neighbouring parts' text that goes with part `index` of `texts`: the end of the part before and the start of the
    /// part after, whole sentences where they fit in `surroundingWords`. Nil for a transcript in one part.
    static func surrounding(_ texts: [String], _ index: Int) -> SurroundingParts? {
        guard texts.count > 1 else { return nil }
        return SurroundingParts(before: index > 0 ? excerpt(texts[index - 1], fromEnd: true) : nil,
                                after: index + 1 < texts.count ? excerpt(texts[index + 1], fromEnd: false) : nil)
    }

    /// Up to `surroundingWords` words of `text` from its end (or its start): whole sentences, or when the nearest sentence
    /// is longer than that, its nearest words.
    static func excerpt(_ text: String, fromEnd: Bool) -> String {
        let sentences = split(text.trimmingCharacters(in: .whitespacesAndNewlines), at: isSentenceEnd)
        var taken = take(sentences, fromEnd: fromEnd)
        if taken.isEmpty, let nearest = fromEnd ? sentences.last : sentences.first {
            taken = take(split(nearest, at: isWordEnd), fromEnd: fromEnd)
        }
        return taken.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The pieces nearest the end (or start) that fit in `surroundingWords` together, in their order.
    private static func take(_ pieces: [String], fromEnd: Bool) -> String {
        var taken: [String] = []
        var words = 0
        for piece in fromEnd ? Array(pieces.reversed()) : pieces {
            let size = WordCount.count(piece)
            if words + size > surroundingWords { break }
            taken.append(piece)
            words += size
        }
        return (fromEnd ? Array(taken.reversed()) : taken).joined()
    }

    /// Everything a part's request carried as reference, for `Prompts.copiedContext`: the text around the cursor and the
    /// ends of the parts around it. Nil when there was none.
    static func reference(_ context: String?, _ surrounding: SurroundingParts?) -> String? {
        let all = [context, surrounding?.before, surrounding?.after].compactMap { $0 }
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        return all.isEmpty ? nil : all.joined(separator: "\n")
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

    /// The app's clean-up of `transcript` (or its translation, when the routes' options ask for one): each part hedged
    /// between the routes (see HedgedPolish) and asked a second time after a transient failure, but not after a timeout.
    /// A second wait as long as the first would only hold the dictation up; the part goes in as spoken instead. Nor when
    /// the system has no connection at all (`NetworkFailure.noConnection`): a moment later it rarely has one.
    /// `concurrency` caps how many parts are out at once (nil: all of them, which is what a dictation wants; the
    /// transcript of an hour-long file has dozens), and with a cap, a part waits to start while `holdWhile` says so (a
    /// file's clean-up, while a dictation is under way).
    public static func run(
        transcript: String,
        primary: PolishRoute,
        backup: PolishRoute?,
        concurrency: Int? = nil,
        holdWhile: (@Sendable () -> Bool)? = nil,
        onPartial: (@Sendable (String) -> Void)? = nil
    ) async throws -> SegmentedPolishResult {
        let policy = RetryPolicy(maxAttempts: 2, retriesTimeouts: false,
                                 stopsEarly: { $0.networkFailure == .noConnection })
        let options = primary.options
        let dictation = concurrency == nil
        let result = try await runParts(transcript: transcript, translation: options.translation,
                                        vocabulary: options.vocabulary, context: options.context,
                                        concurrency: concurrency, holdWhile: holdWhile, polish: { part, surrounding, partial in
            var first = primary
            first.options.surrounding = surrounding
            var second = backup
            second?.options.surrounding = surrounding
            let routes = (first, second)
            return try await policy.run { _ in
                try await HedgedPolish.run(transcript: part, primary: routes.0, backup: routes.1, onPartial: partial)
            }
        }, onPartial: onPartial, keepsLineBreaks: options.dictatedLineBreaks,
           sendsSurrounding: dictation && options.translation == nil)
        guard dictation, result.parts.count > 1, Prompts.relayouts(options.layout), !options.dictatedLineBreaks,
              WordCount.count(result.text) <= relayoutLimit else { return result }
        return try await relaid(result, route: primary)
    }

    /// `result` with its joined text laid out again as a whole by `route`'s layout, when the answer comes back within
    /// `relayoutTimeout` and changed nothing but the layout; as it was otherwise. Throws only when cancelled.
    static func relaid(_ result: SegmentedPolishResult, route: PolishRoute) async throws -> SegmentedPolishResult {
        var relayout = route
        relayout.options.relayout = true
        relayout.options.context = nil
        relayout.options.surrounding = nil
        relayout.options.onContextSent = nil
        let request = relayout
        let text = result.text
        let answer: HedgedPolishResult
        do {
            answer = try await withTimeout(relayoutTimeout(text)) {
                try await HedgedPolish.run(transcript: text, primary: request, backup: nil)
            }
        } catch {
            if Task.isCancelled || APIError.from(error) == .cancelled { throw APIError.cancelled }
            return result
        }
        var laidOut = result
        laidOut.relayout = answer
        if !answer.result.truncated, Prompts.onlyLayoutChanged(original: text, output: answer.result.text) {
            laidOut.text = answer.result.text.trimmingCharacters(in: .whitespacesAndNewlines)
            laidOut.relaid = true
        }
        return laidOut
    }

    /// Cleans up the parts of `transcript` with `polish`, all at once (or `concurrency` at a time), and puts each part's
    /// answer through `Prompts.fallback` (`Prompts.translationFallback` when it's a translation into `translation`): a
    /// part whose answer is unusable, or that got none, goes in as spoken. Throws only when no part got an answer (the
    /// first part's error), or when cancelled. `onPartial` gets the parts' text so far, joined. `vocabulary`: the
    /// speaker's terms, which a translation keeps as they are. `context`: the text around the cursor the requests carry,
    /// if any. `keepsLineBreaks`: the transcript's line breaks were dictated, and those between two parts are put back as
    /// they were.
    public static func run(
        transcript: String,
        translation: TranslationTarget? = nil,
        vocabulary: [String] = [],
        context: String? = nil,
        concurrency: Int? = nil,
        holdWhile: (@Sendable () -> Bool)? = nil,
        polish: @escaping @Sendable (String, (@Sendable (String) -> Void)?) async throws -> HedgedPolishResult,
        onPartial: (@Sendable (String) -> Void)? = nil,
        keepsLineBreaks: Bool = false
    ) async throws -> SegmentedPolishResult {
        try await runParts(transcript: transcript, translation: translation, vocabulary: vocabulary, context: context,
                           concurrency: concurrency, holdWhile: holdWhile,
                           polish: { part, _, partial in try await polish(part, partial) },
                           onPartial: onPartial, keepsLineBreaks: keepsLineBreaks, sendsSurrounding: false)
    }

    /// `run(transcript:…polish:…)`, with `polish` given each part's surrounding parts (see `surrounding`) when
    /// `sendsSurrounding` and there's more than one part; an answer that copied from them beyond what the clean-up allows
    /// is then not used.
    public static func runParts(
        transcript: String,
        translation: TranslationTarget? = nil,
        vocabulary: [String] = [],
        context: String? = nil,
        concurrency: Int? = nil,
        holdWhile: (@Sendable () -> Bool)? = nil,
        polish: @escaping @Sendable (String, SurroundingParts?, (@Sendable (String) -> Void)?) async throws -> HedgedPolishResult,
        onPartial: (@Sendable (String) -> Void)? = nil,
        keepsLineBreaks: Bool = false,
        sendsSurrounding: Bool = false
    ) async throws -> SegmentedPolishResult {
        let split = Self.partsAndBreaks(of: transcript)
        let texts = split.map(\.text)
        let breaks = keepsLineBreaks ? [0] + split.dropLast().map(\.breaksAfter) : []
        let start = ProcessInfo.processInfo.systemUptime
        let progress = PartialProgress(count: texts.count, onChange: onPartial)
        let gate = concurrency.map { AsyncSemaphore(max(1, $0)) }
        let surroundings = texts.indices.map { sendsSurrounding ? surrounding(texts, $0) : nil }
        let outcomes = await withTaskGroup(of: (Int, Result<HedgedPolishResult, Error>).self) { group in
            for (index, text) in texts.enumerated() {
                group.addTask {
                    await gate?.wait()
                    let outcome: Result<HedgedPolishResult, Error>
                    do {
                        if gate != nil, let holdWhile {
                            while holdWhile(), !Task.isCancelled { try? await Task.sleep(nanoseconds: 200_000_000) }
                        }
                        // One that waited its turn past a cancellation doesn't start at all.
                        if gate != nil { try Task.checkCancellation() }
                        outcome = .success(try await polish(text, surroundings[index], { progress.update(index, $0) }))
                    } catch {
                        outcome = .failure(error)
                    }
                    await gate?.signal()
                    return (index, outcome)
                }
            }
            var collected = [Result<HedgedPolishResult, Error>?](repeating: nil, count: texts.count)
            for await (index, outcome) in group { collected[index] = outcome }
            return collected
        }
        if Task.isCancelled { throw APIError.cancelled }
        var results: [SegmentedPolishResult.Part] = []
        for (index, (text, outcome)) in zip(texts, outcomes).enumerated() {
            switch outcome {
            case let .success(answer)?:
                let output = answer.result.text, truncated = answer.result.truncated
                let fallback = if let translation {
                    Prompts.translationFallback(input: text, output: output, truncated: truncated, target: translation,
                                                vocabulary: vocabulary)
                } else {
                    Prompts.fallback(input: text, output: output, truncated: truncated,
                                     context: reference(context, surroundings[index]))
                }
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
