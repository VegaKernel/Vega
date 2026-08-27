using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TerrariaHomeCommandTests
{
    [Test]
    public void DescriptorMatchesPinnedTShockRegistration()
    {
        var command = new HomeCommand(new RecordingPlayers(Player()), new RecordingRecall());

        Assert.Multiple(() =>
        {
            Assert.That(command.Descriptor.Name, Is.EqualTo("home"));
            Assert.That(command.Descriptor.Aliases, Is.Empty);
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.players.home" }));
            Assert.That(command.Descriptor.AllowServer, Is.False);
        });
    }

    [Test]
    public async Task RecallsInvokingPlayerAndIgnoresArgumentsLikeTShock()
    {
        PlayerSnapshot player = Player();
        var recall = new RecordingRecall();
        var command = new HomeCommand(new RecordingPlayers(player), recall);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(player, ["ignored", "arguments"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(recall.LastPlayerId, Is.EqualTo(player.Id));
            Assert.That(result.Messages?.Single().DefaultText, Does.Contain("personal spawn point"));
        });
    }

    [Test]
    public async Task ReportsDeadPlayerFailureFromAuthoritativeRecallService()
    {
        PlayerSnapshot player = Player();
        var recall = new RecordingRecall(PlayerOperationResult.Failed("player.recall-home.dead"));
        var command = new HomeCommand(new RecordingPlayers(player), recall);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(player, []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("player.recall-home.dead"));
            Assert.That(result.Messages?.Single().DefaultText, Does.Contain("Dead players"));
        });
    }

    [Test]
    public async Task RejectsInvocationWithoutAnActivePlayer()
    {
        PlayerSnapshot player = Player();
        var recall = new RecordingRecall();
        var command = new HomeCommand(new RecordingPlayers(), recall);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(player, []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(recall.LastPlayerId, Is.Null);
        });
    }

    [Test]
    public async Task SpawnRecallsInvokingPlayerToWorldSpawnAndIgnoresArguments()
    {
        PlayerSnapshot player = Player();
        var recall = new RecordingRecall();
        var command = new SpawnCommand(new RecordingPlayers(player), recall);

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation(
                "spawn",
                ["ignored"],
                new PlayerPrincipal(player.Id, player.Name),
                NullOutput.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(recall.LastWorldSpawnPlayerId, Is.EqualTo(player.Id));
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.players.spawn" }));
            Assert.That(command.Descriptor.AllowServer, Is.False);
        });
    }

    private static CommandInvocation Invocation(PlayerSnapshot player, IReadOnlyList<string> arguments) =>
        new(
            "home",
            arguments,
            new PlayerPrincipal(player.Id, player.Name),
            NullOutput.Instance);

    private static PlayerSnapshot Player() => new(
        new PlayerId(3),
        "Alice",
        IsDead: false,
        Life: 100,
        MaxLife: 100,
        Mana: 20,
        MaxMana: 20,
        new PlayerPosition(100, 200),
        IsHostile: false,
        Team: 0);

    private sealed class RecordingRecall(
        PlayerOperationResult? result = null) : IPlayerRecallOperations
    {
        public PlayerId? LastPlayerId { get; private set; }
        public PlayerId? LastWorldSpawnPlayerId { get; private set; }

        public ValueTask<PlayerOperationResult> RecallHomeAsync(
            PlayerId id,
            CancellationToken cancellationToken = default)
        {
            LastPlayerId = id;
            return ValueTask.FromResult(result ?? PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> RecallWorldSpawnAsync(
            PlayerId id,
            CancellationToken cancellationToken = default)
        {
            LastWorldSpawnPlayerId = id;
            return ValueTask.FromResult(result ?? PlayerOperationResult.Applied());
        }
    }

    private sealed class RecordingPlayers(params PlayerSnapshot[] players) : IPlayerDirectory
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
                candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class PlayerPrincipal(PlayerId playerId, string name) : IPlayerCommandPrincipal
    {
        public PlayerId PlayerId { get; } = playerId;
        public string PlayerName => name;
        public string? AccountName => null;
        public string Name => name;
        public bool IsServer => false;
        public string CultureSubjectId => $"player:{PlayerId.Slot}";
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
