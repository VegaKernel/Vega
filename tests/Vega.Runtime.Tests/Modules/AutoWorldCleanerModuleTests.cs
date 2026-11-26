using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Modules.AutoWorldCleaner;
using Vega.Operations.Logging;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class AutoWorldCleanerModuleTests
{
    [Test]
    public async Task RunUsesConfiguredItemAgeAndProjectileExclusions()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        using AutoWorldCleanerModule cleaner = CreateCleaner(
            entities,
            onlinePlayers: 1,
            new AutoWorldCleanerOptions(
                true,
                30,
                true,
                new AutoWorldCleanerItemOptions(true, 3, 5),
                new AutoWorldCleanerProjectileOptions(true, 2, true, true)));
        cleaner.Start();

        AutoWorldCleanerRunResult result = await cleaner.RunNowAsync(WorldId.Primary);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AutoWorldCleanerRunStatus.Applied));
            Assert.That(result.RemovedItems, Is.EqualTo(2));
            Assert.That(result.RemovedProjectiles, Is.EqualTo(2));
            Assert.That(entities.LastRemoval, Is.Not.Null);
            Assert.That(
                entities.LastRemoval!.Items.Select(static item => item.Slot),
                Is.EqualTo(new[] { 1, 2 }));
            Assert.That(
                entities.LastRemoval.Projectiles.Select(static projectile => projectile.Slot),
                Is.EqualTo(new[] { 0, 3 }));
            Assert.That(cleaner.Read().State, Is.EqualTo(WorldCleanupRunState.Succeeded));
        });
    }

    [Test]
    public async Task WorldNotReadySkipsWithoutReadingTerrariaState()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        using AutoWorldCleanerModule cleaner = CreateCleaner(
            entities,
            onlinePlayers: 1,
            AutoWorldCleanerOptions.Default with { Enabled = true },
            lifecycleStage: RuntimeLifecycleStage.WorldLoading);
        cleaner.Start();

        AutoWorldCleanerRunResult result = await cleaner.RunNowAsync(WorldId.Primary);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AutoWorldCleanerRunStatus.SkippedNotReady));
            Assert.That(entities.SnapshotCalls, Is.Zero);
            Assert.That(cleaner.Read().State, Is.EqualTo(WorldCleanupRunState.Skipped));
            Assert.That(cleaner.Read().Detail, Is.EqualTo("world-not-ready"));
        });
    }

    [Test]
    public async Task OnlineOnlyConfigurationSkipsWithoutReadingTerrariaState()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        using AutoWorldCleanerModule cleaner = CreateCleaner(
            entities,
            onlinePlayers: 0,
            AutoWorldCleanerOptions.Default with { Enabled = true });
        cleaner.Start();

        AutoWorldCleanerRunResult result = await cleaner.RunNowAsync(WorldId.Primary);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AutoWorldCleanerRunStatus.SkippedNoPlayers));
            Assert.That(entities.SnapshotCalls, Is.Zero);
            Assert.That(cleaner.Read().State, Is.EqualTo(WorldCleanupRunState.Skipped));
        });
    }

    [Test]
    public async Task DisabledConfigurationBlocksManualCleanup()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        using AutoWorldCleanerModule cleaner = CreateCleaner(
            entities,
            onlinePlayers: 1,
            AutoWorldCleanerOptions.Default);
        cleaner.Start();

        AutoWorldCleanerRunResult result = await cleaner.RunNowAsync(WorldId.Primary);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AutoWorldCleanerRunStatus.Disabled));
            Assert.That(entities.SnapshotCalls, Is.Zero);
            Assert.That(cleaner.Read().State, Is.EqualTo(WorldCleanupRunState.Disabled));
        });
    }

    [Test]
    public async Task DispatcherUnavailableFailsWithoutTouchingWorldState()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        var dispatcher = new InlineDispatcher { IsKnown = false };
        using AutoWorldCleanerModule cleaner = CreateCleaner(
            entities,
            onlinePlayers: 1,
            AutoWorldCleanerOptions.Default with { Enabled = true },
            dispatcher);
        cleaner.Start();

        AutoWorldCleanerRunResult result = await cleaner.RunNowAsync(WorldId.Primary);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(AutoWorldCleanerRunStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("auto_world_cleaner.dispatcher_unavailable"));
            Assert.That(entities.SnapshotCalls, Is.Zero);
            Assert.That(cleaner.Read().State, Is.EqualTo(WorldCleanupRunState.Failed));
        });
    }

    [Test]
    public async Task QueuedCleanupCancellationCompletesWithoutWaitingForNativeTick()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        var dispatcher = new QueuedDispatcher();
        using AutoWorldCleanerModule cleaner = CreateCleaner(entities, 1,
            AutoWorldCleanerOptions.Default with { Enabled = true }, dispatcher);
        cleaner.Start();
        using var cancellation = new CancellationTokenSource();
        Task<AutoWorldCleanerRunResult> pending = cleaner.RunNowAsync(WorldId.Primary, cancellation.Token).AsTask();
        Assert.That(dispatcher.HasWork, Is.True);
        cancellation.Cancel();
        AutoWorldCleanerRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(result.Status, Is.EqualTo(AutoWorldCleanerRunStatus.Cancelled));
        // A later native tick may still consume the canceled reservation; it must be a no-op.
        dispatcher.Execute();
        Assert.That(entities.SnapshotCalls, Is.Zero);
    }

    [Test]
    public async Task CancellationAfterCleanupStartsWaitsForActualMutationResult()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        var dispatcher = new QueuedDispatcher();
        using AutoWorldCleanerModule cleaner = CreateCleaner(entities, 1,
            AutoWorldCleanerOptions.Default with { Enabled = true }, dispatcher);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entities.BeforeSnapshot = () =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test cleanup was not released.");
        };
        cleaner.Start();
        using var cancellation = new CancellationTokenSource();
        Task<AutoWorldCleanerRunResult> pending = cleaner.RunNowAsync(WorldId.Primary, cancellation.Token).AsTask();
        Task executing = Task.Run(dispatcher.Execute);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.That(pending.IsCompleted, Is.False);
            Assert.That((await cleaner.RunNowAsync(WorldId.Primary)).Status, Is.EqualTo(AutoWorldCleanerRunStatus.Busy));
        }
        finally { release.Set(); }
        await executing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status, Is.EqualTo(AutoWorldCleanerRunStatus.Applied));
    }

    [Test]
    public async Task CommandRunsCleanerAndExposesStatus()
    {
        var entities = new RecordingWorldEntities(CreateSnapshot());
        using AutoWorldCleanerModule cleaner = CreateCleaner(
            entities,
            onlinePlayers: 1,
            new AutoWorldCleanerOptions(
                true,
                30,
                false,
                new AutoWorldCleanerItemOptions(true, 3, 5),
                new AutoWorldCleanerProjectileOptions(false, 2, true, true)));
        cleaner.Start();
        var command = new AutoWorldCleanerCommand(cleaner);
        var invocation = new CommandInvocation(
            "worldclean",
            Array.Empty<string>(),
            new ServerPrincipal(),
            new NullOutput());

        CommandExecutionResult run = await command.ExecuteAsync(invocation, CancellationToken.None);
        CommandExecutionResult status = await command.ExecuteAsync(
            invocation with { Arguments = new[] { "status" } },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(status.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(command.Descriptor.Aliases, Does.Contain("cleanworld"));
            Assert.That(command.Descriptor.Permissions, Does.Contain("vega.cfg.maintenance"));
        });
    }

    private static AutoWorldCleanerModule CreateCleaner(
        RecordingWorldEntities entities,
        int onlinePlayers,
        AutoWorldCleanerOptions options,
        IGameThreadDispatcher? dispatcher = null,
        RuntimeLifecycleStage lifecycleStage = RuntimeLifecycleStage.WorldReady)
    {
        var logger = new StructuredOperationsLogger([new BoundedOperationsLogStore()]);
        return new AutoWorldCleanerModule(
            options,
            entities,
            new FixedLifecycle(lifecycleStage),
            dispatcher ?? new InlineDispatcher(),
            new TestPlayers(onlinePlayers),
            logger);
    }

    private static WorldEntityCleanupSnapshot CreateSnapshot() => new(
        WorldId.Primary,
        [
            new WorldItemCleanupSnapshot(new WorldItemCleanupHandle(0, 299)),
            new WorldItemCleanupSnapshot(new WorldItemCleanupHandle(1, 300)),
            new WorldItemCleanupSnapshot(new WorldItemCleanupHandle(2, 900))
        ],
        [
            new WorldProjectileCleanupSnapshot(new WorldProjectileCleanupHandle(0, 100), false, false),
            new WorldProjectileCleanupSnapshot(new WorldProjectileCleanupHandle(1, 101), true, false),
            new WorldProjectileCleanupSnapshot(new WorldProjectileCleanupHandle(2, 102), false, true),
            new WorldProjectileCleanupSnapshot(new WorldProjectileCleanupHandle(3, 103), false, false)
        ],
        DateTimeOffset.Parse("2026-08-21T12:00:00Z"));

    private sealed class RecordingWorldEntities(WorldEntityCleanupSnapshot snapshot) : IWorldEntityMaintenance
    {
        public int SnapshotCalls { get; private set; }
        public WorldEntityRemovalRequest? LastRemoval { get; private set; }
        public Action? BeforeSnapshot { get; set; }

        public WorldEntityCleanupSnapshot Snapshot(WorldId worldId)
        {
            BeforeSnapshot?.Invoke();
            SnapshotCalls++;
            Assert.That(worldId, Is.EqualTo(WorldId.Primary));
            return snapshot;
        }

        public WorldEntityRemovalResult Remove(WorldEntityRemovalRequest request)
        {
            LastRemoval = request;
            return new WorldEntityRemovalResult(
                request.Items.Count,
                request.Projectiles.Count,
                0,
                0);
        }
    }

    private sealed class FixedLifecycle(RuntimeLifecycleStage stage) : IRuntimeLifecycle
    {
        public RuntimeLifecycleStage CurrentStage => stage;
        public TimeSpan Elapsed => TimeSpan.Zero;
        public event Action<RuntimeLifecycleTransition>? Transitioned
        {
            add { }
            remove { }
        }
    }

    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        public bool IsKnown { get; init; } = true;
        public bool IsGameThreadKnown => IsKnown;
        public bool IsGameThread => IsKnown;
        public void AssertGameThread()
        {
            if (!IsKnown)
            {
                throw new InvalidOperationException();
            }
        }
        public bool TryPost(string name, Action action)
        {
            if (!IsKnown)
            {
                return false;
            }
            action();
            return true;
        }
        public bool TrySchedule(string name, Func<bool> step)
        {
            if (!IsKnown)
            {
                return false;
            }
            step();
            return true;
        }
    }

    private sealed class QueuedDispatcher : IGameThreadDispatcher
    {
        private Action? work;
        public bool HasWork => Volatile.Read(ref work) is not null;
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => false;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) => Interlocked.CompareExchange(ref work, action, null) is null;
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void Execute() => Interlocked.Exchange(ref work, null)?.Invoke();
    }

    private sealed class TestPlayers(int count) : IPlayerDirectory
    {
        private readonly IReadOnlyList<PlayerSnapshot> players = Enumerable.Range(0, count)
            .Select(index => new PlayerSnapshot(
                new PlayerId(index),
                $"Player{index}",
                false,
                100,
                100,
                20,
                20,
                new PlayerPosition(0, 0),
                false,
                0))
            .ToArray();

        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }
        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class NullOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
