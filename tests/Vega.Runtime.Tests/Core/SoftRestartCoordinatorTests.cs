using NUnit.Framework;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Core.Modules;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SoftRestartCoordinatorTests
{
    [Test]
    public async Task DrainsCallbacksRejectsConcurrentRestartAndSwapsGeneration()
    {
        int created = 0;
        using var coordinator = new SoftRestartCoordinator(
            Enabled(),
            _ => Graph($"generation-{++created}"),
            new NullLogger());
        Assert.That(coordinator.TryAcquireCallback(out IModuleCallbackLease? callback), Is.True);

        Task<SoftRestartResult> first = coordinator.RestartAsync(null).AsTask();
        await Task.Delay(50);
        SoftRestartResult concurrent = await coordinator.RestartAsync(null);
        Assert.Multiple(() =>
        {
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(concurrent.Status, Is.EqualTo(SoftRestartStatus.Busy));
        });

        callback!.Dispose();
        SoftRestartResult completed = await first;
        Assert.Multiple(() =>
        {
            Assert.That(completed.Status, Is.EqualTo(SoftRestartStatus.Succeeded));
            Assert.That(completed.PreviousGeneration, Is.EqualTo(1));
            Assert.That(completed.ActiveGeneration, Is.EqualTo(2));
            Assert.That(coordinator.Read().IsRestarting, Is.False);
        });
    }

    [Test]
    public async Task FailedReplacementCreatesOneFreshRollbackGeneration()
    {
        int created = 0;
        RestartableModuleGraph Factory(IReadOnlySet<string>? _) => ++created == 2
            ? Graph("broken", () => throw new InvalidOperationException("replacement failed"))
            : Graph($"generation-{created}");
        using var coordinator = new SoftRestartCoordinator(Enabled(), Factory, new NullLogger());

        SoftRestartResult result = await coordinator.RestartAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SoftRestartStatus.RolledBack));
            Assert.That(result.ActiveGeneration, Is.EqualTo(2));
            Assert.That(created, Is.EqualTo(3));
            Assert.That(coordinator.TryAcquireCallback(out IModuleCallbackLease? callback), Is.True);
            callback?.Dispose();
        });
    }

    [Test]
    public async Task FailedReplacementAndRollbackEnterObservableDegradedState()
    {
        int created = 0;
        RestartableModuleGraph Factory(IReadOnlySet<string>? _) => ++created == 1
            ? Graph("initial")
            : Graph($"broken-{created}", () => throw new InvalidOperationException("failed"));
        using var coordinator = new SoftRestartCoordinator(Enabled(), Factory, new NullLogger());

        SoftRestartResult result = await coordinator.RestartAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SoftRestartStatus.Degraded));
            Assert.That(coordinator.Read().LastStatus, Is.EqualTo(SoftRestartStatus.Degraded));
            Assert.That(coordinator.TryAcquireCallback(out _), Is.False);
        });
    }

    [Test]
    public async Task UnknownTargetAndPluginGuardDoNotQuiesceActiveGeneration()
    {
        int created = 0;
        using var coordinator = new SoftRestartCoordinator(
            Enabled(),
            _ => Graph($"generation-{++created}"),
            new NullLogger());

        SoftRestartResult missing = await coordinator.RestartAsync(new HashSet<string> { "missing" });
        coordinator.SetProcessRestartGuard(() => "plugin cleanup requires process restart");
        SoftRestartResult guarded = await coordinator.RestartAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(missing.Status, Is.EqualTo(SoftRestartStatus.ModuleNotFound));
            Assert.That(guarded.Status, Is.EqualTo(SoftRestartStatus.ProcessRestartRequired));
            Assert.That(coordinator.Read().ActiveGeneration, Is.EqualTo(1));
            Assert.That(coordinator.TryAcquireCallback(out IModuleCallbackLease? callback), Is.True);
            callback?.Dispose();
        });
    }

    [Test]
    public async Task CancellationWhileDrainingKeepsTheOriginalGenerationActive()
    {
        int created = 0;
        var hosts = new List<FakeHost>();
        RestartableModuleGraph Factory(IReadOnlySet<string>? _)
        {
            var host = new FakeHost();
            hosts.Add(host);
            created++;
            return new RestartableModuleGraph(host, ["module"], () => { });
        }

        using var coordinator = new SoftRestartCoordinator(Enabled(), Factory, new NullLogger());
        Assert.That(coordinator.TryAcquireCallback(out IModuleCallbackLease? callback), Is.True);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        SoftRestartResult result = await coordinator.RestartAsync(null, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SoftRestartStatus.RolledBack));
            Assert.That(result.ActiveGeneration, Is.EqualTo(1));
            Assert.That(created, Is.EqualTo(2));
            Assert.That(hosts[0].Disposed, Is.False);
            Assert.That(hosts[1].Disposed, Is.True);
            Assert.That(coordinator.TryAcquireCallback(out IModuleCallbackLease? next), Is.True);
            next?.Dispose();
        });
        callback?.Dispose();
    }

    [Test]
    public async Task OneHundredRestartsLeaveOnlyTheActiveGenerationOwned()
    {
        var hosts = new List<FakeHost>();
        RestartableModuleGraph Factory(IReadOnlySet<string>? _)
        {
            var host = new FakeHost();
            hosts.Add(host);
            return new RestartableModuleGraph(host, ["module"], () => { });
        }
        using var coordinator = new SoftRestartCoordinator(Enabled(), Factory, new NullLogger());

        for (int index = 0; index < 100; index++)
        {
            Assert.That((await coordinator.RestartAsync(null)).Status, Is.EqualTo(SoftRestartStatus.Succeeded));
        }

        Assert.That(hosts.Count(static host => !host.Disposed), Is.EqualTo(1));
    }

    private static SoftRestartOptions Enabled() => new(true, 2, 2, 10);

    [Test]
    public async Task TimedOutStartIsRetiredOnlyAfterItExitsAndBlocksRollback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeHost(() => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); });
        int created = 0;
        RestartableModuleGraph Factory(IReadOnlySet<string>? _) =>
            new(++created == 1 ? new FakeHost() : slow, ["module"], () => { });
        using var coordinator = new SoftRestartCoordinator(Enabled() with { ModuleTimeoutSeconds = 1 }, Factory, new NullLogger());
        try
        {
            var restart = coordinator.RestartAsync(null).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That((await restart).Status, Is.EqualTo(SoftRestartStatus.Degraded));
            Assert.That(slow.Disposed, Is.False);
            Assert.That((await coordinator.RestartAsync(null)).Status, Is.EqualTo(SoftRestartStatus.ProcessRestartRequired));
            Assert.That(created, Is.EqualTo(2));
        }
        finally { release.TrySetResult(); }
        Assert.That(() => slow.Disposed, Is.True.After(3000, 50));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task FailedDisposalClosesTheGateWithoutStartingAnotherGeneration(bool previousFails)
    {
        int created = 0, previousDisposals = 0, replacementDisposals = 0;
        var previous = new FakeHost(stop: () =>
        {
            previousDisposals++;
            if (previousFails) throw new IOException("previous cleanup");
        });
        var replacement = new FakeHost(() => throw new InvalidOperationException("start"), () =>
        {
            replacementDisposals++;
            if (!previousFails) throw new IOException("replacement cleanup");
        });
        RestartableModuleGraph Factory(IReadOnlySet<string>? _) => new(++created == 1 ? previous : replacement, ["module"], () => { });
        using var coordinator = new SoftRestartCoordinator(Enabled(), Factory, new NullLogger());
        Assert.That((await coordinator.RestartAsync(null)).Status, Is.EqualTo(SoftRestartStatus.Degraded));
        Assert.That(coordinator.TryAcquireCallback(out _), Is.False);
        Assert.That((await coordinator.RestartAsync(null)).Status, Is.EqualTo(SoftRestartStatus.ProcessRestartRequired));
        coordinator.Dispose();
        Assert.That((created, previousDisposals, replacementDisposals), Is.EqualTo((2, 1, 1)));
    }

    private static RestartableModuleGraph Graph(string name, Action? start = null) =>
        new(new FakeHost(start), [name], () => { });

    private sealed class FakeHost(Action? start = null, Action? stop = null) : IVegaModuleHost
    {
        public bool Disposed { get; private set; }
        public void Start() => start?.Invoke();
        public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => [];
        public void Dispose() { Disposed = true; stop?.Invoke(); }
    }

    private sealed class NullLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null,
            string? correlationId = null)
        {
        }
    }
}
