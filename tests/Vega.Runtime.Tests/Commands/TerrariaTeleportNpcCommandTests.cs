using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TerrariaTeleportNpcCommandTests
{
    [Test]
    public void DescriptorMatchesPinnedTShockRegistration()
    {
        TeleportNpcCommand command = CreateCommand(Player(), []);

        Assert.Multiple(() =>
        {
            Assert.That(command.Descriptor.Name, Is.EqualTo("tpnpc"));
            Assert.That(command.Descriptor.Aliases, Is.Empty);
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.players.teleport.npc" }));
            Assert.That(command.Descriptor.AllowServer, Is.False);
        });
    }

    [Test]
    public async Task JoinsArgumentsAndTeleportsCallerToExactNpc()
    {
        PlayerSnapshot player = Player();
        NpcSnapshot npc = Npc(4, "Traveling Merchant");
        var operations = new RecordingOperations();
        TeleportNpcCommand command = CreateCommand(player, [npc], operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(player, ["Traveling", "Merchant"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.PlayerId, Is.EqualTo(player.Id));
            Assert.That(operations.Destination, Is.EqualTo(npc.Position));
        });
    }

    [Test]
    public async Task RejectsAmbiguousNpcPrefix()
    {
        PlayerSnapshot player = Player();
        TeleportNpcCommand command = CreateCommand(
            player,
            [Npc(4, "Blue Slime"), Npc(5, "Blue Cultist Archer")]);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(player, ["Blue"]),
            CancellationToken.None);

        Assert.That(result.ErrorCode, Is.EqualTo("command.tpnpc.ambiguous"));
    }

    private static TeleportNpcCommand CreateCommand(
        PlayerSnapshot player,
        IReadOnlyList<NpcSnapshot> npcs,
        RecordingOperations? operations = null) =>
        new(new PlayerDirectory(player), operations ?? new RecordingOperations(), new NpcService(npcs));

    private static CommandInvocation Invocation(PlayerSnapshot player, IReadOnlyList<string> arguments) =>
        new("tpnpc", arguments, new PlayerPrincipal(player.Id, player.Name), NullOutput.Instance);

    private static PlayerSnapshot Player() => new(
        new PlayerId(3), "Alice", false, 100, 100, 20, 20,
        new PlayerPosition(100, 200), false, 0);

    private static NpcSnapshot Npc(int slot, string name) => new(
        new NpcHandle(WorldId.Primary, slot, 100 + slot, slot),
        100 + slot,
        name,
        new PlayerPosition(slot * 100, slot * 200),
        new WorldVelocity(0, 0),
        100,
        100,
        false,
        false,
        false,
        -1,
        DateTimeOffset.UtcNow);

    private sealed class PlayerDirectory(PlayerSnapshot player) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? value)
        {
            value = id == player.Id ? player : null;
            return value is not null;
        }
        public bool TryFindExact(string name, out PlayerSnapshot? value)
        {
            value = player.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? player : null;
            return value is not null;
        }
    }

    private sealed class RecordingOperations : IPlayerOperations
    {
        public PlayerId? PlayerId { get; private set; }
        public PlayerPosition? Destination { get; private set; }
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken cancellationToken = default)
        {
            PlayerId = id;
            Destination = destination;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class NpcService(IReadOnlyList<NpcSnapshot> npcs) : IWorldNpcService
    {
        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(WorldId worldId, CancellationToken cancellationToken = default) => ValueTask.FromResult(npcs);
        public ValueTask<NpcSnapshot?> CaptureAsync(NpcHandle handle, CancellationToken cancellationToken = default) => ValueTask.FromResult<NpcSnapshot?>(null);
        public ValueTask<NpcOperationResult> SpawnAsync(NpcSpawnRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Failed));
        public ValueTask<NpcOperationResult> TeleportAsync(NpcTeleportRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Failed));
        public ValueTask<NpcOperationResult> DespawnAsync(NpcDespawnRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Failed));
        public ValueTask<NpcOperationResult> KillAsync(NpcKillRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Failed));
        public ValueTask<NpcOperationResult> RenameAsync(NpcRenameRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Failed));
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
