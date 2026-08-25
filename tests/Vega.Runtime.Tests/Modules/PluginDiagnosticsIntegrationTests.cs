using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;
using Vega.Contracts.Diagnostics;
using Vega.Contracts.Operations;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Modules;
using Vega.PluginSdk;
using Vega.Sandboxes;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
[NonParallelizable]
public sealed class PluginDiagnosticsIntegrationTests
{
    [Test]
    public async Task Ordinary_plugin_configuration_supports_reload_and_validated_debug_patch()
    {
        string pluginName = $"Test.Config.{Guid.NewGuid():N}";
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"vega-plugin-data-{Guid.NewGuid():N}");
        string configurationDirectory = Path.Combine(AppContext.BaseDirectory, "config", "plugins", pluginName);
        var scope = new HotModuleRegistrationScope(
            new CommandDispatcher([]),
            NullLogger.Instance,
            pluginName,
            dataDirectory);
        IVegaDebugContext debug = new VegaDebugRegistry(Options(allowStateMutation: true))
            .CreateContext("VegaToolsMCP");

        try
        {
            IPluginConfig<TestConfig> config = scope.Configuration.Use(
                "settings.json",
                static () => new TestConfig(2, "initial"),
                static value => value.Limit > 0 ? null : "limit must be positive");

            Assert.That(config.Current.Limit, Is.EqualTo(2));
            string path = Path.Combine(configurationDirectory, "settings.json");
            Assert.That(File.Exists(path), Is.True);

            File.WriteAllText(path, "{\"limit\":5,\"name\":\"disk\"}");
            TestConfig reloaded = config.Reload();
            Assert.That(reloaded, Is.EqualTo(new TestConfig(5, "disk")));

            VegaDebugPluginConfigurationResult rejected = debug.PatchPluginConfiguration(
                pluginName,
                "settings.json",
                JsonSerializer.SerializeToElement(new { limit = -1 }));
            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(config.Current.Limit, Is.EqualTo(5));

            VegaDebugPluginConfigurationResult applied = debug.PatchPluginConfiguration(
                pluginName,
                "settings.json",
                JsonSerializer.SerializeToElement(new { limit = 9 }));
            Assert.That(applied.Succeeded, Is.True, applied.Error);
            Assert.That(config.Current.Limit, Is.EqualTo(9));
            Assert.That(debug.SnapshotPluginConfigurations().Any(item =>
                item.Plugin == pluginName && item.FileName == "settings.json"), Is.True);
        }
        finally
        {
            await scope.DisposeAsync();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
            if (Directory.Exists(configurationDirectory)) Directory.Delete(configurationDirectory, recursive: true);
        }

        Assert.That(debug.SnapshotPluginConfigurations().Any(item => item.Plugin == pluginName), Is.False);
    }

    [Test]
    public async Task Plugin_scope_receives_primary_sandbox_control_through_runtime_bridge()
    {
        var control = new FakeSandboxControl();
        using var command = new SandboxCommand(control, new FakeSandboxRouting(), NullLogger.Instance);
        string pluginName = $"Test.Sandbox.{Guid.NewGuid():N}";
        string dataDirectory = Path.Combine(Path.GetTempPath(), $"vega-plugin-data-{Guid.NewGuid():N}");
        var scope = new HotModuleRegistrationScope(
            new CommandDispatcher([]),
            NullLogger.Instance,
            pluginName,
            dataDirectory);

        try
        {
            Assert.That(scope.Sandboxes.CanControl, Is.True);
            Assert.That(scope.Sandboxes.Snapshot().Single().Descriptor.WorldId, Is.EqualTo(new WorldId("debug")));
            SandboxOperationResult result = await scope.Sandboxes.StartAsync(new WorldId("debug"));
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(control.StartCalls, Is.EqualTo(1));
        }
        finally
        {
            await scope.DisposeAsync();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public void Debug_network_trace_is_bounded_and_collects_pipeline_timings()
    {
        IVegaDebugContext debug = new VegaDebugRegistry(Options()).CreateContext("VegaToolsMCP");
        debug.StartNetworkTrace(new VegaDebugNetworkTraceOptions(DurationSeconds: 5, MaxEvents: 16));
        try
        {
            using var network = new DiagnosticListener("Vega.Network");
            using var pipeline = new DiagnosticListener("Vega.PacketPipeline");

            if (network.IsEnabled("Packet.Inbound"))
            {
                network.Write("Packet.Inbound", new NetworkPacketDiagnosticEvent(
                    "Inbound", 17, 42, 3, DateTimeOffset.UtcNow));
            }
            if (pipeline.IsEnabled("ProcessorCompleted"))
            {
                pipeline.Write("ProcessorCompleted", new PacketProcessorDiagnosticEvent(
                    "Test.Policy",
                    "Multiplicity.Packets.Views.PacketView",
                    "Inbound",
                    "3",
                    "3",
                    12.5,
                    "Allow",
                    null));
                pipeline.Write("ProcessorCompleted", new PacketProcessorDiagnosticEvent(
                    "Test.Policy",
                    "Multiplicity.Packets.Views.PacketView",
                    "Inbound",
                    "3",
                    "3",
                    7.5,
                    "Reject",
                    "test.reject"));
            }

            VegaDebugNetworkTraceSnapshot snapshot = debug.CaptureNetworkTrace();
            Assert.That(snapshot.Active, Is.True);
            Assert.That(snapshot.Packets, Has.Count.EqualTo(1));
            Assert.That(snapshot.Packets[0].PacketId, Is.EqualTo(17));
            VegaDebugPacketProcessorMetric metric = snapshot.ProcessorMetrics.Single();
            Assert.That(metric.Calls, Is.EqualTo(2));
            Assert.That(metric.TotalMicroseconds, Is.EqualTo(20).Within(0.001));
            Assert.That(metric.NonAllowDecisions, Is.EqualTo(1));
            Assert.That(metric.LastReason, Is.EqualTo("test.reject"));
        }
        finally
        {
            debug.StopNetworkTrace();
        }

        Assert.That(debug.CaptureNetworkTrace().Active, Is.False);
    }

    private static VegaDebugOptions Options(bool allowStateMutation = false) => new(
        true,
        ["VegaToolsMCP"],
        false,
        false,
        allowStateMutation,
        false,
        256,
        4096);

    private sealed record TestConfig(int Limit, string Name);

    private sealed class NullLogger : IOperationsLogger
    {
        public static NullLogger Instance { get; } = new();

        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }

    private sealed class FakeSandboxControl : ISandboxControlService
    {
        private readonly SandboxStatus status;

        public FakeSandboxControl()
        {
            var worldId = new WorldId("debug");
            status = new SandboxStatus(
                new SandboxDescriptor(
                    worldId,
                    "Debug",
                    "source.wld",
                    "instance.wld",
                    SandboxPersistenceMode.Temporary,
                    8,
                    new SandboxAccessPolicy(SandboxAccessMode.Public),
                    null,
                    SandboxSscMode.Disabled,
                    new HashSet<string>(),
                    7788,
                    1,
                    DateTimeOffset.UtcNow,
                    "test"),
                SandboxLifecycleState.Stopped,
                null,
                null,
                null,
                null,
                null,
                0,
                60,
                new SandboxResourceSnapshot(0, 0, 0),
                0,
                null,
                false);
        }

        public int StartCalls { get; private set; }

        public IReadOnlyList<SandboxStatus> Snapshot() => [status];

        public ValueTask<SandboxOperationResult> CreateAsync(
            SandboxCreateRequest request,
            CancellationToken cancellationToken = default) => Applied();

        public ValueTask<SandboxOperationResult> StartAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Applied();
        }

        public ValueTask<SandboxOperationResult> StopAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) => Applied();

        public ValueTask<SandboxOperationResult> RestartAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) => Applied();

        public ValueTask<SandboxOperationResult> ResetAsync(
            WorldId worldId,
            string? replacementSource = null,
            CancellationToken cancellationToken = default) => Applied();

        public ValueTask<SandboxOperationResult> DestroyAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) => Applied();

        public void Dispose()
        {
        }

        private ValueTask<SandboxOperationResult> Applied() => ValueTask.FromResult(
            new SandboxOperationResult(SandboxOperationStatus.Applied, status));
    }

    private sealed class FakeSandboxRouting : ISandboxRoutingService
    {
        public SandboxTransferResult PrepareEnter(
            string accountId,
            IReadOnlySet<string> permissions,
            WorldId originWorldId,
            WorldId targetWorldId) => Rejected(targetWorldId);

        public SandboxTransferResult PrepareLeave(string accountId, WorldId originWorldId) =>
            Rejected(WorldId.Primary);

        public SandboxTransferResult PrepareTransfer(
            string accountId,
            IReadOnlySet<string> permissions,
            WorldId originWorldId,
            WorldId targetWorldId) => Rejected(targetWorldId);

        public SandboxTransferResult AcceptTransfer(SandboxTransferTicket ticket, WorldId receivingWorldId) =>
            Rejected(receivingWorldId);

        private static SandboxTransferResult Rejected(WorldId worldId) =>
            new(false, worldId, null, null, "test", "not used");
    }
}
