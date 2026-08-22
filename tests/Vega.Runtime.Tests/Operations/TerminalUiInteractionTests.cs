using NUnit.Framework;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.TerminalUI;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class TerminalUiInteractionTests
{
    [Test]
    public void PauseFreezesOnlyLogsWhileTelemetryKeepsRefreshing()
    {
        var provider = new SequencedSnapshotProvider();
        var viewModel = new OperationsDashboardViewModel(provider, new UnusedOperationsCommands());

        OperationsSnapshot first = viewModel.Refresh();
        viewModel.SetFollowing(false);
        OperationsSnapshot second = viewModel.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(provider.CaptureCount, Is.EqualTo(2));
            Assert.That(first.Server.TicksPerSecond, Is.EqualTo(59));
            Assert.That(second.Server.TicksPerSecond, Is.EqualTo(58));
            Assert.That(second.Logs.Select(static record => record.Text), Is.EqualTo(new[] { "log 1" }));
            Assert.That(viewModel.IsFollowing, Is.False);
        });
    }

    [Test]
    public void CapturedSnapshotDoesNotMutateDisplayedStateUntilUiAppliesIt()
    {
        var provider = new SequencedSnapshotProvider();
        var viewModel = new OperationsDashboardViewModel(provider, new UnusedOperationsCommands());

        OperationsSnapshot first = viewModel.Refresh();
        OperationsSnapshot captured = viewModel.Capture();

        Assert.Multiple(() =>
        {
            Assert.That(provider.CaptureCount, Is.EqualTo(2));
            Assert.That(viewModel.Render(), Does.Contain(first.Server.TicksPerSecond.ToString("F1")));
        });

        OperationsSnapshot applied = viewModel.Apply(captured);

        Assert.That(applied.Server.TicksPerSecond, Is.EqualTo(58));
    }

    [TestCase(71, 22, false)]
    [TestCase(72, 21, false)]
    [TestCase(72, 22, true)]
    [TestCase(160, 50, true)]
    public void DashboardRequiresUsableTerminalSize(int width, int height, bool expected)
    {
        Assert.That(TerminalUiHost.IsTerminalSizeUsable(width, height), Is.EqualTo(expected));
    }

    [Test]
    public void DirectLogControlsReuseTheViewModelFilterState()
    {
        var viewModel = new OperationsDashboardViewModel(
            new SequencedSnapshotProvider(),
            new UnusedOperationsCommands());

        viewModel.SetLogFilter(OperationsLogLevel.Warning, " network ", " peer ");

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LogFilter.MinimumLevel, Is.EqualTo(OperationsLogLevel.Warning));
            Assert.That(viewModel.LogFilter.Source, Is.EqualTo("network"));
            Assert.That(viewModel.LogFilter.Text, Is.EqualTo("peer"));
            Assert.That(viewModel.ToggleFollowing(), Is.False);
        });

        viewModel.ClearLogFilters();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LogFilter.MinimumLevel, Is.Null);
            Assert.That(viewModel.LogFilter.Source, Is.Null);
            Assert.That(viewModel.LogFilter.Text, Is.Null);
            Assert.That(viewModel.LogFilter.Limit, Is.EqualTo(100));
        });
    }

    private sealed class SequencedSnapshotProvider : IOperationsSnapshotProvider
    {
        public int CaptureCount { get; private set; }

        public OperationsSnapshot Capture(
            OperationsLogFilter? logFilter = null,
            OperationsSnapshotExposure exposure = OperationsSnapshotExposure.Redacted)
        {
            CaptureCount++;
            DateTimeOffset sampledAt = DateTimeOffset.UtcNow.AddSeconds(CaptureCount);
            double tps = 60 - CaptureCount;
            var server = new ServerDashboardSnapshot(
                "Vega",
                "1.0",
                sampledAt - TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1),
                RuntimeLifecycleStage.NetworkReady,
                "World",
                true,
                true,
                0,
                16,
                7777,
                tps,
                Array.Empty<string>())
            {
                TickRate = new TickRateStatisticsSnapshot(
                    tps,
                    tps,
                    tps,
                    sampledAt,
                    tps,
                    sampledAt,
                    CaptureCount)
            };

            return new OperationsSnapshot(
                server,
                Array.Empty<PlayerSnapshot>(),
                Array.Empty<ModuleOperationsSnapshot>(),
                [new OperationsLogRecord(
                    CaptureCount,
                    sampledAt,
                    OperationsLogLevel.Information,
                    "test",
                    $"log {CaptureCount}")],
                new RuntimeMetricsSnapshot(
                    new HostMetricsSnapshot(0, 0, TimeSpan.Zero, "test", 1, "test"),
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    1,
                    1,
                    sampledAt),
                new NetworkMetricsSnapshot(0, 0, 0, 0, sampledAt));
        }
    }

    private sealed class UnusedOperationsCommands : IOperationsCommandService
    {
        public ValueTask<OperationsActionResult> ExecuteAsync(
            string commandText,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
