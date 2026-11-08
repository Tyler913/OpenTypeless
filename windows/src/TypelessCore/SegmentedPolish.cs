using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TypelessCore;

/// <summary>The clean-up of one transcript: in one request, or in parts when it is long (see <see cref="SegmentedPolish"/>).</summary>
/// <param name="Text">The text to insert: each part's clean-up, or the part as spoken where that was unusable.</param>
/// <param name="FirstTokenSeconds">From the start until the first token of the part that answered first.</param>
/// <param name="TotalSeconds">From the start until the last part was in.</param>
public sealed record SegmentedPolishResult(string Text, IReadOnlyList<SegmentedPolishResult.Part> Parts, double FirstTokenSeconds,
                                           double TotalSeconds)
{
    /// <param name="Transcript">This part of the transcript, as spoken.</param>
    /// <param name="Answer">The clean-up's answer, when one came back. Kept even when it isn't used: it was billed all the same.</param>
    /// <param name="Fallback">Why <paramref name="Transcript"/> goes in as spoken instead, or null when the answer is used.</param>
    /// <param name="Error">What went wrong when no answer came back.</param>
    public sealed record Part(string Transcript, HedgedPolishResult? Answer, PolishFallback? Fallback, ApiException? Error)
    {
        /// <summary>What this part contributes to the text.</summary>
        public string Text => Fallback == null && Answer != null ? Answer.Result.Text : Transcript;
    }

    /// <summary>
    /// The answer to laying the joined parts out again as a whole (see <see cref="SegmentedPolish"/>), when one was asked
    /// for and came back; kept even when it isn't used, since it was billed all the same.
    /// </summary>
    public HedgedPolishResult? Relayout { get; init; }

    /// <summary>Whether <see cref="Text"/> is the whole laid out again, rather than the parts as they were joined.</summary>
    public bool Relaid { get; init; }

    /// <summary>Every answer that came back, used or not, for pricing.</summary>
    public IReadOnlyList<HedgedPolishResult> Answers =>
        Parts.Select(p => p.Answer).Append(Relayout).OfType<HedgedPolishResult>().ToList();

    /// <summary>The parts that go in as spoken.</summary>
    public IReadOnlyList<Part> Fallbacks => Parts.Where(p => p.Fallback != null).ToList();

    /// <summary>The model that answered (the first part's, should the backup have answered some parts).</summary>
    public string Model => Answers.FirstOrDefault()?.Model ?? "";

    public bool UsedBackup => Answers.Any(a => a.UsedBackup);
}

/// <summary>The end of the part before one part of a long transcript and the start of the one after it, as spoken; null where there's none.</summary>
public sealed record SurroundingParts(string? Before, string? After);

/// <summary>
/// Long transcripts are cleaned up in parts. Asked to clean up a long dictation in one go, models summarise it instead:
/// of 54 real dictations of a minute or more, 15 came back with under 60% of their words (the worst 2,023 → 379), often
/// rewritten into numbered lists. A part of a few hundred words gets edited as asked. The parts are split at sentence
/// ends, cleaned up in parallel with the same prompt, checked one by one, and joined again, so a long dictation is also
/// ready sooner. The text around the cursor (see <see cref="CursorContext"/>), when there is some, goes with every part,
/// since each is a request of its own and a name can come up in any of them; it's at most a few hundred tokens, and only
/// long dictations have more than one part.
/// <para>
/// A part can't see the others, so a correction of something in the part before, a list that goes on across the split
/// or the layout of the whole was lost at the seams. So each part of a dictation also gets, as reference, the end of the
/// part before it and the start of the one after (<see cref="SurroundingWords"/> words each, as spoken), in blocks of
/// their own with a note (see <see cref="Prompts.PolishUserMessage"/>). An answer that copied text from them beyond a
/// name is caught like one that copied the text around the cursor (<see cref="Prompts.CopiedContext"/>), and that part
/// goes in as spoken. And where the text's layout is the point (<see cref="Prompts.Relayouts"/>), the joined parts are
/// laid out again as a whole by one more request that may only change line breaks and list markers: its answer is used
/// only when it is the joined text exactly, but for those (<see cref="Prompts.OnlyLayoutChanged"/>), so it can't reword
/// anything, and only when it comes back in time (<see cref="RelayoutTimeout"/>).
/// </para>
/// </summary>
public static class SegmentedPolish
{
    /// <summary>
    /// Up to this many words (see <see cref="WordCount"/>: a Chinese character is one) a transcript goes in one request:
    /// about a minute and a half of Chinese, or two and a half of English.
    /// </summary>
    public const int SingleRequestLimit = 400;

    /// <summary>What a part aims for when a transcript is split.</summary>
    internal const int PartSize = 300;

    public static bool NeedsParts(string transcript) => WordCount.Count(transcript) > SingleRequestLimit;

    /// <summary>How much of each neighbouring part goes with a part, at most, in words.</summary>
    internal const int SurroundingWords = PartSize / 2;

    /// <summary>
    /// The longest joined text that is laid out again (about five minutes of speech): the request writes all of it out
    /// again, so a longer one would keep the dictation waiting.
    /// </summary>
    internal const int RelayoutLimit = 2000;

    /// <summary>
    /// How long laying the joined text out again may take before the text goes in as joined: a few seconds for the answer
    /// to start, then the time a fast model takes to write it all out again.
    /// </summary>
    public static double RelayoutTimeout(string text) => Math.Min(20, 3 + text.Length * 0.006);

    /// <summary>
    /// The neighbouring parts' text that goes with part <paramref name="index"/> of <paramref name="texts"/>: the end of the
    /// part before and the start of the part after, whole sentences where they fit in <see cref="SurroundingWords"/>.
    /// Null for a transcript in one part.
    /// </summary>
    internal static SurroundingParts? Surrounding(IReadOnlyList<string> texts, int index)
    {
        if (texts.Count < 2) return null;
        return new SurroundingParts(index > 0 ? Excerpt(texts[index - 1], fromEnd: true) : null,
                                    index + 1 < texts.Count ? Excerpt(texts[index + 1], fromEnd: false) : null);
    }

    /// <summary>
    /// Up to <see cref="SurroundingWords"/> words of <paramref name="text"/> from its end (or its start): whole sentences,
    /// or when the nearest sentence is longer than that, its nearest words.
    /// </summary>
    internal static string Excerpt(string text, bool fromEnd)
    {
        var trimmed = text.Trim();
        var sentences = Split(trimmed, IsSentenceEnd);
        var taken = Take(sentences, fromEnd);
        if (taken.Length == 0 && sentences.Count > 0)
        {
            taken = Take(Split(fromEnd ? sentences[^1] : sentences[0], IsWordEnd), fromEnd);
        }
        return taken.Trim();
    }

    /// <summary>The pieces nearest the end (or start) that fit in <see cref="SurroundingWords"/> together, in their order.</summary>
    private static string Take(List<string> pieces, bool fromEnd)
    {
        var taken = new List<string>();
        var words = 0;
        foreach (var piece in fromEnd ? Enumerable.Reverse(pieces) : pieces)
        {
            var size = WordCount.Count(piece);
            if (words + size > SurroundingWords) break;
            taken.Add(piece);
            words += size;
        }
        if (fromEnd) taken.Reverse();
        return string.Concat(taken);
    }

    /// <summary>
    /// The parts <paramref name="transcript"/> is cleaned up in: itself, untouched, unless it's long. A long one is split at
    /// sentence ends into parts of about the same size; a sentence too long to fit is split at commas, and only a run-on
    /// without either (speech recognition sometimes leaves Chinese unpunctuated) between words or characters.
    /// </summary>
    public static List<string> Parts(string transcript) => PartsAndBreaks(transcript).Select(p => p.Text).ToList();

    /// <summary><see cref="Parts"/>, each with the number of line breaks that follow it in the transcript (none after the last).</summary>
    internal static List<(string Text, int BreaksAfter)> PartsAndBreaks(string transcript)
    {
        if (!NeedsParts(transcript)) return [(transcript, 0)];
        var text = transcript.Trim();
        var total = WordCount.Count(text);
        var count = (total + PartSize - 1) / PartSize;
        var target = (double)total / count;
        const int limit = PartSize / 2;
        var pieces = Split(text, IsSentenceEnd).SelectMany(sentence => WordCount.Count(sentence) <= limit
            ? [sentence]
            : Split(sentence, IsClauseEnd).SelectMany(clause => WordCount.Count(clause) > limit ? Split(clause, IsWordEnd) : [clause]));
        var parts = new List<(string Text, int Words)>();
        var current = new StringBuilder();
        var words = 0;
        foreach (var piece in pieces)
        {
            var size = WordCount.Count(piece);
            // Ends the part here when that leaves it closer to the target than taking this piece as well.
            if (words > 0 && words + size / 2.0 > target)
            {
                parts.Add((current.ToString(), words));
                current.Clear();
                words = 0;
            }
            current.Append(piece);
            words += size;
        }
        if (current.Length > 0) parts.Add((current.ToString(), words));
        // A short leftover goes with the part before it, as long as that doesn't make it too long.
        if (parts.Count > 1 && parts[^1].Words < PartSize / 3 && parts[^2].Words + parts[^1].Words <= SingleRequestLimit)
        {
            var last = parts[^1];
            parts.RemoveAt(parts.Count - 1);
            parts[^1] = (parts[^1].Text + last.Text, parts[^1].Words + last.Words);
        }
        // What ends a piece (the space and line breaks after a sentence) stays with it, so the breaks between two parts are
        // at the end of the first.
        return parts.Select(p => (Text: p.Text.Trim(), BreaksAfter: TrailingBreaks(p.Text))).Where(p => p.Text.Length > 0).ToList();
    }

    /// <summary>The line breaks among the spaces at the end of <paramref name="text"/>, a "\r\n" counting as one (as on macOS).</summary>
    private static int TrailingBreaks(string text)
    {
        var count = 0;
        for (var i = text.Length - 1; i >= 0 && char.IsWhiteSpace(text[i]); i--)
        {
            var c = text[i];
            if (c is not ('\n' or '\v' or '\f' or '\r' or '\u0085' or '\u2028' or '\u2029')) continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Joins the parts' clean-ups the way transcripts are joined (see <see cref="TranscriptJoiner"/>), except that where
    /// either side is laid out over several lines (paragraphs, a list), they're kept a paragraph apart, so text doesn't run
    /// on from the last item of a list. <paramref name="breaks"/>: the line breaks the speaker dictated before each text
    /// (see <see cref="SpokenLayout"/>), where a long transcript was split at them: exactly those go back in.
    /// </summary>
    public static string Join(IEnumerable<string> texts, IReadOnlyList<int>? breaks = null)
    {
        var result = "";
        var previous = "";
        var index = -1;
        foreach (var text in texts)
        {
            index++;
            var part = text.Trim();
            if (part.Length == 0) continue;
            var dictated = breaks != null && index < breaks.Count ? breaks[index] : 0;
            if (result.Length > 0 && dictated > 0) result += new string('\n', dictated) + part;
            else if (result.Length > 0 && (previous.Contains('\n') || part.Contains('\n'))) result += "\n\n" + part;
            else result = TranscriptJoiner.Join([result, part]);
            previous = part;
        }
        return result;
    }

    /// <summary>
    /// The app's clean-up of <paramref name="transcript"/> (or its translation, when the routes' options ask for one):
    /// each part hedged between the routes (see <see cref="HedgedPolish"/>) and asked a second time after a transient
    /// failure, but not after a timeout. A second wait as long as the first would only hold the dictation up; the part goes
    /// in as spoken instead. Nor when the system has no connection at all (<see cref="NetworkFailure.NoConnection"/>): a
    /// moment later it rarely has one.
    /// </summary>
    /// <param name="concurrency">
    /// How many parts are out at once (null: all of them, which is what a dictation wants; the transcript of an hour-long
    /// file has dozens).
    /// </param>
    /// <param name="holdWhile">
    /// With a cap, a part waits to start while this says so (a file's clean-up, while a dictation is under way).
    /// </param>
    /// <remarks>
    /// A dictation's clean-up (not a translation's, nor a file's, whose parts are many and go a few at a time) sends each
    /// part with the ends of the parts around it, and is laid out again as a whole where its layout calls for it (see
    /// <see cref="SegmentedPolish"/>).
    /// </remarks>
    public static async Task<SegmentedPolishResult> Run(string transcript, PolishRoute primary, PolishRoute? backup,
                                                        Action<string>? onPartial = null, CancellationToken cancellationToken = default,
                                                        int? concurrency = null, Func<bool>? holdWhile = null)
    {
        var policy = new RetryPolicy(MaxAttempts: 2, RetriesTimeouts: false,
                                     StopsEarly: failed => failed.Offline == NetworkFailure.NoConnection);
        var options = primary.Options;
        var dictation = concurrency is null;
        var result = await RunParts(transcript, (part, surrounding, partial, token) =>
                                    {
                                        PolishRoute With(PolishRoute route) => route with { Options = route.Options with { Surrounding = surrounding } };
                                        return policy.Run((_, ct) => HedgedPolish.Run(part, With(primary), backup is null ? null : With(backup),
                                                                                      cancellationToken: ct, onPartial: partial),
                                                          cancellationToken: token);
                                    },
                                    onPartial, cancellationToken, options.Translation, options.Vocabulary, options.Context,
                                    concurrency, holdWhile, options.DictatedLineBreaks,
                                    sendsSurrounding: dictation && options.Translation is null, strength: options.Strength).ConfigureAwait(false);
        if (!dictation || result.Parts.Count < 2 || !Prompts.Relayouts(options.Layout) || options.DictatedLineBreaks
            || WordCount.Count(result.Text) > RelayoutLimit)
        {
            return result;
        }
        return await Relaid(result, primary, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="result"/> with its joined text laid out again as a whole by <paramref name="route"/>'s layout, when
    /// the answer comes back within <see cref="RelayoutTimeout"/> and changed nothing but the layout; as it was otherwise.
    /// Throws only when cancelled.
    /// </summary>
    internal static async Task<SegmentedPolishResult> Relaid(SegmentedPolishResult result, PolishRoute route, CancellationToken cancellationToken)
    {
        var relayout = route with { Options = route.Options with { Relayout = true, Context = null, Surrounding = null, OnContextSent = null } };
        HedgedPolishResult answer;
        try
        {
            answer = await Timeouts.WithTimeout(RelayoutTimeout(result.Text),
                                                ct => HedgedPolish.Run(result.Text, relayout, null, cancellationToken: ct),
                                                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested || ApiException.From(error).IsCancelled) throw ApiException.Cancelled();
            return result;
        }
        var used = !answer.Result.Truncated && Prompts.OnlyLayoutChanged(result.Text, answer.Result.Text);
        return result with { Text = used ? answer.Result.Text.Trim() : result.Text, Relayout = answer, Relaid = used };
    }

    /// <summary>
    /// Cleans up the parts of <paramref name="transcript"/> with <paramref name="polish"/>, all at once (or
    /// <paramref name="concurrency"/> at a time), and puts each part's answer through <see cref="Prompts.Fallback"/>
    /// (<see cref="Prompts.TranslationFallback"/> when it's a translation into <paramref name="translation"/>): a part whose
    /// answer is unusable, or that got none, goes in as spoken. Throws only when no part got an answer (the first part's
    /// error), or when cancelled. <paramref name="onPartial"/> gets the parts' text so far, joined.
    /// <paramref name="vocabulary"/>: the speaker's terms, which a translation keeps as they are. <paramref name="context"/>:
    /// the text around the cursor the requests carry, if any. <paramref name="keepsLineBreaks"/>: the transcript's line
    /// breaks were dictated, and those between two parts are put back as they were.
    /// </summary>
    public static Task<SegmentedPolishResult> Run(string transcript,
                                                  Func<string, Action<string>?, CancellationToken, Task<HedgedPolishResult>> polish,
                                                  Action<string>? onPartial = null, CancellationToken cancellationToken = default,
                                                  TranslationTarget? translation = null, IReadOnlyList<string>? vocabulary = null,
                                                  string? context = null, int? concurrency = null, Func<bool>? holdWhile = null,
                                                  bool keepsLineBreaks = false) =>
        RunParts(transcript, (part, _, partial, token) => polish(part, partial, token), onPartial, cancellationToken, translation,
                 vocabulary, context, concurrency, holdWhile, keepsLineBreaks, sendsSurrounding: false);

    /// <summary>
    /// <see cref="Run(string, Func{string, Action{string}?, CancellationToken, Task{HedgedPolishResult}}, Action{string}?, CancellationToken, TranslationTarget?, IReadOnlyList{string}?, string?, int?, Func{bool}?, bool)"/>,
    /// with <paramref name="polish"/> given each part's surrounding parts (see <see cref="Surrounding"/>) when
    /// <paramref name="sendsSurrounding"/> and there's more than one part; an answer that copied from them beyond what
    /// the clean-up allows is then not used. <paramref name="strength"/>: how heavily the parts were cleaned up, which the
    /// guards allow for.
    /// </summary>
    public static async Task<SegmentedPolishResult> RunParts(string transcript,
                                                             Func<string, SurroundingParts?, Action<string>?, CancellationToken, Task<HedgedPolishResult>> polish,
                                                             Action<string>? onPartial = null, CancellationToken cancellationToken = default,
                                                             TranslationTarget? translation = null, IReadOnlyList<string>? vocabulary = null,
                                                             string? context = null, int? concurrency = null, Func<bool>? holdWhile = null,
                                                             bool keepsLineBreaks = false, bool sendsSurrounding = false,
                                                             PolishStrength strength = PolishStrength.Standard)
    {
        var split = PartsAndBreaks(transcript);
        var texts = split.Select(p => p.Text).ToList();
        int[] breaks = keepsLineBreaks ? split.Take(split.Count - 1).Select(p => p.BreaksAfter).Prepend(0).ToArray() : [];
        var clock = Stopwatch.StartNew();
        var progress = new PartialProgress(texts.Count, onPartial);
        var gate = concurrency is { } limit ? new SemaphoreSlim(Math.Max(1, limit)) : null;
        var surroundings = texts.Select((_, index) => sendsSurrounding ? Surrounding(texts, index) : null).ToList();
        var outcomes = await Task.WhenAll(texts.Select((text, index) => Attempt(text, index))).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) throw ApiException.Cancelled();
        var results = new List<SegmentedPolishResult.Part>();
        for (var i = 0; i < texts.Count; i++)
        {
            var (answer, error) = outcomes[i];
            if (answer != null)
            {
                var fallback = translation is null
                    ? Prompts.Fallback(texts[i], answer.Result.Text, answer.Result.Truncated, Reference(context, surroundings[i]), strength)
                    : Prompts.TranslationFallback(texts[i], answer.Result.Text, answer.Result.Truncated, translation, vocabulary);
                results.Add(new SegmentedPolishResult.Part(texts[i], answer, fallback, null));
            }
            else
            {
                var apiError = ApiException.From(error!);
                if (apiError.IsCancelled) throw apiError;
                results.Add(new SegmentedPolishResult.Part(texts[i], null, PolishFallback.Failed, apiError));
            }
        }
        // Nothing came back at all: the clean-up failed, just as a single request would have.
        if (results.All(p => p.Answer == null) && results[0].Error is { } first) throw first;
        var firstToken = results.Where(p => p.Answer != null).Select(p => p.Answer!.FirstTokenSeconds).DefaultIfEmpty(0).Min();
        return new SegmentedPolishResult(Join(results.Select(p => p.Text), breaks), results, firstToken, clock.Elapsed.TotalSeconds);

        async Task<(HedgedPolishResult? Answer, Exception? Error)> Attempt(string text, int index)
        {
            if (gate != null)
            {
                // One that waited its turn past a cancellation doesn't start at all.
                try
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return (null, ApiException.Cancelled());
                }
                // The turn can come with the cancellation, freed by the part it stopped: the semaphore doesn't check it then.
                if (cancellationToken.IsCancellationRequested)
                {
                    gate.Release();
                    return (null, ApiException.Cancelled());
                }
            }
            try
            {
                if (gate != null && holdWhile != null)
                {
                    while (holdWhile()) await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                return (await polish(text, surroundings[index], partial => progress.Update(index, partial), cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception error)
            {
                return (null, error);
            }
            finally
            {
                gate?.Release();
            }
        }
    }

    /// <summary>
    /// Everything a part's request carried as reference, for <see cref="Prompts.CopiedContext"/>: the text around the cursor
    /// and the ends of the parts around it. Null when there was none.
    /// </summary>
    internal static string? Reference(string? context, SurroundingParts? surrounding)
    {
        var all = new[] { context, surrounding?.Before, surrounding?.After }.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return all.Count == 0 ? null : string.Join("\n", all);
    }

    // MARK: - Splitting

    /// <summary>
    /// Cuts <paramref name="text"/> after every character <paramref name="isEnd"/> accepts (given the character after it).
    /// What follows an end (spaces, more punctuation, a closing quote or bracket) stays with the piece it ends.
    /// </summary>
    internal static List<string> Split(string text, Func<Rune, Rune?, bool> isEnd)
    {
        var runes = text.EnumerateRunes().ToArray();
        var pieces = new List<string>();
        var current = new StringBuilder();
        var ended = false;
        for (var i = 0; i < runes.Length; i++)
        {
            if (ended && !Trails(runes[i]))
            {
                pieces.Add(current.ToString());
                current.Clear();
                ended = false;
            }
            current.Append(runes[i].ToString());
            if (isEnd(runes[i], i + 1 < runes.Length ? runes[i + 1] : null)) ended = true;
        }
        if (current.Length > 0) pieces.Add(current.ToString());
        return pieces;
    }

    private static bool Trails(Rune rune) => Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune)
        is UnicodeCategory.ClosePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    /// <summary>"." only ends a sentence before a space or at the end, so "3.5" and "macOS 27.1" stay whole.</summary>
    internal static bool IsSentenceEnd(Rune rune, Rune? next) =>
        rune.Value is '\n' or '\r' or '。' or '！' or '？' or '；' or '…' or '!' or '?' or ';'
        || (rune.Value == '.' && (next is not { } following || Rune.IsWhiteSpace(following)));

    /// <summary>Likewise "," and ":" only before a space, so "1,000" and "10:30" stay whole.</summary>
    internal static bool IsClauseEnd(Rune rune, Rune? next) =>
        rune.Value is '，' or '、' or '：'
        || (rune.Value is ',' or ':' && (next is not { } following || Rune.IsWhiteSpace(following)));

    internal static bool IsWordEnd(Rune rune, Rune? next) => Rune.IsWhiteSpace(rune) || WordCount.IsCjkWordCharacter(rune.Value);

    /// <summary>Each part's text so far, passed on joined, so the progress bar moves with all of them together.</summary>
    private sealed class PartialProgress(int count, Action<string>? onChange)
    {
        private readonly string[] _texts = Enumerable.Repeat("", count).ToArray();

        public void Update(int index, string text)
        {
            if (onChange is null) return;
            // Under the lock, so the joined texts are passed on in order.
            lock (_texts)
            {
                _texts[index] = text;
                onChange(_texts.Length == 1 ? text : Join(_texts));
            }
        }
    }
}
