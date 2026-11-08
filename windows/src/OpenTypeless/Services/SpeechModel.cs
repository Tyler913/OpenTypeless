using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// The speech model for on-device recognition: its download into <c>%LOCALAPPDATA%\OpenTypeless\Models</c> (see
/// <see cref="ModelStore"/>) and, once it's there, the transcriber the pipeline uses instead of a provider. One for the
/// app (and the CLI, which shares the folder).
/// </summary>
public sealed class SpeechModel
{
    public static SpeechModel Shared { get; } = new();

    public ModelStore Store { get; }
    public OnDeviceTranscriber Transcriber { get; }

    private SpeechModel()
    {
        var model = SpeechModelInfo.SenseVoiceSmall;
        Store = new ModelStore(model, Path.Combine(AppPaths.Models, model.Id), new HttpModelFetcher());
        Transcriber = new OnDeviceTranscriber(() =>
        {
            if (!Store.IsReady) throw ApiException.ModelMissing();
            return new SenseVoiceRecognizer(Store.PathOf("model.int8.onnx"), Store.PathOf("tokens.txt"));
        });
        Store.Changed += () =>
        {
            AppLog.Debug("model", Store.State switch
            {
                ModelState.Downloading d => $"downloading {d.Percent}%",
                ModelState.Failed f => "download failed: " + f.Message,
                var state => state.GetType().Name.ToLowerInvariant(),
            });
        };
    }

    public bool IsReady => Store.IsReady;

    /// <summary>How far a download that's under way has got (100 while the last file is checked); null when none is.</summary>
    public static int? DownloadPercent(ModelState state) => state switch
    {
        ModelState.Downloading d => d.Percent,
        ModelState.Verifying => 100,
        _ => null,
    };

    /// <summary>Frees the model's memory, then deletes its files.</summary>
    /// <returns>False when a file couldn't be deleted.</returns>
    public async Task<bool> Remove()
    {
        await Transcriber.Unload();
        return await Store.Remove();
    }

    /// <summary>
    /// What stops speech from being turned into text, as a short line and the settings page that fixes it ("providers"
    /// or "models"); null when nothing does. On-device needs the model, not a key; the provider needs its key.
    /// </summary>
    public static (string Message, string Page)? SetupIssue(AppSettings settings)
    {
        if (settings.SpeechEngine == SpeechEngine.OnDevice)
        {
            var state = Shared.Store.State;
            if (state is ModelState.Ready) return null;
            if (DownloadPercent(state) is { } percent)
            {
                return (L($"正在下载语音模型… {percent}%", $"Downloading the speech model… {percent}%"), "models");
            }
            return (L("请先在设置 → 模型里下载语音模型", "Download the speech model in Settings → Models first"), "models");
        }
        if (settings.SttEndpoint != null && settings.IsConfigured(settings.SttProvider)) return null;
        var name = settings.SttProvider.DisplayName();
        return (L($"请先在设置里配置语音转文字服务商（{name}）", $"Set up the speech-to-text provider ({name}) in Settings first"), "providers");
    }

    /// <summary>
    /// The model as the provider's stand-in while the network is down (see <see cref="OfflineFallback"/>): only once it's
    /// downloaded and knows <paramref name="language"/>, since nothing can be downloaded then. A provider on this PC itself
    /// (a local server) doesn't need the network, so only its own failures count for it, not the system's word.
    /// </summary>
    public static OfflineFallbackRoute OfflineRoute(string? language, ProviderEndpoint endpoint)
    {
        var local = OfflineFallback.IsLocalHost(endpoint.BaseUrl.Host);
        return new OfflineFallbackRoute(Shared.Transcriber, OnDeviceTranscriber.Options(language),
                                        () => Shared.IsReady && OnDeviceLanguages.ModelLanguage(language) != null,
                                        () => !local && NetworkStatus.IsDown);
    }
}

/// <summary>
/// Whether this PC has a network connection at all (any adapter up that isn't a loopback or tunnel), so a dictation goes
/// to the model on this PC at once while it hasn't, rather than waiting on requests that can't get out (see
/// <see cref="OfflineFallback"/>). Read again whenever Windows says that changed; until the first answer the network
/// counts as up.
/// </summary>
public static class NetworkStatus
{
    private static volatile bool _down;
    private static int _started;

    /// <summary>Starts watching, once (at launch).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, args) => _down = !args.IsAvailable;
        _ = Task.Run(() =>
        {
            try
            {
                _down = !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            }
            catch (Exception error)
            {
                AppLog.Debug("network", "can't tell whether the network is up: " + error.Message);
            }
        });
    }

    public static bool IsDown => _down;
}
