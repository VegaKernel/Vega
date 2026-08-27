using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Terraria.Commands;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class TerrariaSpawnMobCommandTests
{
    [Test]
    public void DescriptorMatchesPinnedTShockRegistration()
    {
        SpawnMobCommand command = CreateCommand(Player(3, "Alice"));

        Assert.Multiple(() =>
        {
            Assert.That(command.Descriptor.Aliases, Is.EqualTo(new[] { "sm" }));
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.npc.spawn" }));
            Assert.That(command.Descriptor.AllowServer, Is.False);
        });
    }

    [Test]
    public async Task NameResolutionSpawnsAtCallerWithDefaultAmount()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var npcs = new RecordingNpcs();
        SpawnMobCommand command = CreateCommand(caller, npcs: npcs);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["Zombie"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(npcs.Spawns.Single().Type, Is.EqualTo(20));
            Assert.That(npcs.Spawns.Single().Position, Is.EqualTo(caller.Position));
            Assert.That(npcs.Spawns.Single().TargetPlayerSlot, Is.EqualTo(caller.Id.Slot));
        });
    }

    [Test]
    public async Task AmbiguousAndMissingNpcAreRejected()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var npcs = new RecordingNpcs();
        var ambiguous = new RecordingNpcCatalog(new NpcResolutionResult(
            NpcResolutionStatus.Ambiguous,
            Matches: [new NpcDefinition(20, "Zombie"), new NpcDefinition(21, "Zombie Elf")]));
        var missing = new RecordingNpcCatalog(new NpcResolutionResult(NpcResolutionStatus.NotFound));

        CommandExecutionResult ambiguousResult = await CreateCommand(caller, npcs, ambiguous)
            .ExecuteAsync(Invocation(caller, ["zom"]), CancellationToken.None);
        CommandExecutionResult missingResult = await CreateCommand(caller, npcs, missing)
            .ExecuteAsync(Invocation(caller, ["missing"]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ambiguousResult.ErrorCode, Is.EqualTo("command.spawnmob.npc-ambiguous"));
            Assert.That(missingResult.ErrorCode, Is.EqualTo("command.spawnmob.npc-not-found"));
            Assert.That(npcs.Spawns, Is.Empty);
        });
    }

    [Test]
    public async Task AmountIsClampedToCapturedRuntimeNpcCapacity()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var npcs = new RecordingNpcs();
        SpawnMobCommand command = CreateCommand(caller, npcs: npcs, capacity: 3);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["20", "100"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(npcs.Spawns.Count, Is.EqualTo(3));
        });
    }

    [TestCaseSource(nameof(UnsafeArguments))]
    public async Task UnsafeAmountAndLegacyVegaTargetArgumentAreRejected(string[] arguments)
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var npcs = new RecordingNpcs();

        CommandExecutionResult result = await CreateCommand(caller, npcs: npcs)
            .ExecuteAsync(Invocation(caller, arguments), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That(npcs.Spawns, Is.Empty);
        });
    }

    private static IEnumerable<string[]> UnsafeArguments()
    {
        yield return ["Zombie", "0"];
        yield return ["Zombie", "-1"];
        yield return ["Zombie", "1", "Bob"];
    }

    [Test]
    public async Task WallOfFleshUsesDedicatedSpawnerAndRejectsMultipleAmount()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        var wall = new RecordingWallOfFleshSpawner();
        var catalog = new RecordingNpcCatalog(new NpcResolutionResult(
            NpcResolutionStatus.Found,
            new NpcDefinition(global::Terraria.ID.NPCID.WallofFlesh, "Wall of Flesh")));
        SpawnMobCommand command = CreateCommand(caller, catalog: catalog, wall: wall);

        CommandExecutionResult applied = await command.ExecuteAsync(
            Invocation(caller, ["Wall of Flesh"]),
            CancellationToken.None);
        CommandExecutionResult rejected = await command.ExecuteAsync(
            Invocation(caller, ["Wall of Flesh", "2"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(applied.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(rejected.ErrorCode, Is.EqualTo("command.spawnmob.wof-amount"));
            Assert.That(wall.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SilentExecutionDoesNotBroadcast()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        PlayerSnapshot observer = Player(4, "Bob");
        var messenger = new RecordingMessenger();
        SpawnMobCommand command = CreateCommand(caller, observer, messenger: messenger);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation(caller, ["Zombie"], silent: true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Messages?.Single().DefaultText, Does.StartWith("You spawned"));
            Assert.That(messenger.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task NonSilentExecutionBroadcastsToOtherPlayers()
    {
        PlayerSnapshot caller = Player(3, "Alice");
        PlayerSnapshot observer = Player(4, "Bob");
        var messenger = new RecordingMessenger();
        SpawnMobCommand command = CreateCommand(caller, observer, messenger: messenger);

        await command.ExecuteAsync(Invocation(caller, ["Zombie"]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(messenger.Messages.Select(static message => message.Id), Is.EqualTo(new[] { observer.Id }));
            Assert.That(messenger.Messages.Single().Message, Does.Contain("Alice"));
            Assert.That(messenger.Messages.Single().Message, Does.Contain("Zombie"));
        });
    }

    private static SpawnMobCommand CreateCommand(
        PlayerSnapshot caller,
        RecordingNpcs? npcs = null,
        INpcCatalog? catalog = null,
        int capacity = 200,
        IWallOfFleshSpawner? wall = null) =>
        CreateCommand([caller], npcs, catalog, capacity, wall);

    private static SpawnMobCommand CreateCommand(
        PlayerSnapshot caller,
        PlayerSnapshot observer,
        RecordingMessenger messenger) =>
        CreateCommand([caller, observer], messenger: messenger);

    private static SpawnMobCommand CreateCommand(
        IReadOnlyList<PlayerSnapshot> players,
        RecordingNpcs? npcs = null,
        INpcCatalog? catalog = null,
        int capacity = 200,
        IWallOfFleshSpawner? wall = null,
        RecordingMessenger? messenger = null) =>
        new(
            new RecordingPlayers(players),
            npcs ?? new RecordingNpcs(),
            catalog ?? new RecordingNpcCatalog(new NpcResolutionResult(
                NpcResolutionStatus.Found,
                new NpcDefinition(20, "Zombie"))),
            new RecordingSpawnControl(capacity),
            wall ?? new RecordingWallOfFleshSpawner(),
            messenger ?? new RecordingMessenger());

    private static CommandInvocation Invocation(PlayerSnapshot player, IReadOnlyList<string> arguments, bool silent = false) =>
        new("spawnmob", arguments, new PlayerPrincipal(player), NullOutput.Instance, silent);

    private static PlayerSnapshot Player(int slot, string name) => new(
        new PlayerId(slot), name, false, 100, 100, 20, 20,
        new PlayerPosition(slot * 100, slot * 200), false, 0);

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
        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(WorldId worldId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>([]);
        public ValueTask<NpcSnapshot?> CaptureAsync(NpcHandle handle, CancellationToken cancellationToken = default) => ValueTask.FromResult<NpcSnapshot?>(null);
        public ValueTask<NpcOperationResult> SpawnAsync(NpcSpawnRequest request, CancellationToken cancellationToken = default)
        {
            Spawns.Add(request);
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));
        }
        public ValueTask<NpcOperationResult> TeleportAsync(NpcTeleportRequest request, CancellationToken cancellationToken = default) => Applied();
        public ValueTask<NpcOperationResult> DespawnAsync(NpcDespawnRequest request, CancellationToken cancellationToken = default) => Applied();
        public ValueTask<NpcOperationResult> KillAsync(NpcKillRequest request, CancellationToken cancellationToken = default) => Applied();
        public ValueTask<NpcOperationResult> RenameAsync(NpcRenameRequest request, CancellationToken cancellationToken = default) => Applied();
        private static ValueTask<NpcOperationResult> Applied() => ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));
    }

    private sealed class RecordingNpcCatalog(NpcResolutionResult result) : INpcCatalog
    {
        public NpcResolutionResult Resolve(string query) => result;
    }

    private sealed class RecordingSpawnControl(int capacity) : IWorldNpcSpawnControl
    {
        public ValueTask<NpcSpawnLimitSnapshot?> CaptureAsync(WorldId worldId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NpcSpawnLimitSnapshot?>(new NpcSpawnLimitSnapshot(worldId, 5, capacity));
        public ValueTask<NpcSpawnLimitMutationResult> SetMaximumSpawnsAsync(WorldId worldId, int maximumSpawns, string actor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<NpcSpawnRateSnapshot?> CaptureSpawnRateAsync(WorldId worldId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<NpcSpawnRateMutationResult> SetSpawnRateAsync(WorldId worldId, int spawnRate, string actor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingWallOfFleshSpawner : IWallOfFleshSpawner
    {
        public int Calls { get; private set; }
        public ValueTask<PlayerOperationResult> SpawnAsync(WorldId worldId, PlayerPosition position, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class RecordingMessenger : IPlayerMessenger
    {
        public List<(PlayerId Id, string Message)> Messages { get; } = [];
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerId id, string message, CancellationToken cancellationToken = default)
        {
            Messages.Add((id, message));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class PlayerPrincipal(PlayerSnapshot player) : IPlayerCommandPrincipal
    {
        public PlayerId PlayerId => player.Id;
        public string PlayerName => player.Name;
        public string? AccountName => null;
        public string Name => player.Name;
        public bool IsServer => false;
        public string CultureSubjectId => $"player:{player.Id.Slot}";
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
