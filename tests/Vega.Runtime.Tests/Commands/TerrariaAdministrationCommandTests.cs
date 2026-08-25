using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TerrariaAdministrationCommandTests
{
    [Test]
    public async Task TimeNoonUsesWorldEnvironmentMutationContract()
    {
        var environment = new RecordingEnvironment();
        var command = new TimeCommand(environment);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("time", ["noon"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(environment.LastRequest, Is.Not.Null);
            Assert.That(environment.LastRequest!.WorldId, Is.EqualTo(WorldId.Primary));
            Assert.That(environment.LastRequest.Patch.IsDaytime, Is.True);
            Assert.That(environment.LastRequest.Patch.TimeTicks, Is.EqualTo(27000));
            Assert.That(environment.LastRequest.Actor, Is.EqualTo("server"));
        });
    }

    [Test]
    public async Task TeleportUsesAuthoritativeTargetSnapshotPosition()
    {
        PlayerSnapshot source = Player(3, "Alice", 100, 200);
        PlayerSnapshot target = Player(8, "Bob", 800, 900);
        var players = new RecordingPlayers(source, target);
        var operations = new RecordingPlayerOperations();
        var command = new TeleportCommand(players, operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("tp", ["Alice", "Bob"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.TeleportedPlayer, Is.EqualTo(source.Id));
            Assert.That(operations.TeleportDestination, Is.EqualTo(target.Position));
        });
    }

    [Test]
    public async Task HealDelegatesResolvedTargetAndRequestedAmount()
    {
        PlayerSnapshot target = Player(3, "Alice", 100, 200);
        var players = new RecordingPlayers(target);
        var operations = new RecordingPlayerOperations();
        var command = new HealCommand(players, operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("heal", ["Alice", "25"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.HealedPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.HealAmount, Is.EqualTo(25));
        });
    }

    [Test]
    public async Task BuffConvertsSecondsToTerrariaTicks()
    {
        PlayerSnapshot target = Player(3, "Alice", 100, 200);
        var players = new RecordingPlayers(target);
        var operations = new RecordingPlayerOperations();
        var command = new BuffCommand(players, operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("buff", ["7", "30", "Alice"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.BuffedPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.BuffType, Is.EqualTo(7));
            Assert.That(operations.BuffDurationTicks, Is.EqualTo(1800));
        });
    }

    [Test]
    public async Task KillUsesResolvedTargetAndAuditableReason()
    {
        PlayerSnapshot target = Player(3, "Alice", 100, 200);
        var players = new RecordingPlayers(target);
        var operations = new RecordingPlayerOperations();
        var command = new KillCommand(players, operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("kill", ["Alice"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.KilledPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.KillReason, Does.Contain("Alice"));
            Assert.That(operations.KillReason, Does.Contain("server"));
        });
    }

    [Test]
    public async Task RespawnDelegatesResolvedTarget()
    {
        PlayerSnapshot target = Player(3, "Alice", 100, 200);
        var players = new RecordingPlayers(target);
        var operations = new RecordingPlayerOperations();
        var command = new RespawnCommand(players, operations);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("respawn", ["Alice"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.RespawnedPlayer, Is.EqualTo(target.Id));
        });
    }

    [Test]
    public async Task ButcherDefaultsToHostileNonTownNpcsOnly()
    {
        NpcSnapshot hostile = Npc(1, "Zombie", friendly: false, town: false, boss: false);
        NpcSnapshot guide = Npc(2, "Guide", friendly: true, town: true, boss: false);
        NpcSnapshot boss = Npc(3, "Eye", friendly: false, town: false, boss: true);
        var npcs = new RecordingNpcs(hostile, guide, boss);
        var command = new ButcherCommand(npcs);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("butcher", []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(npcs.Despawned, Does.Contain(hostile.Handle));
            Assert.That(npcs.Despawned, Does.Contain(boss.Handle));
            Assert.That(npcs.Despawned, Does.Not.Contain(guide.Handle));
            Assert.That(npcs.Despawned.Count, Is.EqualTo(2));
        });
    }

    private static CommandInvocation Invocation(string name, IReadOnlyList<string> arguments) =>
        new(name, arguments, ServerPrincipal.Instance, NullOutput.Instance)
        {
            WorldId = WorldId.Primary
        };

    private static PlayerSnapshot Player(int slot, string name, float x, float y) =>
        new(
            new PlayerId(slot),
            name,
            IsDead: false,
            Life: 100,
            MaxLife: 100,
            Mana: 20,
            MaxMana: 20,
            new PlayerPosition(x, y),
            IsHostile: false,
            Team: 0);

    private static NpcSnapshot Npc(int slot, string name, bool friendly, bool town, bool boss) =>
        new(
            new NpcHandle(WorldId.Primary, slot, 100 + slot, slot),
            NetId: 100 + slot,
            name,
            new PlayerPosition(slot * 16, slot * 16),
            new WorldVelocity(0, 0),
            Life: 100,
            LifeMax: 100,
            friendly,
            town,
            boss,
            TargetPlayerSlot: -1,
            DateTimeOffset.UtcNow);

    private sealed class RecordingEnvironment : IWorldEnvironmentService
    {
        public WorldEnvironmentMutationRequest? LastRequest { get; private set; }

        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(Snapshot(worldId));

        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(
            WorldEnvironmentMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new WorldEnvironmentMutationResult(
                WorldEnvironmentMutationStatus.Applied,
                Snapshot(request.WorldId)));
        }

        private static WorldEnvironmentSnapshot Snapshot(WorldId worldId) => new(
            worldId,
            WidthTiles: 8400,
            HeightTiles: 2400,
            SpawnTileX: 100,
            SpawnTileY: 200,
            IsDaytime: true,
            TimeTicks: 1000,
            MoonPhase: 0,
            BloodMoon: false,
            Eclipse: false,
            Raining: false,
            RainIntensity: 0,
            WindSpeed: 0,
            Hardmode: false,
            DateTimeOffset.UtcNow);
    }

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

    private sealed class RecordingPlayerOperations : IPlayerLifecycleOperations
    {
        public PlayerId? TeleportedPlayer { get; private set; }
        public PlayerPosition? TeleportDestination { get; private set; }
        public PlayerId? HealedPlayer { get; private set; }
        public int? HealAmount { get; private set; }
        public PlayerId? KilledPlayer { get; private set; }
        public string? KillReason { get; private set; }
        public PlayerId? BuffedPlayer { get; private set; }
        public int? BuffType { get; private set; }
        public int? BuffDurationTicks { get; private set; }
        public PlayerId? RespawnedPlayer { get; private set; }

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId playerId,
            string reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId playerId,
            PlayerPosition destination,
            CancellationToken cancellationToken = default)
        {
            TeleportedPlayer = playerId;
            TeleportDestination = destination;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> HealAsync(
            PlayerId playerId,
            int? amount = null,
            CancellationToken cancellationToken = default)
        {
            HealedPlayer = playerId;
            HealAmount = amount;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> KillAsync(
            PlayerId playerId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            KilledPlayer = playerId;
            KillReason = reason;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> ApplyBuffAsync(
            PlayerId playerId,
            int buffType,
            int durationTicks,
            CancellationToken cancellationToken = default)
        {
            BuffedPlayer = playerId;
            BuffType = buffType;
            BuffDurationTicks = durationTicks;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }

        public ValueTask<PlayerOperationResult> RespawnAsync(
            PlayerId playerId,
            CancellationToken cancellationToken = default)
        {
            RespawnedPlayer = playerId;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class RecordingNpcs(params NpcSnapshot[] initial) : IWorldNpcService
    {
        private readonly NpcSnapshot[] snapshot = initial;

        public List<NpcHandle> Despawned { get; } = [];

        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>(snapshot);

        public ValueTask<NpcSnapshot?> CaptureAsync(
            NpcHandle handle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NpcSnapshot?>(snapshot.FirstOrDefault(npc => npc.Handle == handle));

        public ValueTask<NpcOperationResult> SpawnAsync(
            NpcSpawnRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));

        public ValueTask<NpcOperationResult> TeleportAsync(
            NpcTeleportRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));

        public ValueTask<NpcOperationResult> DespawnAsync(
            NpcDespawnRequest request,
            CancellationToken cancellationToken = default)
        {
            Despawned.Add(request.Expected);
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));
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
