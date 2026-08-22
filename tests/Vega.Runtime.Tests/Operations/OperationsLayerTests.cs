using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Core.Commands;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;
using Vega.Operations.Actions;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Operations.ReadModels;
using Vega.Core.Localization;
using Vega.TerminalUI;
using Vega.Terraria.Operations;

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
        var localization = new LocalizationService("en-US");
        var service = new OperationsCommandService(
            new CommandDispatcher([handler]),
            localization,
            localization);

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
            1)
        {
            RemoteEndpoint = "203.0.113.8:7777",
            Connection = new ConnectionMetadataSnapshot(
                new ConnectionId(4),
                "203.0.113.8:7777",
                "203.0.113.8:7777",
                ConnectionForwardingProvenance.Direct,
                new ClientPlatformInfo(ClientPlatformKind.Pc, 233),
                "755a3c87-6f2b-4ec9-aed4-b8eeaf855c81",
                ConnectionLoginPhase.FullyConnected)
        };
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
        OperationsSnapshot sensitive = provider.Capture(
            exposure: OperationsSnapshotExposure.Sensitive);
        playerDirectory.Players.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Server.LifecycleStage, Is.EqualTo(RuntimeLifecycleStage.NetworkReady));
            Assert.That(snapshot.Server.WorldName, Is.EqualTo("Test World"));
            Assert.That(snapshot.Server.OnlinePlayers, Is.EqualTo(1));
            Assert.That(snapshot.Server.TickRate.AverageOneMinute, Is.EqualTo(59));
            Assert.That(snapshot.Server.Warnings.Single(), Does.Contain("optional"));
            Assert.That(snapshot.Players.Single().Name, Is.EqualTo("VegaPlayer"));
            Assert.That(snapshot.Players.Single().RemoteEndpoint, Is.Null);
            Assert.That(snapshot.Players.Single().Connection?.ClientUuid, Is.Null);
            Assert.That(sensitive.Players.Single().RemoteEndpoint, Is.EqualTo("203.0.113.8:7777"));
            Assert.That(sensitive.Players.Single().Connection?.ClientUuid, Is.Not.Null);
            Assert.That(snapshot.Modules, Has.Count.EqualTo(2));
            Assert.That(snapshot.Logs.Single().Text, Is.EqualTo("ready"));
            Assert.That(snapshot.Runtime.ManagedMemoryBytes, Is.EqualTo(200));
            Assert.That(snapshot.Network.InboundPackets, Is.EqualTo(7));
        });
    }

    [Test]
    public void WorldCleanupStatusIsSurfacedAndFailuresBecomeWarnings()
    {
        var lifecycle = new FakeLifecycle(RuntimeLifecycleStage.NetworkReady, TimeSpan.Zero);
        var provider = new OperationsSnapshotProvider(
            "Vega",
            "6.0",
            DateTimeOffset.UtcNow,
            lifecycle,
            new MutablePlayerDirectory([]),
            new FixedServerState(),
            new StaticModuleOperationsCatalog([]),
            new BoundedOperationsLogStore(5),
            new FixedRuntimeMetrics(),
            new FixedNetworkMetrics(),
            worldCleanup: new FixedWorldCleanupStatus());

        OperationsSnapshot snapshot = provider.Capture();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.WorldCleanup.Enabled, Is.True);
            Assert.That(snapshot.WorldCleanup.State, Is.EqualTo(WorldCleanupRunState.Failed));
            Assert.That(snapshot.WorldCleanup.Detail, Is.EqualTo("auto_world_cleaner.failed"));
            Assert.That(snapshot.Server.Warnings, Has.Some.Contains("AutoWorldCleaner failed"));
        });
    }

    [Test]
    public void NetworkCountersAreAtomicSnapshots()
    {
        var metrics = new NetworkMetricsCounter();
        metrics.RecordInbound(10);
        metrics.RecordInbound(20);
        metrics.RecordOutbound(30);
        metrics.RecordSecurityDecision(PacketProcessingDecision.Reject("packet.invalid_payload"));
        metrics.RecordSecurityDecision(PacketProcessingDecision.Violation("packet.player_identity_mismatch"));
        metrics.RecordSecurityDecision(PacketProcessingDecision.Violation("packet.player_identity_mismatch"));

        NetworkMetricsSnapshot snapshot = metrics.Read();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.InboundPackets, Is.EqualTo(2));
            Assert.That(snapshot.InboundBytes, Is.EqualTo(30));
            Assert.That(snapshot.OutboundPackets, Is.EqualTo(1));
            Assert.That(snapshot.OutboundBytes, Is.EqualTo(30));
            Assert.That(snapshot.RejectedInboundPackets, Is.EqualTo(1));
            Assert.That(snapshot.SecurityViolations, Is.EqualTo(2));
            Assert.That(snapshot.SecurityRuleCounts["packet.player_identity_mismatch"], Is.EqualTo(2));
        });
    }

    [Test]
    public void SecurityRuleCountersStayBounded()
    {
        var metrics = new NetworkMetricsCounter();
        for (int index = 0; index < 70; index++)
        {
            metrics.RecordSecurityDecision(PacketProcessingDecision.Violation($"packet.rule.{index}"));
        }

        NetworkMetricsSnapshot snapshot = metrics.Read();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SecurityRuleCounts, Has.Count.EqualTo(64));
            Assert.That(snapshot.SecurityRuleCounts["other"], Is.EqualTo(7));
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
        public ServerRuntimeStateSnapshot Read()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new ServerRuntimeStateSnapshot(
                "Test World",
                16,
                7777,
                true,
                60,
                now)
            {
                TickRate = new TickRateStatisticsSnapshot(
                    60,
                    59,
                    50,
                    now.AddSeconds(-30),
                    60,
                    now,
                    60,
                    16.5,
                    18,
                    20)
            };
        }
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

    private sealed class FixedWorldCleanupStatus : IWorldCleanupStatusReader
    {
        public WorldCleanupStatusSnapshot Read() => new(
            WorldId.Primary,
            true,
            WorldCleanupRunState.Failed,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            0,
            0,
            "auto_world_cleaner.failed");
    }
}

[TestFixture]
public sealed class TickRateMonitorTests
{
    [Test]
    public void MonitorPublishesBoundedOneMinuteAverageAndLifetimeExtremes()
    {
        var time = new ManualTimeProvider();
        var monitor = new TickRateMonitor(time);

        TickRateStatisticsSnapshot first = Sample(monitor, time, 60);
        TickRateStatisticsSnapshot second = Sample(monitor, time, 30);

        Assert.Multiple(() =>
        {
            Assert.That(first.Current, Is.EqualTo(60).Within(0.001));
            Assert.That(second.Current, Is.EqualTo(30).Within(0.001));
            Assert.That(second.AverageOneMinute, Is.EqualTo(45).Within(0.001));
            Assert.That(second.Minimum, Is.EqualTo(30).Within(0.001));
            Assert.That(second.Maximum, Is.EqualTo(60).Within(0.001));
            Assert.That(second.MinimumAt, Is.EqualTo(time.GetUtcNow()));
            Assert.That(second.SampleCount, Is.EqualTo(2));
        });

        for (int index = 0; index < 65; index++)
        {
            Sample(monitor, time, 50);
        }

        TickRateStatisticsSnapshot bounded = Sample(monitor, time, 40);
        Assert.Multiple(() =>
        {
            Assert.That(bounded.SampleCount, Is.EqualTo(TickRateMonitor.OneMinuteSampleCapacity));
            Assert.That(bounded.AverageOneMinute, Is.EqualTo((59 * 50 + 40) / 60d).Within(0.001));
            Assert.That(bounded.Minimum, Is.EqualTo(30).Within(0.001));
            Assert.That(bounded.Maximum, Is.EqualTo(60).Within(0.001));
        });
    }

    [Test]
    public void ResetClearsHistoryAndRestartsTheSamplingWindow()
    {
        var time = new ManualTimeProvider();
        var monitor = new TickRateMonitor(time);
        Sample(monitor, time, 60);

        monitor.Reset();
        time.Advance(TimeSpan.FromSeconds(1));
        TickRateStatisticsSnapshot snapshot = monitor.RecordTick();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Current, Is.EqualTo(1).Within(0.001));
            Assert.That(snapshot.Minimum, Is.EqualTo(1).Within(0.001));
            Assert.That(snapshot.Maximum, Is.EqualTo(1).Within(0.001));
            Assert.That(snapshot.SampleCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void TickDurationPercentilesAreComputedFromBoundedIntervals()
    {
        var time = new ManualTimeProvider();
        var monitor = new TickRateMonitor(time, TimeSpan.FromMilliseconds(5_050));

        monitor.RecordTick();
        for (int index = 1; index <= 100; index++)
        {
            time.Advance(TimeSpan.FromMilliseconds(index));
            monitor.RecordTick();
        }

        TickRateStatisticsSnapshot snapshot = monitor.RecordTick();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TickDurationP50Milliseconds, Is.EqualTo(50).Within(0.001));
            Assert.That(snapshot.TickDurationP95Milliseconds, Is.EqualTo(95).Within(0.001));
            Assert.That(snapshot.TickDurationP99Milliseconds, Is.EqualTo(99).Within(0.001));
        });
    }

    private static TickRateStatisticsSnapshot Sample(
        TickRateMonitor monitor,
        ManualTimeProvider time,
        int ticks)
    {
        for (int index = 0; index < ticks - 1; index++)
        {
            monitor.RecordTick();
        }

        time.Advance(TimeSpan.FromSeconds(1));
        return monitor.RecordTick();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            timestamp += duration.Ticks;
            utcNow += duration;
        }
    }
}

[TestFixture]
public sealed class TerminalUiLayoutTests
{
    [Test]
    public void LayoutNormalizationRestoresMissingWindowsAndBoundsUnsafeValues()
    {
        var candidate = new TerminalUiLayoutOptions(
            1,
            10_000,
            new Dictionary<string, TerminalUiPanelLayout>
            {
                ["OVERVIEW"] = new(true, -10, 200, 1, 500)
            });

        TerminalUiLayoutOptions normalized = TerminalUiLayoutOptions.Normalize(candidate);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.RefreshIntervalMilliseconds, Is.EqualTo(250));
            Assert.That(normalized.HistorySamples, Is.EqualTo(600));
            Assert.That(normalized.Windows, Has.Count.EqualTo(TerminalUiLayoutOptions.WindowNames.Count));
            Assert.That(normalized.GetWindow(TerminalUiLayoutOptions.Overview), Is.EqualTo(new TerminalUiPanelLayout(true, 0, 95, 15, 100)));
            Assert.That(normalized.GetWindow(TerminalUiLayoutOptions.Logs).Visible, Is.True);
        });
    }

    [Test]
    public void MetricHistoryCalculatesRatesAndKeepsBoundedNewestSamples()
    {
        var history = new OperationsMetricHistory(20);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        for (int index = 0; index < 25; index++)
        {
            history.Add(CreateSnapshot(index, started.AddSeconds(index)));
        }

        OperationsMetricSample latest = history.Samples[^1];
        Assert.Multiple(() =>
        {
            Assert.That(history.Samples, Has.Count.EqualTo(20));
            Assert.That(history.Samples[0].TicksPerSecond, Is.EqualTo(5));
            Assert.That(latest.CpuPercent, Is.EqualTo(25).Within(0.001));
            Assert.That(latest.InboundKiBPerSecond, Is.EqualTo(1).Within(0.001));
            Assert.That(latest.OutboundKiBPerSecond, Is.EqualTo(2).Within(0.001));
        });
    }

    private static OperationsSnapshot CreateSnapshot(int index, DateTimeOffset sampledAt)
    {
        var server = new ServerDashboardSnapshot(
            "Vega",
            "1.0",
            sampledAt,
            TimeSpan.FromSeconds(index),
            RuntimeLifecycleStage.NetworkReady,
            "World",
            true,
            true,
            0,
            16,
            7777,
            index,
            Array.Empty<string>());
        var runtime = new RuntimeMetricsSnapshot(
            new HostMetricsSnapshot(
                index * 1024 * 1024,
                0,
                TimeSpan.FromSeconds(index * 0.5),
                "test",
                2,
                "host"),
            0,
            index * 512 * 1024,
            0,
            0,
            0,
            0,
            0,
            1,
            1,
            sampledAt);

        return new OperationsSnapshot(
            server,
            Array.Empty<PlayerSnapshot>(),
            Array.Empty<ModuleOperationsSnapshot>(),
            Array.Empty<OperationsLogRecord>(),
            runtime,
            new NetworkMetricsSnapshot(index * 10, index * 1024, index * 20, index * 2048, sampledAt));
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

    [Test]
    public async Task UnknownVegaCommandsAreForwardedToTheNativeConsoleQueue()
    {
        var fallback = new RecordingUnhandledCommandSink();
        var viewModel = new OperationsDashboardViewModel(
            new RecordingSnapshotProvider(CreateSnapshot()),
            new RecordingOperationsCommands
            {
                Result = new OperationsActionResult(
                    CommandExecutionStatus.Failed,
                    "command.not_found",
                    ["Unknown command."])
            },
            fallback);

        string result = await viewModel.ExecutePaletteAsync("save");

        Assert.Multiple(() =>
        {
            Assert.That(fallback.LastCommand, Is.EqualTo("save"));
            Assert.That(result, Is.EqualTo("Submitted to the Terraria server console."));
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
    public void TerminalModeWaitsForWorldSelectionBeforeTakingConsoleInput()
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

        Assert.That(startCount, Is.Zero, "Terraria must retain stdin through world selection.");

        lifecycle.Advance(RuntimeLifecycleStage.WorldLoading);
        Assert.That(startCount, Is.EqualTo(1), "The startup UI should begin after world selection.");

        lifecycle.Advance(RuntimeLifecycleStage.WorldReady);
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

        public OperationsSnapshot Capture(
            OperationsLogFilter? logFilter = null,
            OperationsSnapshotExposure exposure = OperationsSnapshotExposure.Redacted)
        {
            CaptureCount++;
            LastFilter = logFilter;
            return snapshot;
        }
    }

    private sealed class RecordingOperationsCommands : IOperationsCommandService
    {
        public string? LastCommand { get; private set; }

        public OperationsActionResult Result { get; init; } = new(
            CommandExecutionStatus.Success,
            null,
            ["done"]);

        public ValueTask<OperationsActionResult> ExecuteAsync(
            string commandText,
            CancellationToken cancellationToken = default)
        {
            LastCommand = commandText;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingUnhandledCommandSink : IUnhandledOperationsCommandSink
    {
        public string? LastCommand { get; private set; }

        public ValueTask SubmitAsync(string commandText, CancellationToken cancellationToken = default)
        {
            LastCommand = commandText;
            return ValueTask.CompletedTask;
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

[TestFixture]
[NonParallelizable]
public sealed class TerrariaStartupProgressTests
{
    [Test]
    public void StatusTextIsExposedAsNormalizedProgress()
    {
        using var progress = new TerrariaStartupProgress();
        progress.Attach(static () => false);

        HookEvents.Terraria.Main.Invokeset_statusText(
            null,
            static _ => { },
            "Loading world data: 42.5%");

        StartupProgressSnapshot snapshot = progress.Read();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Activity, Is.EqualTo("Loading world data"));
            Assert.That(snapshot.Fraction, Is.EqualTo(0.425d).Within(0.0001d));
            Assert.That(snapshot.RawText, Is.EqualTo("Loading world data: 42.5%"));
        });
    }

    [Test]
    public async Task ActiveTerminalUiRoutesNativeCommandsWithoutCompetingForConsoleInput()
    {
        using var progress = new TerrariaStartupProgress();
        progress.Attach(static () => true);
        await progress.SubmitAsync("save");

        HookEvents.Terraria.Main.ReadLineInputEventArgs eventArgs =
            HookEvents.Terraria.Main.InvokeReadLineInput(null, static () => throw new AssertionException("Native Console.ReadLine must not run."));

        Assert.Multiple(() =>
        {
            Assert.That(eventArgs.ContinueExecution, Is.False);
            Assert.That(eventArgs.HookReturnValue, Is.EqualTo("save"));
        });
    }
}
