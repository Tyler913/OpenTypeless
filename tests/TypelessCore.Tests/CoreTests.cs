using System.Text.Json.Nodes;

namespace TypelessCore.Tests;

public class WavTests
{
    [Fact]
    public void RoundTrip()
    {
        short[] samples = [0, 1, -1, 32767, -32768, 1234];
        var wav = Wav.Encode(samples);
        Assert.Equal(44 + samples.Length * 2, wav.Length);
        Assert.Equal(16_000u, Wav.ReadLE32(wav, 24));
        Assert.Equal((uint)(samples.Length * 2), Wav.ReadLE32(wav, 40));
        Assert.Equal(samples, Wav.DecodeSamples(wav));
    }

    [Fact]
    public void FileWriterProducesValidFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            var writer = new WavFileWriter(path);
            writer.Append(new short[] { 1, 2, 3 });
            writer.FinalizeHeader();
            writer.Append(new short[] { 4, 5 });
            writer.Close();
            var data = File.ReadAllBytes(path);
            Assert.Equal(new short[] { 1, 2, 3, 4, 5 }, Wav.DecodeSamples(data));
            Assert.Equal((uint)(36 + 10), Wav.ReadLE32(data, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ChunkerTests
{
    /// <summary>Loud "speech" with quiet gaps at the given seconds.</summary>
    internal static short[] Signal(double seconds, double[] gaps)
    {
        var n = AudioFormat.SampleCount(seconds);
        var output = new short[n];
        for (var i = 0; i < n; i++)
        {
            var t = AudioFormat.Seconds(i);
            var inGap = gaps.Any(g => Math.Abs(t - g) < 0.25);
            output[i] = inGap ? (short)0 : (short)(8000 * Math.Sin(i * 0.3));
        }
        return output;
    }

    [Fact]
    public void CutsAtQuietestPoint()
    {
        var chunker = new Chunker();
        var audio = Signal(70, [23.0, 47.5]);
        var chunks = new List<AudioChunk>();
        // Feed in 100 ms blocks like the audio engine does.
        var block = AudioFormat.SampleCount(0.1);
        for (var i = 0; i < audio.Length; i += block)
        {
            chunks.AddRange(chunker.Append(audio.AsSpan(i, Math.Min(block, audio.Length - i))));
        }
        if (chunker.Finish() is { } tail) chunks.Add(tail);

        Assert.Equal(3, chunks.Count);
        Assert.True(Math.Abs(chunks[0].Duration - 23.0) < 0.1);
        Assert.True(Math.Abs(chunks[1].StartTime - 23.0) < 0.1);
        Assert.True(Math.Abs(chunks[1].StartTime + chunks[1].Duration - 47.5) < 0.1);
        Assert.Equal([0, 1, 2], chunks.Select(c => c.Index));
        // No audio lost or duplicated.
        Assert.Equal(audio.Length, chunks.Sum(c => c.Samples.Length));
        Assert.Equal(audio, chunks.SelectMany(c => c.Samples));
    }

    [Fact]
    public void NoChunkExceedsMax()
    {
        var chunker = new Chunker();
        var audio = Signal(125, []);
        var chunks = chunker.Append(audio);
        if (chunker.Finish() is { } tail) chunks.Add(tail);
        foreach (var chunk in chunks) Assert.True(chunk.Duration <= 28.0 + 0.001);
        Assert.Equal(audio.Length, chunks.Sum(c => c.Samples.Length));
    }

    [Fact]
    public void ShortRecordingIsSingleChunk()
    {
        var chunker = new Chunker();
        Assert.Empty(chunker.Append(Signal(5, [])));
        Assert.True(Math.Abs((chunker.Finish()?.Duration ?? 0) - 5) < 0.01);
        Assert.Null(chunker.Finish());
    }

    [Fact]
    public void SilenceDetection()
    {
        Assert.True(AudioLevel.IsSilent(Enumerable.Repeat((short)20, 16000).ToArray()));
        Assert.False(AudioLevel.IsSilent(Signal(1, [])));
    }
}

public class JoinerTests
{
    [Fact]
    public void Join()
    {
        Assert.Equal("我想做一个语音输入软件。", TranscriptJoiner.Join(["我想做一个", "语音输入软件。"]));
        Assert.Equal("Hello there general Kenobi.", TranscriptJoiner.Join(["Hello there", "general Kenobi."]));
        Assert.Equal("用 SwiftUI写界面", TranscriptJoiner.Join(["用 SwiftUI", "写界面"]));
        Assert.Equal("done. Next", TranscriptJoiner.Join(["done.", " Next"]));
        Assert.Equal("a", TranscriptJoiner.Join(["", "  a ", ""]));
    }
}

public class SseParserTests
{
    [Fact]
    public void ParsesContentAndDone()
    {
        var p = new SseParser();
        Assert.Empty(p.Parse(": OPENROUTER PROCESSING"));
        Assert.Empty(p.Parse(""));
        Assert.Equal([new SseEvent.Content("你好")], p.Parse("""data: {"choices":[{"delta":{"content":"你好"}}]}"""));
        Assert.Equal([new SseEvent.Finished("length")], p.Parse("""data: {"choices":[{"delta":{},"finish_reason":"length"}]}"""));
        Assert.Equal([new SseEvent.Done()], p.Parse("data: [DONE]"));
        Assert.IsType<SseEvent.Error>(p.Parse("""data: {"error":{"message":"boom"}}""").First());
    }
}

public class RetryTests
{
    [Fact]
    public void Classification()
    {
        Assert.True(ApiException.Http(429, "").IsRetryable);
        Assert.True(ApiException.Http(503, "").IsRetryable);
        Assert.True(ApiException.TimedOut(1).IsRetryable);
        Assert.False(ApiException.Http(401, "").IsRetryable);
        Assert.False(ApiException.Http(402, "").IsRetryable);
        Assert.False(ApiException.MissingApiKey("x").IsRetryable);
    }

    [Fact]
    public async Task RetriesThenSucceeds()
    {
        var policy = new RetryPolicy(MaxAttempts: 3, BaseDelay: 0.01);
        var calls = 0;
        var value = await policy.Run<int>((_, _) =>
        {
            calls += 1;
            if (calls < 3) throw ApiException.Http(502, "bad gateway");
            return Task.FromResult(42);
        });
        Assert.Equal(42, value);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DoesNotRetryAuthErrors()
    {
        var policy = new RetryPolicy(MaxAttempts: 3, BaseDelay: 0.01);
        var calls = 0;
        await Assert.ThrowsAsync<ApiException>(() => policy.Run<int>((_, _) =>
        {
            calls += 1;
            throw ApiException.Http(401, "");
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Timeout()
    {
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            Timeouts.WithTimeout(0.05, async _ => { await Task.Delay(2000, CancellationToken.None); return 1; }));
        Assert.Equal(ApiException.TimedOut(0.05), error);
    }
}

public class PromptTests
{
    [Fact]
    public void Sanitize()
    {
        Assert.Equal("hello", Prompts.SanitizePolishOutput("```\nhello\n```"));
        Assert.Equal("hi", Prompts.SanitizePolishOutput("<transcript>\nhi\n</transcript>"));
    }

    [Fact]
    public void AnswerDetection()
    {
        Assert.False(Prompts.LooksLikeAnAnswer(new string('嗯', 300), new string('字', 250)));
        Assert.True(Prompts.LooksLikeAnAnswer("帮我写一个快速排序", new string('x', 800)));
    }

    [Fact]
    public void ReasoningConfigBody()
    {
        var mandatory = new ModelInfo("g", "g", ["reasoning"], true, ["high", "low", "minimal"]);
        Assert.Equal("minimal", (string?)ReasoningConfig.Body(mandatory)?["effort"]);
        var optional = new ModelInfo("q", "q", ["reasoning"], false, []);
        Assert.False((bool)ReasoningConfig.Body(optional)!["enabled"]!);
        var none = new ModelInfo("n", "n", [], false, []);
        Assert.Null(ReasoningConfig.Body(none));
    }

    [Fact]
    public void PromptIsIdenticalToMacOS()
    {
        // Spot checks that the verbatim copy kept its structure (sections, examples, closing example).
        var prompt = Prompts.PolishSystemPrompt([], "");
        Assert.StartsWith("You turn a raw speech-to-text transcript", prompt);
        Assert.Contains("\n\n## How to work\n", prompt);
        Assert.Contains("\n\n## Layout\n", prompt);
        Assert.Contains("\n\n## Examples\n<transcript>嗯我们周三下午三点开会", prompt);
        Assert.EndsWith("Send the report to Sarah by Friday, and cc the finance team.", prompt);
        var extended = Prompts.PolishSystemPrompt([" Claude ", "", "SwiftUI"], "  keep it casual \n");
        Assert.EndsWith("\n\n## Vocabulary\nThese terms may appear; spell them exactly like this: Claude, SwiftUI"
                        + "\n\n## Additional preferences from the speaker\nkeep it casual", extended);
    }
}
