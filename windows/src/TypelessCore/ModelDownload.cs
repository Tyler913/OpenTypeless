using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TypelessCore;

/// <summary>One file of a speech model: its pinned size and SHA-256, and where to get it (tried in order).</summary>
public sealed record ModelFile(string Name, long Size, string Sha256, IReadOnlyList<Uri> Urls);

/// <summary>A speech model as downloaded for on-device recognition: a folder of pinned files.</summary>
public sealed record SpeechModelInfo(string Id, string DisplayName, IReadOnlyList<ModelFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>
    /// SenseVoice Small (FunAudioLLM, Alibaba; FunASR Model License 1.1: free to use with attribution), int8, as
    /// converted for sherpa-onnx by its authors, at a pinned commit of their Hugging Face repository. Chinese, English,
    /// Japanese, Korean and Cantonese, with punctuation. hf-mirror.com serves the same commit where huggingface.co is
    /// out of reach; the pinned SHA-256 makes either source safe.
    /// </summary>
    public static readonly SpeechModelInfo SenseVoiceSmall = new(
        "sense-voice-small-int8-2024-07-17", "SenseVoice Small",
        [
            File("model.int8.onnx", 239_233_841, "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51"),
            File("tokens.txt", 315_894, "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc"),
        ]);

    public const string Repository = "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17";
    public const string Revision = "2365baeacb507f821a0c8120fcee3d484dba7a07";

    private static ModelFile File(string name, long size, string sha256) => new(name, size, sha256,
    [
        new Uri($"https://huggingface.co/{Repository}/resolve/{Revision}/{name}"),
        new Uri($"https://hf-mirror.com/{Repository}/resolve/{Revision}/{name}"),
    ]);
}

/// <summary>Where a speech model's download stands.</summary>
public abstract record ModelState
{
    public sealed record Missing : ModelState;
    public sealed record Downloading(long Received, long Total) : ModelState
    {
        public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Received / Total, 0, 1);
        /// <summary>Whole percent, rounded down, so 100 only once every byte is in.</summary>
        public int Percent => (int)Math.Floor(Fraction * 100);
    }
    /// <summary>A file is in; its SHA-256 is being checked.</summary>
    public sealed record Verifying : ModelState;
    /// <summary>Every file is there and matched its SHA-256.</summary>
    public sealed record Ready : ModelState;
    public sealed record Failed(string Message) : ModelState;
}

/// <summary>Gets a model file's bytes; a fake one in tests.</summary>
public interface IModelFetcher
{
    /// <summary>
    /// Opens <paramref name="url"/> from byte <paramref name="offset"/>. Returns the body and the byte it starts at:
    /// <paramref name="offset"/> when the server resumed, 0 when it sent the whole file.
    /// </summary>
    Task<(Stream Body, long Start)> Open(Uri url, long offset, CancellationToken cancellationToken);
}

/// <summary>Downloads over HTTPS, resuming with a Range request.</summary>
public sealed class HttpModelFetcher : IModelFetcher
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
    })
    {
        // A stall is caught per read (see ModelStore); the whole download may take long on a slow line.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public async Task<(Stream Body, long Start)> Open(Uri url, long offset, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("OpenTypeless");
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // What's on disk is already the whole file (or more): start over and let the checksum decide.
            response.Dispose();
            return await Open(url, 0, cancellationToken).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw ApiException.Http(status, url.Host);
        }
        var start = response.StatusCode == HttpStatusCode.PartialContent ? response.Content.Headers.ContentRange?.From ?? 0 : 0;
        return (await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), start);
    }
}

/// <summary>
/// A speech model's folder: downloads it on first use (never bundled with the app), checks each file's pinned SHA-256
/// before anything uses it, and removes it to free the space.
///
/// Each file is downloaded to <c>&lt;name&gt;.partial</c> and renamed once its size and SHA-256 match. A download that
/// stops (cancelled, the line dropped, the app quit) resumes from the partial file the next time; one whose checksum
/// doesn't match is deleted and starts over. The free space is checked first. <c>verified</c> records the checked
/// files, so later launches see the model is ready from the sizes alone, without hashing 240 MB again.
///
/// Thread-safety: the methods may be called from any thread; <see cref="Changed"/> is raised on the download's thread.
/// </summary>
public sealed class ModelStore
{
    /// <summary>Kept free on top of the download, so the model doesn't fill the disk.</summary>
    public const long SpareBytes = 64L * 1024 * 1024;
    /// <summary>A read that gets nothing for this long has stalled: the download fails, and resumes when tried again.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public SpeechModelInfo Model { get; }
    public string Folder { get; }
    private readonly IModelFetcher _fetcher;
    private readonly Func<string, long?> _freeBytes;
    private readonly object _lock = new();
    private ModelState _state;
    private CancellationTokenSource? _cancel;
    private Task _running = Task.CompletedTask;

    /// <summary>Raised whenever <see cref="State"/> changes (also for each whole percent downloaded).</summary>
    public event Action? Changed;

    /// <param name="freeBytes">The free space on the folder's drive, null when it can't be told.</param>
    public ModelStore(SpeechModelInfo model, string folder, IModelFetcher fetcher, Func<string, long?>? freeBytes = null)
    {
        Model = model;
        Folder = folder;
        _fetcher = fetcher;
        _freeBytes = freeBytes ?? DriveFreeBytes;
        _state = IsComplete() ? new ModelState.Ready() : new ModelState.Missing();
    }

    public ModelState State
    {
        get { lock (_lock) return _state; }
    }

    public bool IsReady => State is ModelState.Ready;

    public bool IsDownloading => State is ModelState.Downloading or ModelState.Verifying;

    public string PathOf(string name) => Path.Combine(Folder, name);

    private string VerifiedPath => Path.Combine(Folder, "verified");

    /// <summary>What <c>verified</c> holds once every file was checked: a line per file, its name and SHA-256.</summary>
    private string Manifest => string.Join("\n", Model.Files.Select(f => $"{f.Name} {f.Sha256}")) + "\n";

    /// <summary>Every file there with its size, and checked against this model's hashes when it was downloaded.</summary>
    private bool IsComplete()
    {
        try
        {
            return File.Exists(VerifiedPath) && File.ReadAllText(VerifiedPath) == Manifest
                && Model.Files.All(f => new FileInfo(PathOf(f.Name)) is { Exists: true } info && info.Length == f.Size);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How many bytes are still to download, counting partial files already on disk.</summary>
    public long RemainingBytes()
    {
        long remaining = 0;
        foreach (var file in Model.Files)
        {
            if (new FileInfo(PathOf(file.Name)) is { Exists: true } done && done.Length == file.Size) continue;
            var partial = new FileInfo(PathOf(file.Name) + ".partial");
            remaining += file.Size - (partial.Exists && partial.Length <= file.Size ? partial.Length : 0);
        }
        return remaining;
    }

    /// <summary>
    /// Starts the download, unless it's running or done; returns when it ends (in any state). Failures don't throw:
    /// they end in <see cref="ModelState.Failed"/>, and calling this again resumes.
    /// </summary>
    public Task Download()
    {
        CancellationTokenSource cancel;
        lock (_lock)
        {
            if (_state is ModelState.Ready || _state is ModelState.Downloading or ModelState.Verifying) return _running;
            cancel = new CancellationTokenSource();
            _cancel = cancel;
            _state = new ModelState.Downloading(Model.TotalBytes - SafeRemaining(), Model.TotalBytes);
            _running = Task.Run(() => Run(cancel.Token));
        }
        Changed?.Invoke();
        return _running;
    }

    /// <summary>Stops the download; what came in so far stays for the next one to resume from.</summary>
    public void Cancel()
    {
        lock (_lock) _cancel?.Cancel();
    }

    /// <summary>
    /// Stops any download and deletes the model's folder, partial files included. The model must not be in use (see
    /// <see cref="OnDeviceTranscriber.Unload"/>).
    /// </summary>
    /// <returns>False when a file couldn't be deleted (in use or locked by another program); what's left is shown as missing.</returns>
    public async Task<bool> Remove()
    {
        Task running;
        lock (_lock)
        {
            _cancel?.Cancel();
            running = _running;
        }
        try { await running.ConfigureAwait(false); } catch { }
        var removed = true;
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            removed = false;
        }
        Set(new ModelState.Missing());
        return removed;
    }

    private long SafeRemaining()
    {
        try { return RemainingBytes(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Model.TotalBytes; }
    }

    private void Set(ModelState state)
    {
        lock (_lock)
        {
            if (_state == state) return;
            _state = state;
        }
        Changed?.Invoke();
    }

    private async Task Run(CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // The verified mark goes first: a model half replaced isn't taken for a checked one.
            if (File.Exists(VerifiedPath)) File.Delete(VerifiedPath);
            var remaining = RemainingBytes();
            if (_freeBytes(Folder) is { } free && free < remaining + SpareBytes)
            {
                throw new ModelDownloadException(L($"磁盘空间不足：需要 {Megabytes(remaining + SpareBytes)}，只剩 {Megabytes(free)}",
                                                   $"Not enough disk space: {Megabytes(remaining + SpareBytes)} needed, {Megabytes(free)} free"));
            }
            long before = 0;
            foreach (var file in Model.Files)
            {
                await Fetch(file, before, token).ConfigureAwait(false);
                before += file.Size;
            }
            File.WriteAllText(VerifiedPath, Manifest);
            Set(new ModelState.Ready());
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            Set(new ModelState.Missing());
        }
        catch (ModelDownloadException error)
        {
            Set(new ModelState.Failed(error.Message));
        }
        catch (Exception error)
        {
            Set(new ModelState.Failed(ApiException.From(error).Message));
        }
    }

    /// <summary>One file: resumed or downloaded from each source in turn until one works, then checked and put in place.</summary>
    /// <param name="before">The bytes of the files before it, for the progress.</param>
    private async Task Fetch(ModelFile file, long before, CancellationToken token)
    {
        var path = PathOf(file.Name);
        var partial = path + ".partial";
        if (new FileInfo(path) is { Exists: true } existing)
        {
            // There from an earlier download that didn't finish marking the model (or a damaged copy): checked again.
            if (existing.Length == file.Size && await Hash(path, token).ConfigureAwait(false) == file.Sha256) return;
            File.Delete(path);
        }
        Exception? failure = null;
        foreach (var url in file.Urls)
        {
            try
            {
                await Receive(file, url, partial, before, token).ConfigureAwait(false);
                failure = null;
                break;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                failure = error;
            }
        }
        if (failure != null) throw failure;

        Set(new ModelState.Verifying());
        if (await Hash(partial, token).ConfigureAwait(false) != file.Sha256)
        {
            File.Delete(partial);
            throw new ModelDownloadException(L("下载的文件已损坏（校验和不符），请重试", "The download was damaged (checksum mismatch). Try again."));
        }
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>Appends the rest of the file to <paramref name="partial"/>, or starts it over when the source sends it whole.</summary>
    private async Task Receive(ModelFile file, Uri url, string partial, long before, CancellationToken token)
    {
        var offset = new FileInfo(partial) is { Exists: true } info ? info.Length : 0;
        if (offset > file.Size)
        {
            File.Delete(partial);
            offset = 0;
        }
        if (offset == file.Size) return; // all there: the checksum decides
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(StallTimeout);
        Stream body;
        long start;
        try
        {
            (body, start) = await _fetcher.Open(url, offset, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw ApiException.Network(L("连接超时", "connection timed out"));
        }
        await using (body)
        await using (var output = new FileStream(partial, start == offset && offset > 0 ? FileMode.Append : FileMode.Create,
                                                 FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var received = start == offset ? offset : 0;
            var buffer = new byte[1 << 16];
            var reported = -1;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw ApiException.Network(L("连接超时", "connection timed out"));
                }
                if (read == 0) break;
                if (received + read > file.Size) throw ApiException.BadResponse(L("文件比预期的大", "the file is larger than expected"));
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                var progress = new ModelState.Downloading(before + received, Model.TotalBytes);
                if (progress.Percent != reported)
                {
                    reported = progress.Percent;
                    Set(progress);
                }
            }
            if (received != file.Size) throw ApiException.Network(L("下载中断", "the download was cut short"));
        }
    }

    private static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static string Megabytes(long bytes) => $"{Math.Ceiling(bytes / 1_048_576.0):0} MB";

    private static long? DriveFreeBytes(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>A model download that can't go on: not enough space, or a damaged file. The message says which.</summary>
public sealed class ModelDownloadException(string message) : Exception(message);
