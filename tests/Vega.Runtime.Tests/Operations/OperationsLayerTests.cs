using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Core.Commands;
using Vega.Contracts.Runtime;
using Vega.Operations.Actions;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Operations.ReadModels;
using Vega.TerminalUI;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class BoundedOperationsLogStoreTests
{
    [Test]
    public void RetentionIsBoundedAndKeepsNewestRecords()
    {
        var store = new BoundedOperationsLogStore(3);
        var logger = new StructuredOperationsLogger([store]);

        for (int index = 1; index <= 5; index++)
        {
            logger.Log(OperationsLogLevel.Information, "test", $"message {index}");
        }

        IReadOnlyList<OperationsLogRecord> records = store.Query(new OperationsLogFilter());

        Assert.That(records.Select(static record => record.Text), Is.EqualTo(new[]
        {
            "message 3",
            "message 4",
            "message 5"
        }));
    }

    [Test]
    public void QueryFiltersLevelSourceTextAndLimit()
    {
        var store = new BoundedOperationsLogStore(10);
        var logger = new StructuredOperationsLogger([store]);
        logger.Log(OperationsLogLevel.Warning, "network", "slow peer alpha");
        logger.Log(OperationsLogLevel.Error, "network", "failed peer beta");
        logger.Log(OperationsLogLevel.Error, "storage", "failed save beta");

        IReadOnlyList<OperationsLogRecord> records = store.Query(new OperationsLogFilter(
            OperationsLogLevel.Error,
            "network",
            "beta",
            1));

        Assert.That(records.Select(static record => record.Text), Is.EqualTo(new[] { "failed peer beta" }));
    }

    [Test]
    public void ConcurrentWritersCannotExceedCapacity()
    {
        var store = new BoundedOperationsLogStore(64);

        Parallel.For(0, 1_000, index => store.Emit(new OperationsLogRecord(
            index,
            DateTimeOffset.UtcNow,
            OperationsLogLevel.Debug,
            "parallel",
            index.ToString())));

        Assert.That(store.Query(new OperationsLogFilter(Limit: 1_000)), Has.Count.EqualTo(64));
    }

    [Test]
    public void FailingSinkDoesNotBlockIndependentSinks()
    {
        var store = new BoundedOperationsLogStore(4);
        var logger = new StructuredOperationsLogger([new ThrowingSink(), store]);

        logger.Log(OperationsLogLevel.Error, "test", "still recorded");

        Assert.That(store.Query(new OperationsLogFilter()).Single().Text, Is.EqualTo("still recorded"));
    }

    private sealed class ThrowingSink : IOperationsLogSink
    {
        public void Emit(OperationsLogRecord record) => throw new IOException("sink unavailable");
    }
}

[TestFixture]
public sealed class OperationsCommandServiceTests
{
    [Test]
    public async Task CommandsUseTheNormalDispatcherAuthorizationBoundary()
    {
        var handler = new RecordingCommandHandler();
        var service = new OperationsCommandService(new CommandDispatcher([handler]));

        OperationsActionResult result = await service.ExecuteAsync("/operate now");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Messages, Is.EqualTo(new[] { "operation now" }));
            Assert.That(handler.SawServerPrincipal, Is.True);
            Assert.That(handler.SawPermission, Is.True);
        });
    }

    private sealed class RecordingCommandHandler : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(
            "operate",
            Array.Empty<string>(),
            ["vega.operations"]);

        public bool SawServerPrincipal { get; private set; }

        public bool SawPermission { get; private set; }

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            SawServerPrincipal = invocation.Principal.IsServer;
            SawPermission = invocation.Principal.HasPermission("vega.operations");
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("operation.done", invocation.Arguments[0])
                    .WithDefaultText("operation {0}")));
        }
    }
}

[TestFixture]
public sealed class OperationsReadModelTests
{
    [Test]
    public void SnapshotCombinesImmutablePlayersModulesLogsAndCheapMetrics()
    {
        var lifecycle = new FakeLifecycle(RuntimeLifecycleStage.NetworkReady, TimeSpan.FromMinutes(2));
        var player = new PlayerSnapshot(
            new PlayerId(4),
            "VegaPlayer",
            false,
            100,
            100,
            20,
            20,
            new PlayerPosition(10, 20),
            false,
            1);
        var playerDirectory = new MutablePlayerDirectory([player]);
        var modules = new StaticModuleOperationsCatalog([
            new ModuleOperationsSnapshot(
                "healthy",
                "1.0",
                ModuleLoadState.Loaded,
                Array.Empty<string>(),
                TimeSpan.FromMilliseconds(2)),
            new ModuleOperationsSnapshot(
                "optional",
                "1.0",
                ModuleLoadState.Degraded,
                ["external"],
                TimeSpan.FromMilliseconds(3),
                "offline")
        ]);
        var logs = new BoundedOperationsLogStore(5);
        logs.Emit(new OperationsLogRecord(
            1,
            DateTimeOffset.UtcNow,
            OperationsLogLevel.Information,
            "test",
            "ready"));
        var provider = new OperationsSnapshotProvider(
            "Vega",
            "6.0",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2),
            lifecycle,
            playerDirectory,
            new FixedServerState(),
            modules,
            logs,
            new FixedRuntimeMetrics(),
            new FixedNetworkMetrics());

        OperationsSnapshot snapshot = provider.Capture();
        playerDirectory.Players.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Server.LifecycleStage, Is.EqualTo(RuntimeLifecycleStage.NetworkReady));
            Assert.That(snapshot.Server.WorldName, Is.EqualTo("Test World"));
            Assert.That(snapshot.Server.OnlinePlayers, Is.EqualTo(1));
            Assert.That(snapshot.Server.Warnings.Single(), Does.Contain("optional"));
            Assert.That(snapshot.Players.Single().Name, Is.EqualTo("VegaPlayer"));
            Assert.That(snapshot.Modules, Has.Count.EqualTo(2));
            Assert.That(snapshot.Logs.Single().Text, Is.EqualTo("ready"));
            Assert.That(snapshot.Runtime.ManagedMemoryBytes, Is.EqualTo(200));
            Assert.That(snapshot.Network.InboundPackets, Is.EqualTo(7));
        });
    }

    [Test]
    public void NetworkCountersAreAtomicSnapshots()
    {
        var metrics = new NetworkMetricsCounter();
        metrics.RecordInbound(10);
        metrics.RecordInbound(20);
        metrics.RecordOutbound(30);

        NetworkMetricsSnapshot snapshot = metrics.Read();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.InboundPackets, Is.EqualTo(2));
            Assert.That(snapshot.InboundBytes, Is.EqualTo(30));
            Assert.That(snapshot.OutboundPackets, Is.EqualTo(1));
            Assert.That(snapshot.OutboundBytes, Is.EqualTo(30));
        });
    }

    private sealed class FakeLifecycle(RuntimeLifecycleStage stage, TimeSpan elapsed) : IRuntimeLifecycle
    {
        public RuntimeLifecycleStage CurrentStage => stage;

        public TimeSpan Elapsed => elapsed;

        public event Action<RuntimeLifecycleTransition>? Transitioned
        {
            add { }
            remove { }
        }
    }

    private sealed class MutablePlayerDirectory(List<PlayerSnapshot> players) : IPlayerDirectory
    {
        public List<PlayerSnapshot> Players { get; } = players;

        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Players;

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = Players.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = Players.FirstOrDefault(candidate => candidate.Name == name);
            return player is not null;
        }
    }

    private sealed class FixedServerState : IServerRuntimeStateReader
    {
        public ServerRuntimeStateSnapshot Read() => new(
            "Test World",
            16,
            7777,
            true,
            60,
            DateTimeOffset.UtcNow);
    }

    private sealed class FixedRuntimeMetrics : IRuntimeMetricsReader
    {
        public RuntimeMetricsSnapshot Read() => new(
            new HostMetricsSnapshot(100, 1_000, TimeSpan.FromSeconds(1), "test", 4, "host"),
            200,
            300,
            10,
            400,
            1,
            2,
            3,
            8,
            16,
            DateTimeOffset.UtcNow);
    }

    private sealed class FixedNetworkMetrics : INetworkMetricsReader
    {
        public NetworkMetricsSnapshot Read() => new(7, 70, 8, 80, DateTimeOffset.UtcNow);
    }
}

[TestFixture]
public sealed class TerminalOperationsTests
{
    [TestCase("tui", OperationsUiMode.Terminal)]
    [TestCase("plain", OperationsUiMode.Plain)]
    [TestCase("headless", OperationsUiMode.Headless)]
    public void ExplicitModesAreParsedAndNotForwardedToTerraria(
        string mode,
        OperationsUiMode expected)
    {
        OperationsUiOptions options = OperationsUiModeParser.Parse([
            "-world",
            "world.wld",
            $"--vega-ui={mode}"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.EffectiveMode, Is.EqualTo(expected));
            Assert.That(options.ServerArguments, Is.EqualTo(new[] { "-world", "world.wld" }));
        });
    }

    [Test]
    public async Task PauseFollowAndLogFiltersAreViewModelStateOnly()
    {
        var provider = new RecordingSnapshotProvider(CreateSnapshot());
        var commands = new RecordingOperationsCommands();
        var viewModel = new OperationsDashboardViewModel(provider, commands);

        viewModel.Refresh();
        await viewModel.ExecutePaletteAsync(":pause");
        viewModel.Refresh();
        await viewModel.ExecutePaletteAsync(":level warning");
        viewModel.Refresh();
        string commandResult = await viewModel.ExecutePaletteAsync("serverinfo");
        await viewModel.ExecutePaletteAsync(":follow");
        string rendered = viewModel.Render();

        Assert.Multiple(() =>
        {
            Assert.That(provider.CaptureCount, Is.EqualTo(3));
            Assert.That(provider.LastFilter?.MinimumLevel, Is.EqualTo(OperationsLogLevel.Warning));
            Assert.That(commands.LastCommand, Is.EqualTo("serverinfo"));
            Assert.That(commandResult, Does.StartWith("Success:"));
            Assert.That(rendered, Does.Contain("SERVER"));
            Assert.That(rendered, Does.Contain("PLAYERS"));
            Assert.That(rendered, Does.Contain("MODULES"));
            Assert.That(rendered, Does.Contain("LOGS FOLLOW"));
        });
    }

    [TestCase(OperationsUiMode.Plain)]
    [TestCase(OperationsUiMode.Headless)]
    public void NonInteractiveModesNeverInitializeTerminalGui(OperationsUiMode mode)
    {
        var logger = new StructuredOperationsLogger([new BoundedOperationsLogStore(4)]);
        using var host = new TerminalUiHost(
            new RecordingSnapshotProvider(CreateSnapshot()),
            new RecordingOperationsCommands(),
            logger);

        Assert.That(host.TryStart(mode), Is.False);
    }

    [Test]
    public void TerminalModeWaitsUntilNetworkReadyBeforeTakingConsoleInput()
    {
        var lifecycle = new MutableLifecycle();
        int startCount = 0;
        using var gate = new TerminalUiReadinessGate(
            lifecycle,
            OperationsUiMode.Terminal,
            _ =>
            {
                startCount++;
                return true;
            });

        lifecycle.Advance(RuntimeLifecycleStage.WorldLoading);
        lifecycle.Advance(RuntimeLifecycleStage.WorldReady);

        Assert.That(startCount, Is.Zero, "Terraria must retain stdin through world selection and loading.");

        lifecycle.Advance(RuntimeLifecycleStage.NetworkReady);
        lifecycle.Advance(RuntimeLifecycleStage.FullyWarmed);

        Assert.That(startCount, Is.EqualTo(1));
    }

    [Test]
    public void TerminalModeStartsImmediatelyWhenRuntimeIsAlreadyReady()
    {
        var lifecycle = new MutableLifecycle(RuntimeLifecycleStage.NetworkReady);
        int startCount = 0;

        using var gate = new TerminalUiReadinessGate(
            lifecycle,
            OperationsUiMode.Terminal,
            _ =>
            {
                startCount++;
                return true;
            });

        Assert.That(startCount, Is.EqualTo(1));
    }

    private static OperationsSnapshot CreateSnapshot()
    {
        var server = new ServerDashboardSnapshot(
            "Vega",
            "6.0",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            RuntimeLifecycleStage.NetworkReady,
            "World",
            true,
            true,
            0,
            16,
            7777,
            60,
            Array.Empty<string>());
        var runtime = new RuntimeMetricsSnapshot(
            new HostMetricsSnapshot(1, 2, TimeSpan.Zero, "test", 1, "host"),
            3,
            4,
            0,
            5,
            0,
            0,
            0,
            1,
            1,
            DateTimeOffset.UtcNow);

        return new OperationsSnapshot(
            server,
            Array.Empty<PlayerSnapshot>(),
            [new ModuleOperationsSnapshot("Vega", "1", ModuleLoadState.Loaded, Array.Empty<string>(), TimeSpan.Zero)],
            Array.Empty<OperationsLogRecord>(),
            runtime,
            new NetworkMetricsSnapshot(0, 0, 0, 0, DateTimeOffset.UtcNow));
    }

    private sealed class RecordingSnapshotProvider(OperationsSnapshot snapshot) : IOperationsSnapshotProvider
    {
        public int CaptureCount { get; private set; }

        public OperationsLogFilter? LastFilter { get; private set; }

        public OperationsSnapshot Capture(OperationsLogFilter? logFilter = null)
        {
            CaptureCount++;
            LastFilter = logFilter;
            return snapshot;
        }
    }

    private sealed class RecordingOperationsCommands : IOperationsCommandService
    {
        public string? LastCommand { get; private set; }

        public ValueTask<OperationsActionResult> ExecuteAsync(
            string commandText,
            CancellationToken cancellationToken = default)
        {
            LastCommand = commandText;
            return ValueTask.FromResult(new OperationsActionResult(
                CommandExecutionStatus.Success,
                null,
                ["done"]));
        }
    }

    private sealed class MutableLifecycle(
        RuntimeLifecycleStage initialStage = RuntimeLifecycleStage.ProcessStart) : IRuntimeLifecycle
    {
        public RuntimeLifecycleStage CurrentStage { get; private set; } = initialStage;

        public TimeSpan Elapsed => TimeSpan.Zero;

        public event Action<RuntimeLifecycleTransition>? Transitioned;

        public void Advance(RuntimeLifecycleStage stage)
        {
            CurrentStage = stage;
            Transitioned?.Invoke(new RuntimeLifecycleTransition(stage, TimeSpan.Zero, TimeSpan.Zero));
        }
    }
}
