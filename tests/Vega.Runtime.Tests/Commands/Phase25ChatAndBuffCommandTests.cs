using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Moderation;
using Vega.Contracts.Players;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class Phase25ChatAndBuffCommandTests
{
    [Test]
    public async Task GBuffDefaultsToSixtySecondsAndTargetsResolvedPlayer()
    {
        PlayerSnapshot target = Player(3, "Alice", team: 1);
        var operations = new RecordingLifecycleOperations();
        var command = new GBuffCommand(new RecordingPlayers(target), operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("gbuff", ["Alice", "1"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(command.Descriptor.Aliases, Is.EqualTo(new[] { "buffplayer" }));
            Assert.That(operations.BuffedPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.BuffType, Is.EqualTo(1));
            Assert.That(operations.BuffDurationTicks, Is.EqualTo(60 * 60));
        });
    }

    [Test]
    public async Task ThirdPersonBroadcastsTShockStyleTextToEveryOnlinePlayer()
    {
        PlayerSnapshot alice = Player(1, "Alice", team: 1);
        PlayerSnapshot bob = Player(2, "Bob", team: 2);
        var messenger = new RecordingMessenger();
        var command = new ThirdPersonCommand(
            new RecordingPlayers(alice, bob),
            messenger,
            new RecordingMuteService());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("me", ["waves", "hello"], alice),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(messenger.Messages.Select(static entry => entry.Id),
                Is.EquivalentTo(new[] { alice.Id, bob.Id }));
            Assert.That(messenger.Messages.All(static entry => entry.Message == "*Alice waves hello"), Is.True);
        });
    }

    [Test]
    public async Task PartyChatDeliversOnlyToPlayersOnTheSameTeam()
    {
        PlayerSnapshot alice = Player(1, "Alice", team: 1);
        PlayerSnapshot bob = Player(2, "Bob", team: 1);
        PlayerSnapshot charlie = Player(3, "Charlie", team: 2);
        var messenger = new RecordingMessenger();
        var command = new PartyChatCommand(
            new RecordingPlayers(alice, bob, charlie),
            messenger,
            new RecordingMuteService());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("party", ["hold", "here"], alice),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(messenger.Messages.Select(static entry => entry.Id),
                Is.EquivalentTo(new[] { alice.Id, bob.Id }));
            Assert.That(messenger.Messages.All(static entry => entry.Message == "<Alice> hold here"), Is.True);
            Assert.That(messenger.Messages.Any(entry => entry.Id == charlie.Id), Is.False);
        });
    }

    [Test]
    public async Task SocialChatRejectsMutedPlayerBeforeDelivery()
    {
        PlayerSnapshot alice = Player(1, "Alice", team: 1);
        var messenger = new RecordingMessenger();
        var mutes = new RecordingMuteService(muted: true);

        CommandExecutionResult me = await new ThirdPersonCommand(
                new RecordingPlayers(alice), messenger, mutes)
            .ExecuteAsync(Invocation("me", ["waves"], alice), CancellationToken.None);
        CommandExecutionResult party = await new PartyChatCommand(
                new RecordingPlayers(alice), messenger, mutes)
            .ExecuteAsync(Invocation("party", ["hello"], alice), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(me.ErrorCode, Is.EqualTo("command.me.muted"));
            Assert.That(party.ErrorCode, Is.EqualTo("command.party.muted"));
            Assert.That(messenger.Messages, Is.Empty);
        });
    }

    private static CommandInvocation Invocation(string name, IReadOnlyList<string> arguments) =>
        new(name, arguments, new ServerPrincipal(), new NullOutput());

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        PlayerSnapshot player) =>
        new(name, arguments, new PlayerPrincipal(player.Id, player.Name), new NullOutput());

    private static PlayerSnapshot Player(int slot, string name, int team) =>
        new(
            new PlayerId(slot),
            name,
            IsDead: false,
            Life: 100,
            MaxLife: 100,
            Mana: 20,
            MaxMana: 20,
            new PlayerPosition(100, 100),
            IsHostile: false,
            Team: team)
        {
            SessionId = PlayerSessionId.New()
        };

    private sealed class RecordingPlayers(params PlayerSnapshot[] players) : IPlayerDirectory
    {
        private readonly PlayerSnapshot[] snapshot = players;

        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => snapshot;

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = snapshot.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = snapshot.FirstOrDefault(candidate =>
                candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class RecordingMessenger : IPlayerMessenger
    {
        public List<(PlayerId Id, string Message)> Messages { get; } = [];

        public ValueTask<PlayerOperationResult> SendMessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add((id, message));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class RecordingLifecycleOperations : IPlayerLifecycleOperations
    {
        public PlayerId? BuffedPlayer { get; private set; }
        public int? BuffType { get; private set; }
        public int? BuffDurationTicks { get; private set; }

        public ValueTask<PlayerOperationResult> ApplyBuffAsync(
            PlayerId id,
            int buffType,
            int durationTicks,
            CancellationToken cancellationToken = default)
        {
            BuffedPlayer = id;
            BuffType = buffType;
            BuffDurationTicks = durationTicks;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> HealAsync(PlayerId id, int? amount = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> KillAsync(PlayerId id, string reason, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> RespawnAsync(PlayerId id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class RecordingMuteService(bool muted = false) : IMuteService
    {
        public IReadOnlyList<MuteSnapshot> SnapshotActive(DateTimeOffset now) => [];

        public MuteSnapshot? FindActive(string playerName, string? accountName, DateTimeOffset now) =>
            muted
                ? new MuteSnapshot(
                    MuteId.New(),
                    MuteSubjectKind.PlayerName,
                    playerName,
                    "test",
                    "test",
                    now,
                    now.AddMinutes(1),
                    1)
                : null;

        public ValueTask<MuteMutationResult> CreateAsync(MuteSnapshot mute, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MuteMutationResult(true, "ok", After: mute));

        public ValueTask<MuteMutationResult> RevokeAsync(
            MuteId muteId,
            long expectedRevision,
            string actor,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MuteMutationResult(true, "ok"));
    }

    private sealed class PlayerPrincipal(PlayerId playerId, string name) : IPlayerCommandPrincipal
    {
        public PlayerId PlayerId { get; } = playerId;
        public string PlayerName { get; } = name;
        public string? AccountName => null;
        public string CultureSubjectId => $"player:{PlayerId.Slot}";
        public string Name { get; } = name;
        public bool IsServer => false;
        public bool HasPermission(string permission) => true;
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
