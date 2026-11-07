namespace TypelessCore;

/// <summary>
/// What turns speech into text: the speech-to-text provider (the default, also when the setting is missing) or a model
/// running on this PC, which needs no API key. Stored in <c>settings.json</c> under <c>speechEngine</c> as
/// <c>"provider"</c> or <c>"onDevice"</c>, as on macOS.
/// </summary>
public enum SpeechEngine { Provider, OnDevice }

public static class SpeechEngines
{
    public static string RawValue(this SpeechEngine engine) => engine == SpeechEngine.OnDevice ? "onDevice" : "provider";

    /// <summary>The saved value; anything but <c>"onDevice"</c> (missing, empty, unknown) is the provider.</summary>
    public static SpeechEngine Parse(string? raw) => raw?.Trim() == "onDevice" ? SpeechEngine.OnDevice : SpeechEngine.Provider;
}

/// <summary>What a dictation needs before it can start, and whether it is cleaned up, with either speech engine.</summary>
public static class SpeechSetup
{
    /// <summary>
    /// Whether speech can be turned into text: with the provider, its key (or a local server's address); on this PC,
    /// the downloaded model. The provider's key doesn't matter on-device, so a missing one isn't "Setup needed".
    /// </summary>
    public static bool SpeechReady(SpeechEngine engine, bool providerConfigured, bool modelReady) =>
        engine == SpeechEngine.OnDevice ? modelReady : providerConfigured;

    /// <summary>
    /// Whether a dictation is cleaned up. On-device recognition is a way to start with no key at all: with clean-up on
    /// but no provider for it, the text goes in as spoken, without an error. With the speech-to-text provider, a clean-up
    /// with no key fails as before (and the transcript goes in), so the missing key is noticed.
    /// </summary>
    public static bool CleansUp(bool polishEnabled, SpeechEngine engine, bool polishConfigured) =>
        polishEnabled && (polishConfigured || engine == SpeechEngine.Provider);
}

/// <summary>The spoken languages the on-device model (SenseVoice Small) knows.</summary>
public static class OnDeviceLanguages
{
    /// <summary>Chinese (Mandarin), English, Japanese, Korean and Cantonese.</summary>
    public static readonly IReadOnlyList<string> Supported = ["zh", "en", "ja", "ko", "yue"];

    /// <summary>
    /// The model's language for a spoken-language hint (global or an app profile's): "" for none (the model detects it),
    /// the code for one it knows, null for one it doesn't ("fr"), which the dictation can't be transcribed in.
    /// </summary>
    public static string? ModelLanguage(string? hint)
    {
        var code = (hint ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        if (code.Length == 0 || code == "auto") return "";
        // "zh-Hans", "en-US": the language, not the script or region; Cantonese however it's written.
        if (code is "zh-hk" or "zh-yue" or "yue-hk") return "yue";
        var primary = code.Split('-')[0];
        return Supported.Contains(primary) ? primary : null;
    }
}

/// <summary>A speech model loaded in memory. Called one recognition at a time (see <see cref="OnDeviceTranscriber"/>).</summary>
public interface ILocalSpeechModel : IDisposable
{
    /// <summary>The text of 16 kHz mono audio (samples from −1 to 1); <paramref name="language"/> "" to detect it.</summary>
    string Recognize(float[] samples, string language);
}

/// <summary>
/// Speech-to-text on this PC, as a route for <see cref="TranscriptionPipeline"/> in place of a provider: chunking at
/// pauses, the speculative tail, the extra round for failed chunks, History, Retry and files all work as with a
/// provider. No backup route, hedging or preconnect: there is no network to be slow.
///
/// The model is loaded on first use (about a second; see <see cref="Warm"/>) and kept. Recognitions run one at a time on
/// the thread pool, never on the capture or UI thread: each already uses several cores. Nothing is billed, so a
/// result carries no usage and the dictation costs $0 (its words and time still count on Home).
/// </summary>
public sealed class OnDeviceTranscriber : ISpeechTranscriber
{
    /// <summary>The model ID History and the logs name on-device transcription by.</summary>
    public const string ModelId = "sensevoice-small";

    private readonly Func<ILocalSpeechModel> _load;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ILocalSpeechModel? _model;

    /// <param name="load">Loads the model (slow, may throw, e.g. <see cref="ApiException.ModelMissing"/>); called under the lock.</param>
    public OnDeviceTranscriber(Func<ILocalSpeechModel> load)
    {
        _load = load;
    }

    /// <summary>The options a pipeline transcribes with on-device: only the language hint matters.</summary>
    public static TranscriptionOptions Options(string? language) => new(ModelId, string.IsNullOrWhiteSpace(language) ? null : language.Trim());

    /// <summary>How many recognitions have run since the model was loaded (for tests and the log).</summary>
    public int Recognitions { get; private set; }

    /// <summary>Whether the model is loaded.</summary>
    public bool IsLoaded => Volatile.Read(ref _model) != null;

    /// <summary>Loads the model in the background, if it isn't yet, so the first chunk doesn't wait for it. Failures wait for that chunk.</summary>
    public void Warm()
    {
        if (IsLoaded) return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_model == null) Volatile.Write(ref _model, _load());
            }
            catch
            {
                // The chunk that needs the model loads it again and reports why it can't.
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>Frees the model's memory (it's loaded again when needed): before its files are removed, or when another engine is chosen.</summary>
    public async Task Unload()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var model = _model;
            Volatile.Write(ref _model, null);
            model?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One chunk. A language the model doesn't know fails at once, permanently (no retry would help): the dictation is
    /// kept and can be retried from History in another language. Nothing is retried here: a recognition that failed
    /// fails the same way again; <see cref="TranscriptionPipeline.Finish"/>'s extra round still covers a fluke.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeDetailedWithRetry(short[] samples, TranscriptionOptions options, RetryPolicy? policy = null,
                                                                       Action<int, ApiException>? onRetry = null,
                                                                       CancellationToken cancellationToken = default)
    {
        if (OnDeviceLanguages.ModelLanguage(options.Language) is not { } language) throw ApiException.UnsupportedLanguage();
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw ApiException.Cancelled();
        }
        try
        {
            if (cancellationToken.IsCancellationRequested) throw ApiException.Cancelled();
            var text = await Task.Run(() =>
            {
                var model = _model ?? _load();
                Volatile.Write(ref _model, model);
                Recognitions++;
                return model.Recognize(Floats(samples), language);
            }, CancellationToken.None).ConfigureAwait(false);
            return new TranscriptionResult(text.Trim(), null);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw ApiException.OnDevice(error.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>16-bit samples as the model takes them, from −1 to 1.</summary>
    internal static float[] Floats(short[] samples)
    {
        var floats = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) floats[i] = samples[i] / 32768f;
        return floats;
    }
}
