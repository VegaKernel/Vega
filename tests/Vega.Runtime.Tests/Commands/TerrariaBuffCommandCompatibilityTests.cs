using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TerrariaBuffCommandCompatibilityTests
{
    [Test]
    public async Task BuffUsesCallerAndDefaultsToSixtySeconds()
    {
        PlayerSnapshot player = Player(3, "Alice");
        var directory = new RecordingDirectory(player);
        var operations = new RecordingLifecycleOperations();
        var command = new BuffCommand(directory, operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("buff", ["1"], new PlayerPrincipal(player.Id, player.Name)),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.BuffedPlayer, Is.EqualTo(player.Id));
            Assert.That(operations.BuffType, Is.EqualTo(1));
            Assert.That(operations.BuffDurationTicks, Is.EqualTo(60 * 60));
            Assert.That(command.Descriptor.AllowServer, Is.False);
            Assert.That(command.Descriptor.Syntax, Is.EqualTo("buff <buff-name|id> [seconds]"));
        });
    }

    [Test]
    public async Task BuffRejectsServerConsoleInsteadOfAcceptingTargetArgument()
    {
        PlayerSnapshot player = Player(3, "Alice");
        var command = new BuffCommand(new RecordingDirectory(player), new RecordingLifecycleOperations());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("buff", ["1", "30", "Alice"], ServerPrincipal.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(result.ErrorCode, Is.EqualTo("command.buff.usage"));
        });
    }

    [Test]
    public async Task GBuffPreservesTargetFirstShapeAliasAndTerraZDurationClamp()
    {
        PlayerSnapshot player = Player(3, "Alice");
        var operations = new RecordingLifecycleOperations();
        var command = new GBuffCommand(new RecordingDirectory(player), operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("buffplayer", ["Alice", "1", "-1"], ServerPrincipal.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.BuffedPlayer, Is.EqualTo(player.Id));
            Assert.That(operations.BuffType, Is.EqualTo(1));
            Assert.That(
                operations.BuffDurationTicks,
                Is.EqualTo(BuffCommandParsing.MaximumSeconds * 60));
            Assert.That(command.Descriptor.Aliases, Is.EqualTo(new[] { "buffplayer" }));
            Assert.That(command.Descriptor.AllowServer, Is.True);
        });
    }

    [TestCase("0")]
    [TestCase("not-a-number")]
    public async Task GBuffRejectsDurationThatCannotReachLifecycleBoundary(string duration)
    {
        PlayerSnapshot player = Player(3, "Alice");
        var operations = new RecordingLifecycleOperations();
        var command = new GBuffCommand(new RecordingDirectory(player), operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("gbuff", ["Alice", "1", duration], ServerPrincipal.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(result.ErrorCode, Is.EqualTo("command.gbuff.usage"));
            Assert.That(operations.BuffedPlayer, Is.Null);
        });
    }

    private static PlayerSnapshot Player(int slot, string name) =>
        new(
            new PlayerId(slot),
            name,
            IsDead: false,
            Life: 100,
            MaxLife: 100,
            Mana: 20,
            MaxMana: 20,
            new PlayerPosition(100, 200),
            IsHostile: false,
            Team: 0);

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        ICommandPrincipal principal) =>
        new(name, arguments, principal, NullOutput.Instance) { WorldId = WorldId.Primary };

    private sealed class RecordingDirectory(params PlayerSnapshot[] players) : IPlayerDirectory
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

    private sealed class RecordingLifecycleOperations : IPlayerLifecycleOperations
    {
        public PlayerId? BuffedPlayer { get; private set; }
        public int? BuffType { get; private set; }
        public int? BuffDurationTicks { get; private set; }

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId id,
            PlayerPosition destination,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> HealAsync(
            PlayerId id,
            int? amount = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> KillAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

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

        public ValueTask<PlayerOperationResult> RespawnAsync(
            PlayerId id,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
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
