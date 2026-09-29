using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace OpenTypeless.Audio;

/// <summary>
/// Shows what's being said above the recording capsule while the user talks, recognised by Windows' own continuous
/// dictation. Only a preview: nothing it produces is inserted or saved; the dictation's text still comes from the
/// speech-to-text provider. Windows' recogniser listens to the default microphone by itself and needs the speech
/// language installed (and, for dictation, online speech recognition allowed in Privacy settings); when any of that
/// is missing the preview simply stays empty.
/// </summary>
public sealed class LivePreview
{
    private readonly Action<string> _onText;
    private SpeechRecognizer? _recognizer;
    private string _finished = "";
    private bool _stopped;

    /// <param name="onText">Gets everything recognised so far, on a background thread.</param>
    public LivePreview(Action<string> onText)
    {
        _onText = onText;
    }

    /// <summary>The last reason the preview couldn't start, for the settings page.</summary>
    public static string? LastProblem { get; private set; }

    /// <summary>Starts recognising <paramref name="language"/> (an ISO code such as "zh", or "" for the system language).</summary>
    public async void Start(string language)
    {
        try
        {
            var recognizer = Tag(language) is { } tag ? new SpeechRecognizer(new Language(tag)) : new SpeechRecognizer();
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success || _stopped)
            {
                LastProblem = compiled.Status.ToString();
                recognizer.Dispose();
                return;
            }
            recognizer.HypothesisGenerated += (_, e) => _onText(_finished + e.Hypothesis.Text);
            recognizer.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
            {
                if (e.Result.Status != SpeechRecognitionResultStatus.Success || e.Result.Text.Length == 0) return;
                _finished += e.Result.Text + " ";
                _onText(_finished);
            };
            _recognizer = recognizer;
            await recognizer.ContinuousRecognitionSession.StartAsync();
            LastProblem = null;
            if (_stopped) Stop();
        }
        catch (Exception error)
        {
            // Most often "The speech privacy policy was not accepted" (online speech recognition is off).
            LastProblem = error.Message;
            Services.AppLog.Debug("preview", "live preview unavailable: " + error.Message);
        }
    }

    public async void Stop()
    {
        _stopped = true;
        if (_recognizer is not { } recognizer) return;
        _recognizer = null;
        try
        {
            await recognizer.ContinuousRecognitionSession.CancelAsync();
        }
        catch (Exception) { }
        recognizer.Dispose();
    }

    /// <summary>The installed recognizer language for an ISO code: the usual region when present ("zh" → zh-CN), otherwise
    /// any installed variant ("pt" → pt-PT).</summary>
    private static string? Tag(string language)
    {
        if (language.Length == 0) return null;
        var installed = SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag).ToList();
        var usual = language switch
        {
            "zh" => "zh-CN", "en" => "en-US", "ja" => "ja-JP", "ko" => "ko-KR", "es" => "es-ES",
            "pt" => "pt-BR", "fr" => "fr-FR", "de" => "de-DE", "ru" => "ru-RU", _ => null,
        };
        return installed.FirstOrDefault(tag => string.Equals(tag, usual, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(tag => tag.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
    }
}
