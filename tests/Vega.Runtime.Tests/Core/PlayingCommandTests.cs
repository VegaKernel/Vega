using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Core.Commands;
using Vega.Core.Commands.Players;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PlayingCommandTests
{
    [Test]
    public async Task ListsOnlinePlayerNamesWithoutSlots()
    {
        var command = Command(Player(2, "Alice"), Player(7, "Bob"));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("playing", []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages, Has.Count.EqualTo(2));
            Assert.That(result.Messages![0].Arguments, Is.EqualTo(new object?[] { 2 }));
            Assert.That(result.Messages[1].Arguments, Is.EqualTo(new object?[] { "Alice, Bob" }));
            Assert.That(result.Messages[1].Arguments[0], Does.Not.Contain("Index"));
        });
    }

    [TestCase("playing")]
    [TestCase("online")]
    [TestCase("who")]
    public async Task DispatcherOwnsAllAliasesWithoutBasePermission(string name)
    {
        var dispatcher = new CommandDispatcher([Command(Player(1, "Alice"))]);

        CommandExecutionResult result = await dispatcher.DispatchAsync(
            Invocation(name, [], permissions: []));

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch(name), Is.True);
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        });
    }

    [Test]
    public async Task DisplaysSlotsWithPermission()
    {
        var command = Command(Player(4, "Alice"), Player(12, "Bob"));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("who", ["-I"], permissions: ["vega.admin.seeplayerids"]),
            CancellationToken.None);

        Assert.That(
            result.Messages![1].Arguments,
            Is.EqualTo(new object?[] { "Alice (Index: 4), Bob (Index: 12)" }));
    }

    [Test]
    public async Task RejectsSlotsWithoutPermission()
    {
        var command = Command(Player(4, "Alice"));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("playing", ["-i"], permissions: []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(result.ErrorCode, Is.EqualTo("command.playing.ids-forbidden"));
        });
    }

    [Test]
    public async Task ServerCanDisplaySlotsWithoutPlayerPermissions()
    {
        var command = Command(Player(4, "Alice"));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("playing", ["-i"], isServer: true, permissions: []),
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(result.Messages![1].Arguments, Is.EqualTo(new object?[] { "Alice (Index: 4)" }));
    }

    [Test]
    public async Task ParsesRequestedPageAndClampsPagesPastTheEnd()
    {
        PlayerSnapshot[] players = Enumerable.Range(0, 17)
            .Select(index => Player(index, $"Player-{index:00}-LongName"))
            .ToArray();
        var command = Command(players);

        CommandExecutionResult secondPage = await command.ExecuteAsync(
            Invocation("online", ["2"]),
            CancellationToken.None);
        CommandExecutionResult pastEnd = await command.ExecuteAsync(
            Invocation("online", ["99"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(secondPage.Messages, Has.Count.EqualTo(2));
            Assert.That(secondPage.Messages![1].Arguments[0], Does.Contain("Player-16-LongName"));
            Assert.That(secondPage.Messages[1].Arguments[0], Does.Not.Contain("Player-00-LongName"));
            Assert.That(pastEnd.Messages![1].Arguments, Is.EqualTo(secondPage.Messages[1].Arguments));
        });
    }

    [TestCase("unexpected")]
    [TestCase("0")]
    [TestCase("-1")]
    public async Task RejectsInvalidArgument(string argument)
    {
        var command = Command(Player(1, "Alice"));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("playing", [argument]),
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(result.ErrorCode, Is.EqualTo("command.playing.usage"));
    }

    [TestCase("1", "2")]
    [TestCase("-i", "-i")]
    [TestCase("-i", "1", "extra")]
    public async Task RejectsConflictingOrExcessArguments(params string[] arguments)
    {
        var command = Command(Player(1, "Alice"));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("playing", arguments),
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
    }

    [Test]
    public async Task ReportsEmptyServer()
    {
        var command = Command();

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("playing", []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages, Has.Count.EqualTo(1));
            Assert.That(result.Messages![0].Key, Is.EqualTo("command.playing.none"));
        });
    }

    private static PlayingCommand Command(params PlayerSnapshot[] players) =>
        new(new FakeDirectory(players));

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        bool isServer = false,
        IReadOnlyCollection<string>? permissions = null) =>
        new(name, arguments, new FakePrincipal(isServer, permissions ?? []), new FakeOutput());

    private static PlayerSnapshot Player(int slot, string name) =>
        new(
            new PlayerId(slot),
            name,
            false,
            100,
            100,
            20,
            20,
            new PlayerPosition(0, 0),
            false,
            0);

    private sealed class FakeDirectory(IReadOnlyList<PlayerSnapshot> players) : IPlayerDirectory
    {
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

    private sealed class FakePrincipal(
        bool isServer,
        IReadOnlyCollection<string> permissions) : ICommandPrincipal
    {
        public string Name => isServer ? "Server" : "Player";
        public bool IsServer => isServer;
        public bool HasPermission(string permission) =>
            isServer || permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class FakeOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
