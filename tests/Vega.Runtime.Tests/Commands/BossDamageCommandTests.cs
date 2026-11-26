using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Modules.Administration;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class BossDamageCommandTests
{
    [Test]
    public async Task HistoryUsesRecordedNamesAndPaginatesActualNativeCredits()
    {
        var world = new WorldId("arena");
        var nativeEntries = Enumerable.Range(0, 9).Select(index =>
            new BossDamageContribution($"Historical player {index}", $"Historical player {index}", index * 100, index, 10)).ToArray();
        var history = new History(new(world, [new("Boss encounter", 120, nativeEntries)]));
        var command = new BossDamageCommand(history);
        var result = await command.ExecuteAsync(Invocation(world, []), CancellationToken.None);
        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(history.RequestedWorld, Is.EqualTo(world));
        Assert.That(result.Messages!.Count(m => m.Key == "bossdamage.credit"), Is.EqualTo(8));
        Assert.That(result.Messages!.First(m => m.Key == "bossdamage.credit").Arguments,
            Is.EqualTo(new object[] { "Historical player 0", 0, 0, 10 }));
        var second = await command.ExecuteAsync(Invocation(world, ["2"]), CancellationToken.None);
        Assert.That(second.Messages!.Single(m => m.Key == "bossdamage.credit").Arguments,
            Is.EqualTo(new object[] { "Historical player 8", 800, 8, 10 }));
        Assert.That((await command.ExecuteAsync(Invocation(world, ["3"]), CancellationToken.None)).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
    }

    [Test]
    public async Task WorldMismatchAndMissingDataAreNotFabricated()
    {
        var world = new WorldId("arena");
        var command = new BossDamageCommand(new History(new(world, [])));
        var missing = await command.ExecuteAsync(Invocation(world, []), CancellationToken.None);
        Assert.That(missing.Messages!.Single().Key, Is.EqualTo("bossdamage.empty"));
        Assert.That((await command.ExecuteAsync(Invocation(new("other"), []), CancellationToken.None)).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        var native = new TerrariaBossDamageHistory(new NeverDispatch(), world);
        Assert.That(await native.RecentAsync(new("other")), Is.Null);
    }

    private static CommandInvocation Invocation(WorldId world, string[] args) =>
        new("bossdamage", args, new Principal(), new Output()) { WorldId = world };

    private sealed class History(BossDamageHistorySnapshot snapshot) : IBossDamageHistory
    {
        public WorldId? RequestedWorld { get; private set; }
        public ValueTask<BossDamageHistorySnapshot?> RecentAsync(WorldId worldId, CancellationToken cancellationToken = default)
        {
            RequestedWorld = worldId;
            return ValueTask.FromResult<BossDamageHistorySnapshot?>(snapshot);
        }
    }

    private sealed class NeverDispatch : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => throw new AssertionException("Wrong world must be rejected before dispatch.");
        public bool IsGameThread => throw new NotSupportedException();
        public void AssertGameThread() => throw new NotSupportedException();
        public bool TryPost(string name, Action action) => throw new NotSupportedException();
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
    }

    private sealed class Principal : ICommandPrincipal
    {
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
