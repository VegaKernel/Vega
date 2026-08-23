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
                "-world",
                "arena.wld"
            ],
            name => name == "VEGA_BACKEND_TOKEN" ? Convert.ToBase64String(key) : null);

        Assert.Multiple(() =>
        {
            Assert.That(launch.IsBackend, Is.True);
            Assert.That(launch.WorldId, Is.EqualTo(new WorldId("arena")));
            Assert.That(launch.EnabledModules, Is.EquivalentTo(new[] { "WorldEdit", "RegionVision" }));
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
                SandboxSscMode.Disabled,
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
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Vega.sln"))) current = current.Parent;
        Assert.That(current, Is.Not.Null);
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
        string path = Path.Combine(
            current!.FullName,
            "tests",
            "Vega.Sandbox.TestBackend",
            "bin",
            configuration,
            "net10.0",
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
