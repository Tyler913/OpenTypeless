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

    /// <summary>Every answer that came back, used or not, for pricing.</summary>
    public IReadOnlyList<HedgedPolishResult> Answers => Parts.Select(p => p.Answer).OfType<HedgedPolishResult>().ToList();

    /// <summary>The parts that go in as spoken.</summary>
    public IReadOnlyList<Part> Fallbacks => Parts.Where(p => p.Fallback != null).ToList();

    /// <summary>The model that answered (the first part's, should the backup have answered some parts).</summary>
    public string Model => Answers.FirstOrDefault()?.Model ?? "";

    public bool UsedBackup => Answers.Any(a => a.UsedBackup);
}

/// <summary>
/// Long transcripts are cleaned up in parts. Asked to clean up a long dictation in one go, models summarise it instead:
/// of 54 real dictations of a minute or more, 15 came back with under 60% of their words (the worst 2,023 → 379), often
/// rewritten into numbered lists. A part of a few hundred words gets edited as asked. The parts are split at sentence
/// ends, cleaned up in parallel with the same prompt, checked one by one, and joined again, so a long dictation is also
/// ready sooner. Each part goes on its own: the prompt has no place for read-only context, and a neighbouring part
/// pasted in as such could end up cleaned up into the answer as well.
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
    /// The app's clean-up of <paramref name="transcript"/>: each part hedged between the routes (see
    /// <see cref="HedgedPolish"/>) and asked a second time after a transient failure, but not after a timeout. A second wait
    /// as long as the first would only hold the dictation up; the part goes in as spoken instead.
    /// </summary>
    public static Task<SegmentedPolishResult> Run(string transcript, PolishRoute primary, PolishRoute? backup,
                                                  Action<string>? onPartial = null, CancellationToken cancellationToken = default)
    {
        var policy = new RetryPolicy(MaxAttempts: 2, RetriesTimeouts: false);
        return Run(transcript, (part, partial, token) => policy.Run(
                       (_, ct) => HedgedPolish.Run(part, primary, backup, cancellationToken: ct, onPartial: partial), cancellationToken: token),
                   onPartial, cancellationToken, keepsLineBreaks: primary.Options.DictatedLineBreaks);
    }

    /// <summary>
    /// Cleans up the parts of <paramref name="transcript"/> with <paramref name="polish"/>, all at once, and puts each part's
    /// answer through <see cref="Prompts.Fallback"/>: a part whose answer is unusable, or that got none, goes in as spoken.
    /// Throws only when no part got an answer (the first part's error), or when cancelled. <paramref name="onPartial"/>
    /// gets the parts' text so far, joined.
    /// </summary>
    public static async Task<SegmentedPolishResult> Run(string transcript,
                                                        Func<string, Action<string>?, CancellationToken, Task<HedgedPolishResult>> polish,
                                                        Action<string>? onPartial = null, CancellationToken cancellationToken = default,
                                                        bool keepsLineBreaks = false)
    {
        var split = PartsAndBreaks(transcript);
        var texts = split.Select(p => p.Text).ToList();
        int[] breaks = keepsLineBreaks ? split.Take(split.Count - 1).Select(p => p.BreaksAfter).Prepend(0).ToArray() : [];
        var clock = Stopwatch.StartNew();
        var progress = new PartialProgress(texts.Count, onPartial);
        var outcomes = await Task.WhenAll(texts.Select((text, index) => Attempt(text, index))).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) throw ApiException.Cancelled();
        var results = new List<SegmentedPolishResult.Part>();
        for (var i = 0; i < texts.Count; i++)
        {
            var (answer, error) = outcomes[i];
            if (answer != null)
            {
                var fallback = Prompts.Fallback(texts[i], answer.Result.Text, answer.Result.Truncated);
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
            try
            {
                return (await polish(text, partial => progress.Update(index, partial), cancellationToken).ConfigureAwait(false), null);
            }
            catch (Exception error)
            {
                return (null, error);
            }
        }
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
