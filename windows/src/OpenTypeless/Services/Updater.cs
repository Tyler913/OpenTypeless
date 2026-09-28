using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using TypelessCore;

namespace OpenTypeless.Services;

/// <summary>
/// Keeps the app up to date from GitHub Releases: the counterpart of the macOS Updater.
///
/// Checks shortly after launch and then once a day, downloads a newer zip in the background, and installs it when
/// the user clicks Restart to update. A running .exe can't replace itself, so the unpacked new version is started
/// with <c>--apply-update</c> (see <see cref="UpdateInstaller"/>): it waits for this copy to exit, swaps the install
/// folder and starts the result. Nothing is installed while a dictation is being recorded or processed.
///
/// Before anything is replaced, the zip's SHA-256 must match the digest GitHub publishes and the unpacked app must
/// carry the expected version.
/// </summary>
public sealed class Updater
{
    public static Updater Shared { get; } = new();

    public enum UpdatePhase
    {
        Idle,
        Checking,
        UpToDate,
        /// <summary>A newer version exists but can't be installed from inside the app (see <see cref="ManualReason"/>).</summary>
        Available,
        Downloading,
        /// <summary>Downloaded and verified; installs on Restart to update.</summary>
        Ready,
        Installing,
        Failed,
    }

    private readonly AppSettings _settings = AppSettings.Shared;
    private DispatcherQueueTimer? _timer;
    private bool _busy;
    private string? _stagedDirectory;

    /// <summary>Raised on the UI thread whenever anything below changes.</summary>
    public event Action? Changed;

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public AvailableUpdate? Update { get; private set; }
    /// <summary>Why the found update has to be downloaded by hand, if it does.</summary>
    public string? ManualReason { get; private set; }
    /// <summary>Restart to update was clicked during a dictation; it installs as soon as that finishes.</summary>
    public bool InstallPending { get; private set; }

    /// <summary>Set by the app: true while no dictation is being recorded or processed.</summary>
    public Func<bool> CanInstallNow { get; set; } = () => true;
    /// <summary>Set by the app: quits so the installer can replace the files.</summary>
    public Action? Quit { get; set; }

    public AppVersion CurrentVersion { get; } =
        AppVersion.FromSystemVersion(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public DateTimeOffset? LastChecked => _settings.LastUpdateCheck;

    /// <summary>A build running straight out of the source tree has nothing to update.</summary>
    public bool IsDevelopmentBuild => LaunchAtLogin.IsDevelopmentBuild;

    public bool CanSkip => Update != null && Phase is UpdatePhase.Available or UpdatePhase.Ready;

    /// <summary>The folder this copy runs from, e.g. <c>%LOCALAPPDATA%\Programs\OpenTypeless</c>.</summary>
    public static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary><c>%LOCALAPPDATA%\OpenTypeless\Updates</c>: one folder per downloaded version.</summary>
    private static string UpdatesDirectory => Path.Combine(AppPaths.Support, "Updates");

    private static string Platform => UpdateCheck.WindowsPlatform(RuntimeInformation.ProcessArchitecture == Architecture.Arm64);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private Updater() { }

    // MARK: Schedule

    public void Start()
    {
        if (IsDevelopmentBuild) return;
        ReportPreviousInstall();
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var launch = dispatcher.CreateTimer();
        launch.Interval = TimeSpan.FromSeconds(10);
        launch.IsRepeating = false;
        launch.Tick += (_, _) =>
        {
            CleanUp();
            CheckIfDue(launch: true);
        };
        launch.Start();
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromHours(1);
        _timer.Tick += (_, _) => CheckIfDue(launch: false);
        _timer.Start();
    }

    private void CheckIfDue(bool launch)
    {
        if (!_settings.AutoCheckUpdates || _busy) return;
        if (Phase is not (UpdatePhase.Idle or UpdatePhase.UpToDate or UpdatePhase.Failed)) return;
        var due = launch || LastChecked is not { } last || DateTimeOffset.Now - last >= TimeSpan.FromHours(24);
        if (due) _ = Check(userInitiated: false);
    }

    /// <summary>After an install restart: if the version didn't change, the installer couldn't swap the folder.</summary>
    private void ReportPreviousInstall()
    {
        var raw = _settings.InstallingUpdateVersion;
        if (raw.Length == 0) return;
        _settings.InstallingUpdateVersion = "";
        if (AppVersion.Parse(raw) is { } target && CurrentVersion < target)
        {
            Fail(L($"没能安装 {target}，已继续使用 {CurrentVersion}", $"Couldn't install {target}; still on {CurrentVersion}"));
        }
    }

    /// <summary>Removes the previous install folder and downloads that are no newer than this version.</summary>
    private void CleanUp()
    {
        try
        {
            var backup = InstallDirectory + ".old";
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (!Directory.Exists(UpdatesDirectory)) return;
        foreach (var folder in Directory.GetDirectories(UpdatesDirectory))
        {
            if (AppVersion.Parse(Path.GetFileName(folder)) is { } version && version > CurrentVersion) continue;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // MARK: Check and download

    public void CheckNow() => _ = Check(userInitiated: true);

    private async Task Check(bool userInitiated)
    {
        if (_busy) return;
        _busy = true;
        Set(UpdatePhase.Checking);
        try
        {
            var releases = await UpdateCheck.FetchReleases(Http);
            _settings.LastUpdateCheck = DateTimeOffset.Now;
            var found = UpdateCheck.Newest(releases, Platform, CurrentVersion);
            if (found == null || (!userInitiated && found.Version == AppVersion.Parse(_settings.SkippedUpdateVersion)))
            {
                Update = null;
                Set(UpdatePhase.UpToDate);
                return;
            }
            Update = found;
            ManualReason = InstallBlocker(found);
            if (ManualReason != null)
            {
                Set(UpdatePhase.Available);
                return;
            }
            await Download(found);
        }
        catch (Exception error)
        {
            AppLog.Debug("update", "failed: " + error);
            Fail(ApiException.From(error).Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Why <paramref name="update"/> can't be installed in place, or null if it can.</summary>
    private string? InstallBlocker(AvailableUpdate update)
    {
        if (IsDevelopmentBuild) return L("开发版本不会自动更新", "Development builds don't update themselves");
        if (update.Sha256 == null) return L("GitHub 没有提供这个文件的校验值", "GitHub published no checksum for this download");
        var folder = InstallDirectory;
        if (!File.Exists(Path.Combine(folder, "OpenTypeless.exe"))) return L("找不到安装位置", "Can't find where OpenTypeless is installed");
        // The whole folder is replaced, so it must not hold (or be inside) the settings and history.
        if (UpdateInstaller.Overlaps(folder, AppPaths.Support))
        {
            return L($"OpenTypeless 装在了它的数据文件夹里（{folder}），请把它移到别处，比如 %LOCALAPPDATA%\\Programs",
                     $"OpenTypeless is installed in its own data folder ({folder}); move it elsewhere, such as %LOCALAPPDATA%\\Programs");
        }
        // The installer renames the install folder, so both it and its parent must be writable.
        if (!IsWritable(folder) || Path.GetDirectoryName(folder) is not { } parent || !IsWritable(parent))
        {
            return L($"没有权限替换 {folder} 中的 OpenTypeless", $"No permission to replace OpenTypeless in {folder}");
        }
        return null;
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".opentypeless-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task Download(AvailableUpdate update)
    {
        var folder = Path.Combine(UpdatesDirectory, update.Version.ToString());
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, update.AssetName);
        Progress = 0;
        Set(UpdatePhase.Downloading);

        // A zip downloaded by an earlier launch is reused if it's intact.
        var progress = new Progress<double>(value =>
        {
            if (Phase != UpdatePhase.Downloading) return;
            Progress = value;
            Changed?.Invoke();
        });
        var hash = File.Exists(zip) ? await Task.Run(() => HashFile(zip)) : null;
        if (hash != update.Sha256) hash = await Task.Run(() => Fetch(update, zip, progress));
        if (hash != update.Sha256)
        {
            File.Delete(zip);
            throw ApiException.BadResponse(L("下载的文件校验失败", "the download doesn't match its published checksum"));
        }

        var app = await Task.Run(() => Unpack(zip, Path.Combine(folder, "unpacked"), update.Version));
        _stagedDirectory = app;
        Set(UpdatePhase.Ready);
    }

    /// <summary>Streams the zip to disk and returns its SHA-256.</summary>
    private static async Task<string> Fetch(AvailableUpdate update, string destination, IProgress<double> progress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", "OpenTypeless");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            return await Fetch(request, update, destination, progress, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw ApiException.TimedOut(TimeSpan.FromMinutes(15).TotalSeconds);
        }
    }

    private static async Task<string> Fetch(HttpRequestMessage request, AvailableUpdate update, string destination,
                                            IProgress<double> progress, CancellationToken token)
    {
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw ApiException.Http((int)response.StatusCode, update.AssetName);
        var total = response.Content.Headers.ContentLength ?? update.Size;

        var partial = destination + ".partial";
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[81920];
            long received = 0;
            var lastPercent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                sha.AppendData(buffer, 0, read);
                received += read;
                var percent = total > 0 ? (int)(received * 100 / total) : 0;
                if (percent == lastPercent) continue;
                lastPercent = percent;
                progress.Report(Math.Min(1, percent / 100.0));
            }
        }
        File.Move(partial, destination, overwrite: true);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Extracts the zip and returns the folder holding the new OpenTypeless.exe, after checking its version.</summary>
    private static string Unpack(string zip, string destination, AppVersion version)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        // Rejects entries that would land outside the destination.
        ZipFile.ExtractToDirectory(zip, destination);
        var exe = Directory.EnumerateFiles(destination, "OpenTypeless.exe", SearchOption.AllDirectories)
                      .OrderBy(path => path.Length).FirstOrDefault()
                  ?? throw ApiException.BadResponse(L("下载的文件里没有 OpenTypeless.exe", "the download has no OpenTypeless.exe"));
        // The managed assembly always carries the <Version> from Directory.Build.props; the native launcher usually does too.
        var assembly = Path.Combine(Path.GetDirectoryName(exe)!, "OpenTypeless.dll");
        var fileVersion = AppVersion.Parse(FileVersionInfo.GetVersionInfo(File.Exists(assembly) ? assembly : exe).FileVersion);
        if (fileVersion != version)
        {
            throw ApiException.BadResponse(L($"下载的版本是 {fileVersion}，不是 {version}", $"the download is version {fileVersion}, not {version}"));
        }
        return Path.GetDirectoryName(exe)!;
    }

    // MARK: Install

    /// <summary>Starts the installer and quits. During a dictation, waits until it has finished.</summary>
    public void InstallAndRestart()
    {
        if (Phase != UpdatePhase.Ready || _stagedDirectory == null || Update == null) return;
        if (!CanInstallNow())
        {
            InstallPending = true;
            Changed?.Invoke();
            return;
        }
        InstallPending = false;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(_stagedDirectory, "OpenTypeless.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = _stagedDirectory,
            };
            start.ArgumentList.Add(UpdateInstaller.Argument);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(InstallDirectory);
            Process.Start(start)?.Dispose();
        }
        catch (Exception error)
        {
            Fail(L("无法启动安装：", "Couldn't start the install: ") + error.Message);
            return;
        }
        _settings.InstallingUpdateVersion = Update.Version.ToString();
        Set(UpdatePhase.Installing);
        Quit?.Invoke();
    }

    /// <summary>Called whenever a dictation finishes.</summary>
    public void SessionBecameIdle()
    {
        if (InstallPending) InstallAndRestart();
    }

    /// <summary>Stops offering this version; a manual check still shows it.</summary>
    public void SkipUpdate()
    {
        if (!CanSkip || Update == null) return;
        _settings.SkippedUpdateVersion = Update.Version.ToString();
        try
        {
            var folder = Path.Combine(UpdatesDirectory, Update.Version.ToString());
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Update = null;
        ManualReason = null;
        InstallPending = false;
        _stagedDirectory = null;
        Set(UpdatePhase.UpToDate);
    }

    /// <summary>The release page, for the notes or a manual download.</summary>
    public static void OpenInBrowser(Uri url) => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    private void Set(UpdatePhase phase)
    {
        Phase = phase;
        if (phase != UpdatePhase.Failed) Error = null;
        Changed?.Invoke();
    }

    private void Fail(string message)
    {
        Error = message;
        Set(UpdatePhase.Failed);
    }
}
