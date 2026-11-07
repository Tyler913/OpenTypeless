using System.Reflection;
using SherpaOnnx;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// SenseVoice Small loaded through sherpa-onnx (its native win-x64 / win-arm64 runtime and ONNX Runtime ship next to
/// the app). Used one recognition at a time by <see cref="OnDeviceTranscriber"/>.
/// </summary>
internal sealed class SenseVoiceRecognizer : ILocalSpeechModel
{
    private readonly OfflineRecognizer _recognizer;
    private OfflineRecognizerConfig _config;

    /// <exception cref="ApiException">The files couldn't be loaded as a model.</exception>
    public SenseVoiceRecognizer(string model, string tokens)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioFormat.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.SenseVoice.Model = model;
        config.ModelConfig.SenseVoice.Language = "";
        // Punctuation, and numbers written as digits, as typed text has them.
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = tokens;
        // Half the cores, up to 4: enough for a chunk in a fraction of its length, leaving the rest for the app in front.
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        _config = config;
        _recognizer = new OfflineRecognizer(config);
        // sherpa-onnx returns no recognizer for files it can't load, and its wrapper doesn't check: using that would crash.
        if (Handle(_recognizer) == IntPtr.Zero) throw ApiException.OnDevice(L("无法加载语音模型", "the speech model couldn't be loaded"));
    }

    public string Recognize(float[] samples, string language)
    {
        if (_config.ModelConfig.SenseVoice.Language != language)
        {
            _config.ModelConfig.SenseVoice.Language = language;
            _recognizer.SetConfig(_config);
        }
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(AudioFormat.SampleRate, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text;
    }

    public void Dispose() => _recognizer.Dispose();

    /// <summary>The native recognizer behind the wrapper (its private handle), or a non-zero stand-in if that can't be read.</summary>
    private static IntPtr Handle(OfflineRecognizer recognizer)
    {
        var field = typeof(OfflineRecognizer).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(recognizer) is System.Runtime.InteropServices.HandleRef handle ? handle.Handle : new IntPtr(1);
    }
}
