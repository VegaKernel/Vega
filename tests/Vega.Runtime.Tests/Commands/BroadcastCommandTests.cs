using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class BroadcastCommandTests
{
    [Test]
    public async Task BroadcastFansOutJoinedMessageToEveryOnlinePlayer()
    {
        PlayerSnapshot alice = Player(3, "Alice");
        PlayerSnapshot bob = Player(8, "Bob");
        var players = new RecordingPlayers(alice, bob);
        var messenger = new RecordingMessenger();
        var command = new BroadcastCommand(players, messenger);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["maintenance", "in", "five", "minutes"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(messenger.Messages, Has.Count.EqualTo(2));
            Assert.That(messenger.Messages, Does.Contain((alice.Id, "maintenance in five minutes")));
            Assert.That(messenger.Messages, Does.Contain((bob.Id, "maintenance in five minutes")));
        });
    }

    [Test]
    public async Task BroadcastWithoutMessageReturnsInvalidUsage()
    {
        var players = new RecordingPlayers();
        var messenger = new RecordingMessenger();
        var command = new BroadcastCommand(players, messenger);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation([]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(result.ErrorCode, Is.EqualTo("command.broadcast.usage"));
            Assert.That(messenger.Messages, Is.Empty);
        });
    }

    private static CommandInvocation Invocation(IReadOnlyList<string> arguments) =>
        new("broadcast", arguments, ServerPrincipal.Instance, NullOutput.Instance)
        {
            WorldId = WorldId.Primary
        };

    private static PlayerSnapshot Player(int slot, string name) =>
        new(
            new PlayerId(slot),
            name,
            IsDead: false,
            Life: 100,
            MaxLife: 100,
            Mana: 20,
            MaxMana: 20,
            new PlayerPosition(slot * 16, slot * 16),
            IsHostile: false,
            Team: 0);

    private sealed class RecordingPlayers(params PlayerSnapshot[] players) : IPlayerDirectory
    {
        private readonly PlayerSnapshot[] online = players;

        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => online;

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = online.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = online.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class RecordingMessenger : IPlayerMessenger
    {
        public List<(PlayerId PlayerId, string Message)> Messages { get; } = [];

        public ValueTask<PlayerOperationResult> SendMessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add((id, message));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();

        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();

        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
