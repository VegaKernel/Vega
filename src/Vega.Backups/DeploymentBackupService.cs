using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vega.Contracts.Backups;
using Vega.Contracts.Operations;

namespace Vega.Backups;

public sealed class DeploymentBackupService : IDeploymentBackupService, IDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object sync = new();
    private readonly string baseDirectory;
    private readonly string backupsDirectory;
    private readonly string stagingDirectory;
    private readonly BackupOptions options;
    private readonly IBackupConsistencyBarrier barrier;
    private readonly IDatabaseBackupSource database;
    private readonly Func<string?> worldPath;
    private readonly IOperationsLogger logger;
    private readonly SemaphoreSlim execution = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task scheduler;
    private DeploymentBackupSnapshot snapshot;
    private bool disposed;

    public DeploymentBackupService(
        string baseDirectory,
        BackupOptions options,
        IBackupConsistencyBarrier barrier,
        IDatabaseBackupSource database,
        Func<string?> worldPath,
        IOperationsLogger logger)
    {
        this.baseDirectory = Path.GetFullPath(baseDirectory);
        backupsDirectory = Path.Combine(this.baseDirectory, "backups");
        stagingDirectory = Path.Combine(this.baseDirectory, "data", "backup-staging");
        this.options = options;
        this.barrier = barrier;
        this.database = database;
        this.worldPath = worldPath;
        this.logger = logger;
        TimeSpan interval = options.ParseInterval();
        snapshot = options.Enabled
            ? new DeploymentBackupSnapshot(true, DeploymentBackupState.Idle, null, null,
                DateTimeOffset.UtcNow + interval, null, 0, null)
            : DeploymentBackupSnapshot.Disabled;
        scheduler = options.Enabled ? RunSchedulerAsync(interval, stopping.Token) : Task.CompletedTask;
    }

    public DeploymentBackupSnapshot Read()
    {
        lock (sync) return snapshot;
    }

    public async ValueTask<DeploymentBackupSnapshot> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await execution.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Read() with { Detail = "A deployment backup is already running." };
        }

        string? stage = null;
        string? stagedArchive = null;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            Set(new DeploymentBackupSnapshot(options.Enabled, DeploymentBackupState.SavingWorld,
                started, null, Read().NextScheduledAt, null, 0, "Saving the authoritative world."));
            await barrier.SaveAndFlushAsync(cancellationToken).ConfigureAwait(false);

            string sourceWorld = ResolveWorldPath();
            stage = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string worldDirectory = Path.Combine(stage, "world");
            Directory.CreateDirectory(worldDirectory);
            await CopyAsync(sourceWorld, Path.Combine(worldDirectory, Path.GetFileName(sourceWorld)), cancellationToken)
                .ConfigureAwait(false);
            string imagePath = Path.ChangeExtension(sourceWorld, ".vworld");
            if (File.Exists(imagePath))
            {
                await CopyAsync(imagePath, Path.Combine(worldDirectory, Path.GetFileName(imagePath)), cancellationToken)
                    .ConfigureAwait(false);
            }

            Set(Read() with
            {
                State = DeploymentBackupState.CapturingDatabase,
                Detail = $"Capturing {database.Provider} database."
            });
            string databaseDirectory = Path.Combine(stage, "database");
            Directory.CreateDirectory(databaseDirectory);
            await database.WriteBackupAsync(
                    Path.Combine(databaseDirectory, SafeFileName(database.BackupFileName)),
                    cancellationToken)
                .ConfigureAwait(false);

            await WriteRedactedConfigurationAsync(stage, cancellationToken).ConfigureAwait(false);
            var manifest = new BackupManifest(
                "Vega deployment backup",
                typeof(DeploymentBackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                started,
                DateTimeOffset.UtcNow,
                Path.GetFileName(sourceWorld),
                File.Exists(imagePath),
                database.Provider,
                database.SchemaVersion);
            await File.WriteAllTextAsync(
                Path.Combine(stage, "manifest.json"),
                JsonSerializer.Serialize(manifest, ManifestJson),
                cancellationToken).ConfigureAwait(false);
            await WriteChecksumsAsync(stage, cancellationToken).ConfigureAwait(false);

            Set(Read() with { State = DeploymentBackupState.Archiving, Detail = "Creating deployment archive." });
            Directory.CreateDirectory(stagingDirectory);
            stagedArchive = Path.Combine(stagingDirectory, $"archive-{Guid.NewGuid():N}.zip");
            await Task.Run(
                () => ZipFile.CreateFromDirectory(stage, stagedArchive, CompressionLevel.Optimal, false),
                cancellationToken).ConfigureAwait(false);
            using (ZipFile.OpenRead(stagedArchive)) { }
            Directory.CreateDirectory(backupsDirectory);
            string archive = Path.Combine(backupsDirectory, $"Vega-backup-{started:yyyyMMdd-HHmmssfff}.zip");
            File.Move(stagedArchive, archive);
            stagedArchive = null;
            ApplyRetention();
            var completed = new DeploymentBackupSnapshot(
                options.Enabled,
                DeploymentBackupState.Succeeded,
                started,
                DateTimeOffset.UtcNow,
                options.Enabled ? DateTimeOffset.UtcNow + options.ParseInterval() : null,
                archive,
                new FileInfo(archive).Length,
                "Backup completed and passed archive integrity validation.");
            Set(completed);
            logger.Log(OperationsLogLevel.Information, "backups",
                $"Deployment backup completed: '{archive}' ({completed.ArchiveBytes} bytes).");
            return completed;
        }
        catch (OperationCanceledException)
        {
            Set(new DeploymentBackupSnapshot(
                options.Enabled,
                DeploymentBackupState.Failed,
                started,
                DateTimeOffset.UtcNow,
                options.Enabled ? DateTimeOffset.UtcNow + options.ParseInterval() : null,
                null,
                0,
                "Backup cancelled before publication."));
            logger.Log(OperationsLogLevel.Warning, "backups", "Deployment backup was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            var failed = new DeploymentBackupSnapshot(
                options.Enabled,
                DeploymentBackupState.Failed,
                started,
                DateTimeOffset.UtcNow,
                options.Enabled ? DateTimeOffset.UtcNow + options.ParseInterval() : null,
                null,
                0,
                ex.Message);
            Set(failed);
            logger.Log(OperationsLogLevel.Error, "backups", "Deployment backup failed.", ex);
            return failed;
        }
        finally
        {
            if (stagedArchive is not null && File.Exists(stagedArchive)) File.Delete(stagedArchive);
            if (stage is not null && Directory.Exists(stage)) Directory.Delete(stage, true);
            execution.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        try { scheduler.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        execution.Wait();
        execution.Release();
        stopping.Dispose();
        execution.Dispose();
    }

    private async Task RunSchedulerAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await CreateAsync(cancellationToken).ConfigureAwait(false);
            lock (sync) snapshot = snapshot with { NextScheduledAt = DateTimeOffset.UtcNow + interval };
        }
    }

    private string ResolveWorldPath()
    {
        string? value = worldPath();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The active world path is unavailable.");
        }

        string resolved = Path.GetFullPath(value);
        if (!File.Exists(resolved) || !string.Equals(Path.GetExtension(resolved), ".wld", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The active .wld file does not exist.", resolved);
        }

        return resolved;
    }

    private async Task WriteRedactedConfigurationAsync(string stage, CancellationToken cancellationToken)
    {
        string source = Path.Combine(baseDirectory, "config");
        if (!Directory.Exists(source)) return;
        string target = Path.Combine(stage, "config");
        foreach (string path in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, path);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            JsonNode? json = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            Redact(json);
            await File.WriteAllTextAsync(destination, json?.ToJsonString(ManifestJson) ?? "null", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            foreach (string key in value.Select(static pair => pair.Key).ToArray())
            {
                if (IsSecret(key)) value[key] = "<redacted>";
                else Redact(value[key]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array) Redact(item);
        }
    }

    private static bool IsSecret(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("connectionString", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteChecksumsAsync(string stage, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (string path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
                     .Where(static path => !path.EndsWith("checksums.sha256", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => Path.GetRelativePath(stage, path), StringComparer.Ordinal))
        {
            await using FileStream stream = File.OpenRead(path);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            lines.Add($"{Convert.ToHexString(hash).ToLowerInvariant()} *{Path.GetRelativePath(stage, path).Replace('\\', '/')}");
        }

        await File.WriteAllLinesAsync(Path.Combine(stage, "checksums.sha256"), lines, cancellationToken)
            .ConfigureAwait(false);
    }

    private void ApplyRetention()
    {
        string root = Path.GetFullPath(backupsDirectory);
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        foreach (FileInfo obsolete in new DirectoryInfo(root).EnumerateFiles("Vega-backup-*.zip")
                     .OrderByDescending(static file => file.Name, StringComparer.Ordinal)
                     .Skip(options.RetainedBackups))
        {
            if (!obsolete.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Backup retention target escaped the backups directory.");
            }

            obsolete.Delete();
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SafeFileName(string value)
    {
        string name = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(name) ? "database.dump" : name;
    }

    private void Set(DeploymentBackupSnapshot value)
    {
        lock (sync) snapshot = value;
    }

    private sealed record BackupManifest(
        string Format,
        string VegaVersion,
        DateTimeOffset StartedAt,
        DateTimeOffset CapturedAt,
        string WorldFile,
        bool IncludesWorldImage,
        string DatabaseProvider,
        int SchemaVersion);
}
