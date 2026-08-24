using NUnit.Framework;
using Vega.Contracts.Operations;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Sandboxes;
using Vega.Terraria.Networking;
using Multiplicity.Packets;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SandboxControlTests
{
    [Test]
    public async Task CreateResetAndDestroyPreserveImmutableSourceAndPersistentCatalog()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "template.wld");
        string replacement = Path.Combine(root, "replacement.wld");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllBytesAsync(replacement, [4, 5, 6]);
        try
        {
            SandboxOptions options = SandboxOptions.Default with { MaximumActiveWorlds = 2 };
            using (var control = new SandboxControlService(root, options, new RecordingLogger()))
            {
                SandboxOperationResult created = await control.CreateAsync(new SandboxCreateRequest(
                    "arena",
                    "Arena",
                    source,
                    SandboxPersistenceMode.Persistent,
                    8,
                    new SandboxAccessPolicy(SandboxAccessMode.Public),
                    null,
                    SandboxSscMode.Isolated,
                    new HashSet<string>()));
                Assert.That(created.IsSuccess, Is.True, created.Message);
                Assert.That(await File.ReadAllBytesAsync(created.Sandbox!.Descriptor.InstanceWorldPath), Is.EqualTo(new byte[] { 1, 2, 3 }));
                SandboxOperationResult retried = await control.CreateAsync(new SandboxCreateRequest(
                    "arena",
                    "Arena",
                    source,
                    SandboxPersistenceMode.Persistent,
                    8,
                    new SandboxAccessPolicy(SandboxAccessMode.Public),
                    null,
                    SandboxSscMode.Isolated,
                    new HashSet<string>()));
                Assert.That(retried.Status, Is.EqualTo(SandboxOperationStatus.AlreadyInDesiredState));

                SandboxOperationResult reset = await control.ResetAsync(new WorldId("arena"), replacement);
                Assert.That(reset.IsSuccess, Is.True, reset.Message);
                Assert.That(await File.ReadAllBytesAsync(reset.Sandbox!.Descriptor.InstanceWorldPath), Is.EqualTo(new byte[] { 4, 5, 6 }));
                Assert.That(await File.ReadAllBytesAsync(source), Is.EqualTo(new byte[] { 1, 2, 3 }));
            }

            using (var reopened = new SandboxControlService(root, options, new RecordingLogger()))
            {
                Assert.That(reopened.Snapshot().Single().Descriptor.WorldId, Is.EqualTo(new WorldId("arena")));
                SandboxOperationResult destroyed = await reopened.DestroyAsync(new WorldId("arena"));
                Assert.That(destroyed.IsSuccess, Is.True, destroyed.Message);
                Assert.That(File.Exists(source), Is.True);
                Assert.That(File.Exists(replacement), Is.True);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void BackendLaunchContextIsAuthenticatedAndRemovedFromTerrariaArguments()
    {
        byte[] key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        SandboxBackendLaunchOptions launch = SandboxBackendLaunchOptions.Parse(
            [
                "--vega-backend-world-id=arena",
                $"--vega-backend-instance={Guid.NewGuid():D}",
                "--vega-backend-status=status.json",
                "--vega-backend-modules=WorldEdit,RegionVision",
                "--vega-backend-ssc-mode=Isolated",
                $"--vega-backend-ssc-store={Path.Combine(Path.GetTempPath(), "ssc.json")}",
                "-world",
                "arena.wld"
            ],
            name => name == "VEGA_BACKEND_TOKEN" ? Convert.ToBase64String(key) : null);

        Assert.Multiple(() =>
        {
            Assert.That(launch.IsBackend, Is.True);
            Assert.That(launch.WorldId, Is.EqualTo(new WorldId("arena")));
            Assert.That(launch.EnabledModules, Is.EquivalentTo(new[] { "WorldEdit", "RegionVision" }));
            Assert.That(launch.SscMode, Is.EqualTo(SandboxSscMode.Isolated));
            Assert.That(launch.SscStorePath, Is.Not.Empty);
            Assert.That(launch.ServerArguments, Is.EqualTo(new[] { "-world", "arena.wld" }));
        });
    }

    [Test]
    public void TransferTicketIsTargetBoundAndSingleUse()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ticket-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var tickets = new SandboxTransferTicketService(Path.Combine(root, "routing.key"));
            SandboxTransferTicket ticket = tickets.Issue("account", WorldId.Primary, new WorldId("arena"));

            Assert.Multiple(() =>
            {
                Assert.That(tickets.ValidateAndConsume(ticket, new WorldId("other"), DateTimeOffset.UtcNow),
                    Is.EqualTo(SandboxTransferValidationStatus.WrongTarget));
                Assert.That(tickets.ValidateAndConsume(ticket, new WorldId("arena"), DateTimeOffset.UtcNow),
                    Is.EqualTo(SandboxTransferValidationStatus.Accepted));
                Assert.That(tickets.ValidateAndConsume(ticket, new WorldId("arena"), DateTimeOffset.UtcNow),
                    Is.EqualTo(SandboxTransferValidationStatus.Replayed));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }


    [Test]
    public void SscStorePersistsCopyOnEnterAndRejectsStaleWrites()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ssc-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "ssc.json");
        try
        {
            var store = new SandboxSscStore(path);
            SandboxSscWriteResult primary = store.Save(
                "account", WorldId.Primary, SandboxSscMode.Inherit, "primary-v1", 0, DateTimeOffset.UtcNow);
            Assert.That(primary.IsSuccess, Is.True);

            SandboxSscTransferPreparation enter = store.PrepareTransfer(
                "account", WorldId.Primary, new WorldId("arena"),
                SandboxSscMode.Inherit, SandboxSscMode.CopyOnEnter,
                DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
            Assert.That(enter.Accepted, Is.True);
            Assert.That(enter.Lease, Is.Not.Null);
            Assert.That(store.StageTransferState(
                enter.Lease!.LeaseId, "account", "primary-v1", DateTimeOffset.UtcNow).Accepted, Is.True);
            Assert.That(store.CommitTransfer(
                enter.Lease.LeaseId, "account", new WorldId("arena"), DateTimeOffset.UtcNow).Accepted, Is.True);

            SandboxSscStateSnapshot copied = store.Read(
                "account", new WorldId("arena"), SandboxSscMode.CopyOnEnter)!;
            Assert.That(copied.Payload, Is.EqualTo("primary-v1"));

            SandboxSscWriteResult sandbox = store.Save(
                "account", new WorldId("arena"), SandboxSscMode.CopyOnEnter,
                "arena-v2", copied.Revision, DateTimeOffset.UtcNow);
            Assert.That(sandbox.IsSuccess, Is.True);
            Assert.That(store.Save(
                "account", new WorldId("arena"), SandboxSscMode.CopyOnEnter,
                "stale", copied.Revision, DateTimeOffset.UtcNow).Status,
                Is.EqualTo(SandboxSscWriteStatus.Conflict));

            SandboxSscTransferPreparation leave = store.PrepareTransfer(
                "account", new WorldId("arena"), WorldId.Primary,
                SandboxSscMode.CopyOnEnter, SandboxSscMode.Inherit,
                DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
            Assert.That(store.StageTransferState(
                leave.Lease!.LeaseId, "account", "arena-v2", DateTimeOffset.UtcNow).Accepted, Is.True);
            Assert.That(store.CommitTransfer(
                leave.Lease.LeaseId, "account", WorldId.Primary, DateTimeOffset.UtcNow).Accepted, Is.True);

            var reopened = new SandboxSscStore(path);
            SandboxSscStateSnapshot restored = reopened.Read(
                "account", WorldId.Primary, SandboxSscMode.Inherit)!;
            Assert.That(restored.Payload, Is.EqualTo("primary-v1"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void TransferAdmissionBindsTrustedIdentityAndIsSingleUse()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-admission-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var tickets = new SandboxTransferTicketService(Path.Combine(root, "control.key"));
            var admissions = new SandboxTransferAdmissionService(Path.Combine(root, "admissions.json"), tickets);
            var target = new WorldId("arena");
            SandboxTransferTicket ticket = tickets.Issue("account", WorldId.Primary, target);
            const string uuid = "755a3c87-6f2b-4ec9-aed4-b8eeaf855c81";

            Assert.That(admissions.Register(ticket, "203.0.113.9", uuid, DateTimeOffset.UtcNow).Accepted, Is.True);
            Assert.That(admissions.Consume(target, "203.0.113.10", uuid, DateTimeOffset.UtcNow).Accepted, Is.False);
            SandboxTransferAdmissionResult accepted = admissions.Consume(
                target, "203.0.113.9", uuid, DateTimeOffset.UtcNow);
            Assert.Multiple(() =>
            {
                Assert.That(accepted.Accepted, Is.True);
                Assert.That(accepted.Ticket?.AccountId, Is.EqualTo("account"));
            });
            SandboxTransferAdmissionResult replay = admissions.Consume(
                target, "203.0.113.9", uuid, DateTimeOffset.UtcNow);
            Assert.Multiple(() =>
            {
                Assert.That(replay.Accepted, Is.False);
                Assert.That(replay.ErrorCode, Is.EqualTo("sandbox.transfer.admission_required"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void SscStoreCoordinatesConcurrentProcessesAndPreservesIsolatedTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ssc-concurrency", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "ssc.json");
        try
        {
            var first = new SandboxSscStore(path);
            var second = new SandboxSscStore(path);
            SandboxSscWriteResult seeded = first.Save(
                "account", new WorldId("arena"), SandboxSscMode.Isolated, "isolated-v1", 0, DateTimeOffset.UtcNow);
            Assert.That(seeded.IsSuccess, Is.True);

            SandboxSscWriteResult stale = second.Save(
                "account", new WorldId("arena"), SandboxSscMode.Isolated, "stale", 0, DateTimeOffset.UtcNow);
            Assert.That(stale.Status, Is.EqualTo(SandboxSscWriteStatus.Conflict));

            SandboxSscTransferPreparation transfer = first.PrepareTransfer(
                "account", WorldId.Primary, new WorldId("arena"),
                SandboxSscMode.Disabled, SandboxSscMode.Isolated,
                DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
            Assert.That(transfer.Accepted, Is.True);
            Assert.That(first.StageTransferState(
                transfer.Lease!.LeaseId, "account", "client-current", DateTimeOffset.UtcNow).Accepted, Is.True);
            Assert.That(second.CommitTransfer(
                transfer.Lease.LeaseId, "account", new WorldId("arena"), DateTimeOffset.UtcNow).Accepted, Is.True);

            SandboxSscStateSnapshot preserved = first.Read(
                "account", new WorldId("arena"), SandboxSscMode.Isolated)!;
            Assert.That(preserved.Payload, Is.EqualTo("isolated-v1"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void ResetDeletesOnlyWorldScopedSscState()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ssc-reset", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "ssc.json");
        try
        {
            var store = new SandboxSscStore(path);
            Assert.That(store.Save(
                "account", WorldId.Primary, SandboxSscMode.Inherit, "shared", 0, DateTimeOffset.UtcNow).IsSuccess, Is.True);
            Assert.That(store.Save(
                "account", new WorldId("arena"), SandboxSscMode.Isolated, "isolated", 0, DateTimeOffset.UtcNow).IsSuccess, Is.True);

            Assert.That(store.DeleteWorldState(new WorldId("arena")), Is.EqualTo(1));
            Assert.That(store.Read("account", new WorldId("arena"), SandboxSscMode.Isolated), Is.Null);
            Assert.That(store.Read("account", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("shared"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task SupervisorCompletesSignedReadinessAndGracefulShutdown()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-supervisor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "template.wld");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            SandboxOptions options = SandboxOptions.Default with
            {
                StartTimeoutSeconds = 10,
                GracefulStopTimeoutSeconds = 5
            };
            using var control = new SandboxControlService(
                root,
                options,
                new RecordingLogger(),
                FindTestBackend());
            Assert.That((await control.CreateAsync(new SandboxCreateRequest(
                "ready",
                "Ready",
                source,
                SandboxPersistenceMode.Temporary,
                8,
                new SandboxAccessPolicy(SandboxAccessMode.Public),
                TimeSpan.FromMinutes(30),
                SandboxSscMode.Isolated,
                new HashSet<string>()))).IsSuccess, Is.True);

            SandboxOperationResult started = await control.StartAsync(new WorldId("ready"));
            Assert.Multiple(() =>
            {
                Assert.That(started.IsSuccess, Is.True, started.Message);
                Assert.That(started.Sandbox?.State, Is.EqualTo(SandboxLifecycleState.Ready));
                Assert.That(started.Sandbox?.ProcessId, Is.Not.Null);
            });

            SandboxOperationResult stopped = await control.StopAsync(new WorldId("ready"));
            Assert.That(stopped.Sandbox?.State, Is.EqualTo(SandboxLifecycleState.Stopped));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task LowDiskRejectsMaterializationWithoutDamagingSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-low-disk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "template.wld");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            using var control = new SandboxControlService(
                root,
                SandboxOptions.Default,
                new RecordingLogger(),
                FindTestBackend(),
                availableFreeSpace: _ => 0);
            SandboxOperationResult created = await control.CreateAsync(new SandboxCreateRequest(
                "disk",
                "Disk",
                source,
                SandboxPersistenceMode.Temporary,
                8,
                new SandboxAccessPolicy(SandboxAccessMode.Public),
                TimeSpan.FromMinutes(30),
                SandboxSscMode.Disabled,
                new HashSet<string>()));

            byte[] preserved = await File.ReadAllBytesAsync(source);
            Assert.Multiple(() =>
            {
                Assert.That(created.IsSuccess, Is.False);
                Assert.That(created.ErrorCode, Is.EqualTo("sandbox.create_failed"));
                Assert.That(preserved, Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task SimultaneousStartsRespectGlobalActiveBudget()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-simultaneous", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "template.wld");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            SandboxOptions options = SandboxOptions.Default with
            {
                MaximumActiveWorlds = 1,
                StartTimeoutSeconds = 10,
                GracefulStopTimeoutSeconds = 5
            };
            using var control = new SandboxControlService(root, options, new RecordingLogger(), FindTestBackend());
            foreach (string id in new[] { "one", "two" })
            {
                Assert.That((await control.CreateAsync(new SandboxCreateRequest(
                    id, id, source, SandboxPersistenceMode.Temporary, 8,
                    new SandboxAccessPolicy(SandboxAccessMode.Public), TimeSpan.FromMinutes(30),
                    SandboxSscMode.Disabled, new HashSet<string>()))).IsSuccess, Is.True);
            }

            SandboxOperationResult[] results = await Task.WhenAll(
                control.StartAsync(new WorldId("one")).AsTask(),
                control.StartAsync(new WorldId("two")).AsTask());

            Assert.Multiple(() =>
            {
                Assert.That(results.Count(static result => result.IsSuccess), Is.EqualTo(1));
                Assert.That(results.Count(static result => result.ErrorCode == "sandbox.admission_budget"), Is.EqualTo(1));
                Assert.That(control.Snapshot().Count(static status => status.State == SandboxLifecycleState.Ready), Is.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ControlPlaneRestartReconcilesSignedRunningBackend()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-reconcile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "template.wld");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        SandboxControlService? first = null;
        SandboxControlService? second = null;
        try
        {
            SandboxOptions options = SandboxOptions.Default with
            {
                MaximumAutomaticRestarts = 0,
                StartTimeoutSeconds = 10,
                GracefulStopTimeoutSeconds = 5
            };
            first = new SandboxControlService(root, options, new RecordingLogger(), FindTestBackend());
            Assert.That((await first.CreateAsync(new SandboxCreateRequest(
                "reconcile", "Reconcile", source, SandboxPersistenceMode.Temporary, 8,
                new SandboxAccessPolicy(SandboxAccessMode.Public), TimeSpan.FromMinutes(30),
                SandboxSscMode.Disabled, new HashSet<string>()))).IsSuccess, Is.True);
            SandboxOperationResult started = await first.StartAsync(new WorldId("reconcile"));
            Assert.That(started.IsSuccess, Is.True, started.Message);
            int processId = started.Sandbox!.ProcessId!.Value;

            second = new SandboxControlService(root, options, new RecordingLogger(), FindTestBackend());
            SandboxStatus reconciled = second.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(reconciled.State, Is.EqualTo(SandboxLifecycleState.Ready));
                Assert.That(reconciled.ProcessId, Is.EqualTo(processId));
            });

            Assert.That((await second.StopAsync(new WorldId("reconcile"))).IsSuccess, Is.True);
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CrashLoopStopsAtRestartBudget()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-crash-loop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "template.wld");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            SandboxOptions options = SandboxOptions.Default with
            {
                MaximumAutomaticRestarts = 1,
                RestartBackoffSeconds = 1,
                StartTimeoutSeconds = 5
            };
            using var control = new SandboxControlService(root, options, new RecordingLogger(), FindTestBackend());
            Assert.That((await control.CreateAsync(new SandboxCreateRequest(
                "crash-loop", "Crash", source, SandboxPersistenceMode.Temporary, 8,
                new SandboxAccessPolicy(SandboxAccessMode.Public), TimeSpan.FromMinutes(30),
                SandboxSscMode.Disabled, new HashSet<string>()))).IsSuccess, Is.True);

            SandboxOperationResult firstStart = await control.StartAsync(new WorldId("crash-loop"));
            Assert.That(firstStart.IsSuccess, Is.True, firstStart.Message);
            await Task.Delay(TimeSpan.FromSeconds(6));
            SandboxStatus status = control.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(status.RestartCount, Is.EqualTo(1));
                Assert.That(status.State, Is.EqualTo(SandboxLifecycleState.Failed));
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void DimensionsManualTransferPacketMatchesProtocolTypeThree()
    {
        byte[] packet = DimensionsTransferPacket.Create(new SandboxEndpoint("127.0.0.1", 7780));
        using var reader = new BinaryReader(new MemoryStream(packet));

        Assert.Multiple(() =>
        {
            Assert.That(reader.ReadInt16(), Is.EqualTo(packet.Length));
            Assert.That(reader.ReadByte(), Is.EqualTo((byte)PacketTypes.Placeholder));
            Assert.That(reader.ReadInt16(), Is.EqualTo(3));
            Assert.That(reader.ReadString(), Is.EqualTo("127.0.0.1"));
            Assert.That(reader.ReadUInt16(), Is.EqualTo(7780));
        });
    }

    [Test]
    public void RoutingRejectsCapacityAndBackendLossWithOriginFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-routing-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var control = new FakeControl(Status(online: 8, capacity: 8));
            var tickets = new SandboxTransferTicketService(Path.Combine(root, "routing.key"));
            var routing = new SandboxRoutingService(control, tickets, new SandboxEndpoint("127.0.0.1", 7777));
            SandboxTransferResult full = routing.PrepareEnter(
                "account",
                new HashSet<string>(),
                WorldId.Primary,
                new WorldId("arena"));

            control.Statuses = [Status(online: 0, capacity: 8)];
            SandboxTransferResult prepared = routing.PrepareTransfer(
                "account",
                new HashSet<string>(),
                WorldId.Primary,
                new WorldId("arena"));
            control.Statuses = [];
            SandboxTransferResult lost = routing.AcceptTransfer(prepared.Ticket!, new WorldId("arena"));

            Assert.Multiple(() =>
            {
                Assert.That(full.ErrorCode, Is.EqualTo("sandbox.transfer.full"));
                Assert.That(full.EffectiveWorldId, Is.EqualTo(WorldId.Primary));
                Assert.That(lost.ErrorCode, Is.EqualTo("sandbox.transfer.backend_lost"));
                Assert.That(lost.EffectiveWorldId, Is.EqualTo(WorldId.Primary));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string FindTestBackend()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "src", "Vega.sln"))) current = current.Parent;
        Assert.That(current, Is.Not.Null);
        string targetFramework = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Name;
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
        string path = Path.Combine(
            current!.FullName,
            "tests",
            "Vega.Sandbox.TestBackend",
            "bin",
            configuration,
            targetFramework,
            "Vega.Sandbox.TestBackend.dll");
        Assert.That(File.Exists(path), Is.True, $"Test backend was not built: {path}");
        return path;
    }

    private static SandboxStatus Status(int online, int capacity)
    {
        var descriptor = new SandboxDescriptor(
            new WorldId("arena"),
            "Arena",
            "template.wld",
            "world.wld",
            SandboxPersistenceMode.Temporary,
            capacity,
            new SandboxAccessPolicy(SandboxAccessMode.Public),
            TimeSpan.FromMinutes(30),
            SandboxSscMode.Disabled,
            new HashSet<string>(),
            7780,
            1,
            DateTimeOffset.UtcNow,
            "revision");
        return new SandboxStatus(
            descriptor,
            SandboxLifecycleState.Ready,
            new SandboxEndpoint("127.0.0.1", 7780),
            123,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            online,
            60,
            new SandboxResourceSnapshot(0, 0, 0),
            0,
            null,
            true);
    }

    private sealed class FakeControl(params SandboxStatus[] statuses) : ISandboxControlService
    {
        public IReadOnlyList<SandboxStatus> Statuses { get; set; } = statuses;
        public IReadOnlyList<SandboxStatus> Snapshot() => Statuses;
        public ValueTask<SandboxOperationResult> CreateAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> StartAsync(WorldId worldId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> StopAsync(WorldId worldId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> RestartAsync(WorldId worldId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> ResetAsync(WorldId worldId, string? replacementSource = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SandboxOperationResult> DestroyAsync(WorldId worldId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }
}
