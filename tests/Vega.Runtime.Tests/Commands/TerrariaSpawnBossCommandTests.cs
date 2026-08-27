using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TerrariaSpawnBossCommandTests
{
    [Test]
    public void DescriptorMatchesPinnedTShockRegistration()
    {
        SpawnBossCommand command = CreateCommand();

        Assert.Multiple(() =>
        {
            Assert.That(command.Descriptor.Name, Is.EqualTo("spawnboss"));
            Assert.That(command.Descriptor.Aliases, Is.EqualTo(new[] { "sb" }));
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.npc.spawnboss" }));
            Assert.That(command.Descriptor.AllowServer, Is.False);
        });
    }

    [Test]
    public async Task AllForcesNightAndSpawnsEveryPinnedBossType()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var npcs = new RecordingNpcs();
        var environment = new RecordingEnvironment();
        var command = CreateCommand(caller, npcs, environment);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["all"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(environment.LastPatch?.IsDaytime, Is.False);
            Assert.That(environment.LastPatch?.TimeTicks, Is.EqualTo(0));
            Assert.That(npcs.Spawns.Select(static spawn => spawn.Type), Is.EqualTo(
                new[] { 4, 13, 35, 50, 125, 126, 127, 134, 222, 245, 262, 266, 370, 398, 439, 636, 657 }));
        });
    }

    [Test]
    public async Task TwinsSpawnBothEntitiesForEachRequestedIteration()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var npcs = new RecordingNpcs();
        var command = CreateCommand(caller, npcs);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["twins", "2"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(npcs.Spawns.Select(static spawn => spawn.Type), Is.EqualTo(new[] { 125, 125, 126, 126 }));
            Assert.That(npcs.Spawns.Select(static spawn => spawn.TargetPlayerSlot), Is.All.EqualTo(caller.Id.Slot));
        });
    }

    [TestCase("0")]
    [TestCase("51")]
    [TestCase("invalid")]
    public async Task AmountOutsideVegaSafetyBoundIsRejected(string amount)
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var npcs = new RecordingNpcs();
        var command = CreateCommand(caller, npcs);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["eoc", amount]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(npcs.Spawns, Is.Empty);
        });
    }

    [Test]
    public async Task WallOfFleshRejectsMisleadingMultipleAmount()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var wallOfFlesh = new RecordingWallOfFleshSpawner();
        var command = CreateCommand(caller, wallOfFlesh: wallOfFlesh);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["wof", "2"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo("command.spawnboss.wof-amount"));
            Assert.That(wallOfFlesh.Calls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task NonSilentSuccessBroadcastsToOtherPlayers()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        PlayerSnapshot observer = Player(4, "Bob");
        var messenger = new RecordingMessenger();
        var command = CreateCommand(caller, observer, messenger: messenger);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["eoc"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(messenger.Messages.Select(static entry => entry.Id), Is.EqualTo(new[] { observer.Id }));
            Assert.That(messenger.Messages.Single().Message, Does.Contain("Alice"));
            Assert.That(messenger.Messages.Single().Message, Does.Contain("Eye of Cthulhu"));
        });
    }

    [Test]
    public async Task SilentSuccessReturnsCallerOnlyMessageWithoutBroadcast()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        PlayerSnapshot observer = Player(4, "Bob");
        var messenger = new RecordingMessenger();
        var command = CreateCommand(caller, observer, messenger: messenger);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["eoc"], silent: true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages?.Single().DefaultText, Does.StartWith("You spawned"));
            Assert.That(messenger.Messages, Is.Empty);
        });
    }

    private static SpawnBossCommand CreateCommand(
        params PlayerSnapshot[] players) =>
        CreateCommand(players, new RecordingNpcs(), new RecordingEnvironment());

    private static SpawnBossCommand CreateCommand(
        PlayerSnapshot player,
        RecordingNpcs npcs,
        RecordingEnvironment? environment = null) =>
        CreateCommand([player], npcs, environment ?? new RecordingEnvironment());

    private static SpawnBossCommand CreateCommand(
        PlayerSnapshot first,
        PlayerSnapshot second,
        RecordingMessenger messenger) =>
        CreateCommand([first, second], new RecordingNpcs(), new RecordingEnvironment(), messenger: messenger);

    private static SpawnBossCommand CreateCommand(
        PlayerSnapshot player,
        IWallOfFleshSpawner wallOfFlesh) =>
        CreateCommand([player], new RecordingNpcs(), new RecordingEnvironment(), wallOfFlesh);

    private static SpawnBossCommand CreateCommand(
        IReadOnlyList<PlayerSnapshot> players,
        RecordingNpcs npcs,
        RecordingEnvironment environment,
        IWallOfFleshSpawner? wallOfFlesh = null,
        RecordingMessenger? messenger = null) =>
        new(
            new RecordingPlayers(players),
            npcs,
            environment,
            wallOfFlesh ?? new RecordingWallOfFleshSpawner(),
            messenger ?? new RecordingMessenger());

    private static CommandInvocation Invocation(
        PlayerSnapshot player,
        IReadOnlyList<string> arguments,
        bool silent = false) =>
        new(
            "spawnboss",
            arguments,
            new PlayerPrincipal(player.Id, player.Name),
            NullOutput.Instance,
            silent)
        {
            WorldId = WorldId.Primary
        };

    private static PlayerSnapshot Player(int slot, string name) => new(
        new PlayerId(slot),
        name,
        IsDead: false,
        Life: 100,
        MaxLife: 100,
        Mana: 20,
        MaxMana: 20,
        new PlayerPosition(slot * 100, slot * 200),
        IsHostile: false,
        Team: 0);

    private sealed class RecordingPlayers(IReadOnlyList<PlayerSnapshot> players) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class RecordingNpcs : IWorldNpcService
    {
        public List<NpcSpawnRequest> Spawns { get; } = [];

        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>([]);

        public ValueTask<NpcSnapshot?> CaptureAsync(
            NpcHandle handle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NpcSnapshot?>(null);

        public ValueTask<NpcOperationResult> SpawnAsync(
            NpcSpawnRequest request,
            CancellationToken cancellationToken = default)
        {
            Spawns.Add(request);
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));
        }

        public ValueTask<NpcOperationResult> TeleportAsync(NpcTeleportRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));

        public ValueTask<NpcOperationResult> DespawnAsync(NpcDespawnRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));

        public ValueTask<NpcOperationResult> KillAsync(NpcKillRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));

        public ValueTask<NpcOperationResult> RenameAsync(NpcRenameRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));
    }

    private sealed class RecordingEnvironment : IWorldEnvironmentService
    {
        public WorldEnvironmentPatch? LastPatch { get; private set; }

        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(WorldId worldId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(null);

        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(
            WorldEnvironmentMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastPatch = request.Patch;
            return ValueTask.FromResult(new WorldEnvironmentMutationResult(WorldEnvironmentMutationStatus.Applied));
        }
    }

    private sealed class RecordingWallOfFleshSpawner : IWallOfFleshSpawner
    {
        public int Calls { get; private set; }

        public ValueTask<PlayerOperationResult> SpawnAsync(
            WorldId worldId,
            PlayerPosition position,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
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
