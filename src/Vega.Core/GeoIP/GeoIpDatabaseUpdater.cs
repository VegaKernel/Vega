using MaxMind.Db;
using MaxMind.GeoIP2;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vega.Core.GeoIP;

public sealed class GeoIpDatabaseUpdater : IDisposable
{
    private const string TreeUrl =
        "https://api.github.com/repos/P3TERX/GeoLite.mmdb/git/trees/download";
    private const string RawBaseUrl =
        "https://raw.githubusercontent.com/P3TERX/GeoLite.mmdb/download/";

    private static readonly DatabaseSpec[] Databases =
    [
        new("GeoLite2-ASN.mmdb", "-ASN"),
        new("GeoLite2-City.mmdb", "-City"),
        new("GeoLite2-Country.mmdb", "-Country")
    ];

    private readonly string dataDirectory;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly TimeSpan updateInterval;
    private readonly TimeSpan initialDelay;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private readonly object stateGate = new();
    private readonly Task worker;
    private CancellationTokenSource? activeCheck;
    private int autoUpdateEnabled;
    private int disposed;

    public GeoIpDatabaseUpdater(
        string dataDirectory,
        bool enabled,
        HttpClient? httpClient = null,
        TimeSpan? updateInterval = null,
        TimeSpan? initialDelay = null,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.httpClient = httpClient ?? new HttpClient();
        ownsHttpClient = httpClient is null;
        this.updateInterval = updateInterval ?? TimeSpan.FromHours(24);
        this.initialDelay = initialDelay ?? TimeSpan.FromSeconds(30);
        this.log = log;

        if (this.updateInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(updateInterval));
        }

        if (this.initialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        autoUpdateEnabled = enabled ? 1 : 0;
        worker = Task.Run(RunWorkerAsync);
    }

    public bool AutoUpdateEnabled => Volatile.Read(ref autoUpdateEnabled) != 0;

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposed();

        int requested = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref autoUpdateEnabled, requested) == requested)
        {
            return;
        }

        if (!enabled)
        {
            CancelActiveCheck();
        }

        WakeWorker();
    }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!await checkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CancellationTokenSource? checkCancellation = null;
        try
        {
            checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.Token,
                cancellationToken);
            lock (stateGate)
            {
                activeCheck = checkCancellation;
            }

            IReadOnlyDictionary<string, RemoteDatabase> remote =
                await FetchRemoteMetadataAsync(checkCancellation.Token).ConfigureAwait(false);

            Directory.CreateDirectory(dataDirectory);
            foreach (DatabaseSpec database in Databases)
            {
                checkCancellation.Token.ThrowIfCancellationRequested();
                if (!remote.TryGetValue(database.FileName, out RemoteDatabase? metadata))
                {
                    throw new InvalidDataException(
                        $"GeoIP metadata does not contain {database.FileName}.");
                }

                try
                {
                    await UpdateDatabaseAsync(database, metadata, checkCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"GeoIP update for {database.FileName} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            lock (stateGate)
            {
                if (ReferenceEquals(activeCheck, checkCancellation))
                {
                    activeCheck = null;
                }
            }

            checkCancellation?.Dispose();
            checkGate.Release();
        }
    }

    public static async Task<string> ComputeGitBlobShaAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The file to hash does not exist.", path);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] header = Encoding.ASCII.GetBytes($"blob {info.Length}\0");
        hash.AppendData(header);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            if (AutoUpdateEnabled && initialDelay > TimeSpan.Zero)
            {
                await WaitForSignalOrDelayAsync(initialDelay, lifetime.Token).ConfigureAwait(false);
            }

            while (!lifetime.IsCancellationRequested)
            {
                if (AutoUpdateEnabled)
                {
                    try
                    {
                        await CheckForUpdatesAsync(lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        lifetime.IsCancellationRequested || !AutoUpdateEnabled)
                    {
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"GeoIP automatic update failed: {ex.Message}");
                    }
                }

                await WaitForSignalOrDelayAsync(updateInterval, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task<IReadOnlyDictionary<string, RemoteDatabase>> FetchRemoteMetadataAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TreeUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Vega", "1.0"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        JsonElement root = document.RootElement;
        if (root.TryGetProperty("truncated", out JsonElement truncated) && truncated.GetBoolean())
        {
            throw new InvalidDataException("GitHub returned a truncated GeoIP tree.");
        }

        if (!root.TryGetProperty("tree", out JsonElement tree) || tree.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub GeoIP tree response is invalid.");
        }

        var result = new Dictionary<string, RemoteDatabase>(StringComparer.Ordinal);
        foreach (JsonElement entry in tree.EnumerateArray())
        {
            if (!entry.TryGetProperty("path", out JsonElement pathElement) ||
                !entry.TryGetProperty("type", out JsonElement typeElement) ||
                !entry.TryGetProperty("sha", out JsonElement shaElement) ||
                !entry.TryGetProperty("size", out JsonElement sizeElement))
            {
                continue;
            }

            string? path = pathElement.GetString();
            if (path is null || !Databases.Any(database => database.FileName == path))
            {
                continue;
            }

            string? type = typeElement.GetString();
            string? sha = shaElement.GetString();
            long size = sizeElement.GetInt64();
            if (!string.Equals(type, "blob", StringComparison.Ordinal) ||
                sha is null ||
                sha.Length != 40 ||
                !sha.All(Uri.IsHexDigit) ||
                size <= 0)
            {
                throw new InvalidDataException($"GitHub metadata for {path} is invalid.");
            }

            result[path] = new RemoteDatabase(sha.ToLowerInvariant(), size);
        }

        foreach (DatabaseSpec database in Databases)
        {
            if (!result.ContainsKey(database.FileName))
            {
                throw new InvalidDataException(
                    $"GitHub GeoIP tree does not contain {database.FileName}.");
            }
        }

        return result;
    }

    private async Task UpdateDatabaseAsync(
        DatabaseSpec database,
        RemoteDatabase remote,
        CancellationToken cancellationToken)
    {
        string targetPath = Path.Combine(dataDirectory, database.FileName);
        if (File.Exists(targetPath))
        {
            string localSha = await ComputeGitBlobShaAsync(targetPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(localSha, remote.Sha, StringComparison.Ordinal))
            {
                return;
            }
        }

        string temporaryPath = Path.Combine(
            dataDirectory,
            $".{database.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                RawBaseUrl + Uri.EscapeDataString(database.FileName));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Vega", "1.0"));

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength != remote.Size)
            {
                throw new InvalidDataException(
                    $"Downloaded {database.FileName} has an unexpected content length.");
            }

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(temporaryPath).Length != remote.Size)
            {
                throw new InvalidDataException(
                    $"Downloaded {database.FileName} has an unexpected size.");
            }

            string downloadedSha = await ComputeGitBlobShaAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(downloadedSha, remote.Sha, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Downloaded {database.FileName} does not match the GitHub blob hash.");
            }

            ValidateDatabase(temporaryPath, database.ExpectedTypeSuffix);
            File.Move(temporaryPath, targetPath, overwrite: true);
            log?.Invoke($"GeoIP database updated: {database.FileName}.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateDatabase(string path, string expectedTypeSuffix)
    {
        using var reader = new DatabaseReader(path, FileAccessMode.Memory);
        string databaseType = reader.Metadata.DatabaseType;
        if (!databaseType.EndsWith(expectedTypeSuffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"MMDB type '{databaseType}' does not match expected '{expectedTypeSuffix}'.");
        }
    }

    private async Task WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delayTask = Task.Delay(delay, waitCancellation.Token);
        Task signalTask = wakeSignal.WaitAsync(waitCancellation.Token);
        Task completed = await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
        waitCancellation.Cancel();

        try
        {
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void WakeWorker()
    {
        try
        {
            wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void CancelActiveCheck()
    {
        lock (stateGate)
        {
            try
            {
                activeCheck?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        CancelActiveCheck();
        WakeWorker();
        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }

        checkGate.Dispose();
        wakeSignal.Dispose();
        lifetime.Dispose();
    }

    private sealed record DatabaseSpec(string FileName, string ExpectedTypeSuffix);
    private sealed record RemoteDatabase(string Sha, long Size);
}
