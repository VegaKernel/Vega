using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands.Administration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class KickCommandTests
{
    [Test]
    public async Task KicksQuotedExactNameWithReason()
    {
        var target = Player(4, "Player With Space");
        var operations = new FakeOperations();
        var command = new KickCommand(
            new FakeDirectory([target]),
            operations,
            new FakeAuthorization());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["Player With Space", "griefing", "spawn"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.KickedPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.KickReason, Is.EqualTo("Kicked: griefing spawn"));
            Assert.That(result.Messages![0].DefaultText, Is.EqualTo("Kicked {0} for '{1}'."));
        });
    }

    [Test]
    public async Task UsesLegacyDefaultReason()
    {
        var target = Player(7, "Target");
        var operations = new FakeOperations();
        var command = new KickCommand(
            new FakeDirectory([target]),
            operations,
            new FakeAuthorization());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["Target"]),
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(operations.KickReason, Is.EqualTo("Kicked: Misbehaviour."));
    }

    [Test]
    public async Task RejectsAmbiguousPrefix()
    {
        var operations = new FakeOperations();
        var command = new KickCommand(
            new FakeDirectory([Player(1, "Alice"), Player(2, "Alina")]),
            operations,
            new FakeAuthorization());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["Ali"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("command.kick.multiple-matches"));
            Assert.That(operations.KickedPlayer, Is.Null);
        });
    }

    [Test]
    public async Task PlayerCannotKickImmuneTarget()
    {
        var target = Player(2, "Admin");
        var authorization = new FakeAuthorization(target.Id);
        var operations = new FakeOperations();
        var command = new KickCommand(
            new FakeDirectory([target]),
            operations,
            authorization);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["Admin"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That(result.ErrorCode, Is.EqualTo("command.kick.immune"));
            Assert.That(operations.KickedPlayer, Is.Null);
        });
    }

    [Test]
    public async Task ServerBypassesKickImmunity()
    {
        var target = Player(2, "Admin");
        var operations = new FakeOperations();
        var command = new KickCommand(
            new FakeDirectory([target]),
            operations,
            new FakeAuthorization(target.Id));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["Admin"], isServer: true),
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(operations.KickedPlayer, Is.EqualTo(target.Id));
    }

    [Test]
    public async Task SupportsExplicitSlotSelector()
    {
        var target = Player(12, "Someone");
        var operations = new FakeOperations();
        var command = new KickCommand(
            new FakeDirectory([target]),
            operations,
            new FakeAuthorization());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(["tsi:12"]),
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(operations.KickedPlayer, Is.EqualTo(target.Id));
    }

    [Test]
    public async Task KickPassesExactWorldAndSessionSnapshotRatherThanReusableSlot()
    {
        WorldId world = new("arena");
        var target = Player(4, "Target") with { WorldId = world, SessionId = PlayerSessionId.New() };
        var other = target with { WorldId = WorldId.Primary, SessionId = PlayerSessionId.New() };
        var operations = new FakeOperations();
        var command = new KickCommand(new FakeDirectory([other, target]), operations, new FakeAuthorization());
        CommandExecutionResult result = await command.ExecuteAsync(Invocation(["tsi:4"]) with { WorldId = world }, default);
        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(operations.KickedSnapshot, Is.EqualTo(target));
    }

    private static CommandInvocation Invocation(IReadOnlyList<string> arguments, bool isServer = false) =>
        new("kick", arguments, new FakePrincipal(isServer), new FakeOutput());

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

    private sealed class FakeOperations : IPlayerOperations
    {
        public PlayerId? KickedPlayer { get; private set; }
        public string? KickReason { get; private set; }
        public PlayerSnapshot? KickedSnapshot { get; private set; }

        public ValueTask<PlayerOperationResult> KickAsync(PlayerSnapshot player, string reason, CancellationToken cancellationToken = default)
        {
            KickedSnapshot = player;
            return KickAsync(player.Id, reason, cancellationToken);
        }

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default)
        {
            KickedPlayer = id;
            KickReason = reason;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId id,
            PlayerPosition destination,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class FakeAuthorization(params PlayerId[] immunePlayers) : IPlayerAuthorizationService
    {
        public bool HasPermission(PlayerId playerId, string permission) =>
            permission == "vega.admin.nokick" && immunePlayers.Contains(playerId);
    }

    private sealed class FakePrincipal(bool isServer) : ICommandPrincipal
    {
        public string Name => isServer ? "Server" : "Moderator";
        public bool IsServer => isServer;
        public bool HasPermission(string permission) => true;
    }

    private sealed class FakeOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
