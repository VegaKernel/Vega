using System.IO.Compression;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Vega.Backups;
using Vega.Contracts.Backups;
using Vega.Contracts.Operations;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class DeploymentBackupTests
{
    [Test]
    public async Task BackupPublishesConsistentRedactedArchiveAndAppliesRetention()
    {
        string root = CreateRoot();
        try
        {
            string world = Path.Combine(root, "worlds", "primary.wld");
            Directory.CreateDirectory(Path.GetDirectoryName(world)!);
            await File.WriteAllTextAsync(world, "world-one");
            await File.WriteAllTextAsync(Path.ChangeExtension(world, ".vworld"), "cache-one");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            await File.WriteAllTextAsync(Path.Combine(root, "config", "server.json"),
                "{\"port\":7777,\"password\":\"secret\"}");
            await File.WriteAllTextAsync(Path.Combine(root, "config", "database.json"),
                "{\"connectionString\":\"Server=secret\"}");
            var barrier = new FakeBarrier();
            var database = new FakeDatabase();
            using var backups = new DeploymentBackupService(
                root,
                new BackupOptions(false, "06:00:00", 2),
                barrier,
                database,
                () => world,
                new RecordingLogger());

            for (int index = 0; index < 3; index++)
            {
                DeploymentBackupSnapshot result = await backups.CreateAsync();
                Assert.That(result.State, Is.EqualTo(DeploymentBackupState.Succeeded), result.Detail);
                await Task.Delay(5);
            }

            FileInfo[] archives = new DirectoryInfo(Path.Combine(root, "backups"))
                .EnumerateFiles("*.zip")
                .OrderBy(static file => file.Name)
                .ToArray();
            Assert.That(archives, Has.Length.EqualTo(2));
            using ZipArchive archive = ZipFile.OpenRead(archives[^1].FullName);
            Assert.Multiple(() =>
            {
                Assert.That(archive.GetEntry("world/primary.wld"), Is.Not.Null);
                Assert.That(archive.GetEntry("world/primary.vworld"), Is.Not.Null);
                Assert.That(archive.GetEntry("database/vega.test"), Is.Not.Null);
                Assert.That(archive.GetEntry("manifest.json"), Is.Not.Null);
                Assert.That(archive.GetEntry("checksums.sha256"), Is.Not.Null);
                Assert.That(barrier.SaveCount, Is.EqualTo(3));
                Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(root, "data", "backup-staging")), Is.Empty);
            });

            string serverConfig = await ReadEntryAsync(archive, "config/server.json");
            string databaseConfig = await ReadEntryAsync(archive, "config/database.json");
            Assert.Multiple(() =>
            {
                Assert.That(JsonNode.Parse(serverConfig)?["password"]?.GetValue<string>(), Is.EqualTo("<redacted>"));
                Assert.That(JsonNode.Parse(databaseConfig)?["connectionString"]?.GetValue<string>(), Is.EqualTo("<redacted>"));
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ConcurrentRunIsRejectedAndFailureNeverPublishesPartialArchive()
    {
        string root = CreateRoot();
        try
        {
            string world = Path.Combine(root, "primary.wld");
            await File.WriteAllTextAsync(world, "world");
            var barrier = new BlockingBarrier();
            using var backups = new DeploymentBackupService(
                root,
                new BackupOptions(false, "06:00:00", 2),
                barrier,
                new FakeDatabase(),
                () => world,
                new RecordingLogger());

            Task<DeploymentBackupSnapshot> first = backups.CreateAsync().AsTask();
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            DeploymentBackupSnapshot overlapping = await backups.CreateAsync();
            barrier.Release.TrySetResult();
            DeploymentBackupSnapshot completed = await first;

            Assert.Multiple(() =>
            {
                Assert.That(overlapping.Detail, Does.Contain("already running"));
                Assert.That(completed.State, Is.EqualTo(DeploymentBackupState.Succeeded));
            });

            using var failing = new DeploymentBackupService(
                root,
                new BackupOptions(false, "06:00:00", 2),
                new FakeBarrier(),
                new FakeDatabase(fail: true),
                () => world,
                new RecordingLogger());
            DeploymentBackupSnapshot failed = await failing.CreateAsync();
            Assert.Multiple(() =>
            {
                Assert.That(failed.State, Is.EqualTo(DeploymentBackupState.Failed));
                Assert.That(new DirectoryInfo(Path.Combine(root, "backups")).EnumerateFiles("*.zip").Count(), Is.EqualTo(1));
                Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(root, "data", "backup-staging")), Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void ConfigurationHasExactlyThreeBoundedOperatorSettings()
    {
        string path = WriteOptions("{\"enabled\":true,\"interval\":\"06:00:00\",\"retainedBackups\":0}");
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    typeof(BackupOptions).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
                    Has.Length.EqualTo(3));
                Assert.That(() => new BackupOptions(true, "00:01:00", 7).ParseInterval(), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => BackupOptionsStore.Load(path), Throws.TypeOf<InvalidDataException>());
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task WorldSaveFailureAndCancellationCleanStagingWithoutPublication()
    {
        string root = CreateRoot();
        try
        {
            string world = Path.Combine(root, "primary.wld");
            await File.WriteAllTextAsync(world, "world");
            using (var failing = new DeploymentBackupService(
                       root,
                       new BackupOptions(false, "06:00:00", 2),
                       new FailingBarrier(),
                       new FakeDatabase(),
                       () => world,
                       new RecordingLogger()))
            {
                Assert.That((await failing.CreateAsync()).State, Is.EqualTo(DeploymentBackupState.Failed));
            }

            var blocking = new BlockingBarrier();
            using var cancelled = new DeploymentBackupService(
                root,
                new BackupOptions(false, "06:00:00", 2),
                blocking,
                new FakeDatabase(),
                () => world,
                new RecordingLogger());
            using var cancellation = new CancellationTokenSource();
            Task<DeploymentBackupSnapshot> run = cancelled.CreateAsync(cancellation.Token).AsTask();
            await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.That(async () => await run, Throws.InstanceOf<OperationCanceledException>());
            Assert.Multiple(() =>
            {
                Assert.That(cancelled.Read().State, Is.EqualTo(DeploymentBackupState.Failed));
                Assert.That(Directory.Exists(Path.Combine(root, "backups")), Is.False);
                Assert.That(Directory.Exists(Path.Combine(root, "data", "backup-staging")), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string WriteOptions(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vega-backup-options-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        await using Stream stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeBarrier : IBackupConsistencyBarrier
    {
        public int SaveCount { get; private set; }
        public ValueTask SaveAndFlushAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingBarrier : IBackupConsistencyBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SaveAndFlushAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailingBarrier : IBackupConsistencyBarrier
    {
        public ValueTask SaveAndFlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("world save failed"));
    }

    private sealed class FakeDatabase(bool fail = false) : IDatabaseBackupSource
    {
        public string Provider => "test";
        public int SchemaVersion => 1;
        public string BackupFileName => "vega.test";
        public async ValueTask WriteBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
        {
            if (fail) throw new IOException("dump failed");
            await File.WriteAllTextAsync(destinationPath, "database", cancellationToken);
        }
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
}
