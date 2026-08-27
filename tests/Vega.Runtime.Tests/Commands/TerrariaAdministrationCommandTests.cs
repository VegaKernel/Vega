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
        var command = new TeleportCommand(players, operations, new NullMessenger());

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
    public async Task ButcherDefaultsToAllNonTownNpcsExceptTargetDummyAndUsesKillPath()
    {
        NpcSnapshot hostile = Npc(1, "Zombie", friendly: false, town: false, boss: false);
        NpcSnapshot guide = Npc(2, "Guide", friendly: true, town: true, boss: false);
        NpcSnapshot boss = Npc(3, "Eye", friendly: false, town: false, boss: true);
        NpcSnapshot bunny = Npc(4, "Bunny", friendly: true, town: false, boss: false);
        NpcSnapshot dummy = Npc(
            5,
            "Target Dummy",
            friendly: false,
            town: false,
            boss: false,
            netId: global::Terraria.ID.NPCID.TargetDummy);
        var npcs = new RecordingNpcs(hostile, guide, boss, bunny, dummy);
        var command = new ButcherCommand(npcs, new RecordingNpcCatalog(), new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("butcher", []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(npcs.Killed, Does.Contain(hostile.Handle));
            Assert.That(npcs.Killed, Does.Contain(boss.Handle));
            Assert.That(npcs.Killed, Does.Contain(bunny.Handle));
            Assert.That(npcs.Killed, Does.Not.Contain(guide.Handle));
            Assert.That(npcs.Killed, Does.Not.Contain(dummy.Handle));
            Assert.That(npcs.Killed.Count, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task TeleportOneArgumentMovesCallerAndNotifiesDestination()
    {
        PlayerSnapshot source = Player(3, "Alice", 100, 200);
        PlayerSnapshot target = Player(8, "Bob", 800, 900);
        var players = new RecordingPlayers(source, target);
        var operations = new RecordingPlayerOperations();
        var messenger = new RecordingMessenger();
        var command = new TeleportCommand(players, operations, messenger);
        var principal = new PlayerPrincipal(source.Id, source.Name, "vega.players.teleport");

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation("tp", ["Bob"], principal, NullOutput.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.TeleportedPlayer, Is.EqualTo(source.Id));
            Assert.That(operations.TeleportDestination, Is.EqualTo(target.Position));
            Assert.That(messenger.Messages.Single().Id, Is.EqualTo(target.Id));
            Assert.That(command.Descriptor.Aliases, Is.Empty);
            Assert.That(command.Descriptor.AllowServer, Is.False);
        });
    }

    [Test]
    public async Task TeleportTwoArgumentsRequiresOthersPermission()
    {
        PlayerSnapshot source = Player(3, "Alice", 100, 200);
        PlayerSnapshot target = Player(8, "Bob", 800, 900);
        var command = new TeleportCommand(
            new RecordingPlayers(source, target),
            new RecordingPlayerOperations(),
            new NullMessenger());
        var principal = new PlayerPrincipal(source.Id, source.Name, "vega.players.teleport");

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation("tp", ["Alice", "Bob"], principal, NullOutput.Instance),
            CancellationToken.None);

        Assert.That(result.ErrorCode, Is.EqualTo("command.tp.others-forbidden"));
    }

    [Test]
    public async Task TeleportAllExcludesActorAndRequiresAllPermission()
    {
        PlayerSnapshot actor = Player(1, "Admin", 50, 60);
        PlayerSnapshot first = Player(3, "Alice", 100, 200);
        PlayerSnapshot target = Player(8, "Bob", 800, 900);
        var operations = new RecordingPlayerOperations();
        var command = new TeleportCommand(
            new RecordingPlayers(actor, first, target),
            operations,
            new NullMessenger());
        var principal = new PlayerPrincipal(
            actor.Id,
            actor.Name,
            "vega.players.teleport",
            "vega.players.teleport.others",
            "vega.players.teleport.all",
            "vega.players.teleport.silent");

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation("tp", ["*", "Bob"], principal, NullOutput.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.TeleportedPlayers, Is.EqualTo(new[] { first.Id, target.Id }));
        });
    }

    [Test]
    public async Task TeleportHereJoinsPlayerNameAndMovesPlayerToCaller()
    {
        PlayerSnapshot actor = Player(1, "Admin", 50, 60);
        PlayerSnapshot target = Player(3, "Alice Smith", 100, 200);
        var operations = new RecordingPlayerOperations();
        var messenger = new RecordingMessenger();
        var command = new TeleportHereCommand(
            new RecordingPlayers(actor, target),
            operations,
            messenger);
        var principal = new PlayerPrincipal(
            actor.Id,
            actor.Name,
            "vega.players.teleport.others");

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation("tphere", ["Alice", "Smith"], principal, NullOutput.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.TeleportedPlayer, Is.EqualTo(target.Id));
            Assert.That(operations.TeleportDestination, Is.EqualTo(actor.Position));
            Assert.That(messenger.Messages.Single().Id, Is.EqualTo(target.Id));
            Assert.That(command.Descriptor.Aliases, Is.Empty);
            Assert.That(command.Descriptor.AllowServer, Is.False);
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.players.teleport.others" }));
        });
    }

    [Test]
    public async Task TeleportHereAllExcludesCaller()
    {
        PlayerSnapshot actor = Player(1, "Admin", 50, 60);
        PlayerSnapshot first = Player(3, "Alice", 100, 200);
        PlayerSnapshot second = Player(8, "Bob", 800, 900);
        var operations = new RecordingPlayerOperations();
        var command = new TeleportHereCommand(
            new RecordingPlayers(actor, first, second),
            operations,
            new NullMessenger());
        var principal = new PlayerPrincipal(
            actor.Id,
            actor.Name,
            "vega.players.teleport.others",
            "vega.players.teleport.all");

        CommandExecutionResult result = await command.ExecuteAsync(
            new CommandInvocation("tphere", ["*"], principal, NullOutput.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(operations.TeleportedPlayers, Is.EqualTo(new[] { first.Id, second.Id }));
            Assert.That(operations.TeleportDestination, Is.EqualTo(actor.Position));
        });
    }

    [Test]
    public async Task ButcherWithNpcSelectorKillsOnlyMatchingNetId()
    {
        NpcSnapshot first = Npc(1, "Zombie", friendly: false, town: false, boss: false, netId: 20);
        NpcSnapshot second = Npc(2, "Zombie", friendly: false, town: false, boss: false, netId: 20);
        NpcSnapshot other = Npc(3, "Demon Eye", friendly: false, town: false, boss: false, netId: 21);
        var npcs = new RecordingNpcs(first, second, other);
        var command = new ButcherCommand(
            npcs,
            new RecordingNpcCatalog(new NpcDefinition(20, "Zombie")),
            new RecordingPlayers(),
            new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("butcher", ["Zombie"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(npcs.Killed, Is.EqualTo(new[] { first.Handle, second.Handle }));
        });
    }

    [Test]
    public async Task RenameNpcRenamesEveryActiveTownNpcOfResolvedType()
    {
        NpcSnapshot firstGuide = Npc(1, "Guide", friendly: true, town: true, boss: false, netId: 22);
        NpcSnapshot secondGuide = Npc(2, "Guide", friendly: true, town: true, boss: false, netId: 22);
        NpcSnapshot zombie = Npc(3, "Zombie", friendly: false, town: false, boss: false, netId: 20);
        var npcs = new RecordingNpcs(firstGuide, secondGuide, zombie);
        var command = new RenameNpcCommand(
            npcs,
            new RecordingNpcCatalog(new NpcDefinition(22, "Guide")),
            new RecordingPlayers(),
            new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("renamenpc", ["Guide", "Andrew"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(npcs.Renamed.Select(static entry => entry.Handle),
                Is.EqualTo(new[] { firstGuide.Handle, secondGuide.Handle }));
            Assert.That(npcs.Renamed.Select(static entry => entry.Name),
                Is.All.EqualTo("Andrew"));
        });
    }

    [Test]
    public async Task RenameNpcRejectsNamesLongerThanTShockLimit()
    {
        var npcs = new RecordingNpcs();
        var command = new RenameNpcCommand(
            npcs,
            new RecordingNpcCatalog(new NpcDefinition(22, "Guide")),
            new RecordingPlayers(),
            new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("renamenpc", ["Guide", new string('x', 201)]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo("command.renamenpc.invalid-name"));
            Assert.That(npcs.Renamed, Is.Empty);
        });
    }

    [Test]
    public async Task MaxSpawnsWithoutArgumentsReportsCurrentLimitWithoutMutation()
    {
        var spawnControl = new RecordingNpcSpawnControl(maximumSpawns: 7, capacity: 200);
        var command = new MaxSpawnsCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("maxspawns", []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages?.Single().DefaultText, Does.Contain("7"));
            Assert.That(spawnControl.SetValues, Is.Empty);
        });
    }

    [Test]
    public async Task MaxSpawnsDefaultUsesTerrariaDefaultAndPermission()
    {
        var spawnControl = new RecordingNpcSpawnControl(maximumSpawns: 12, capacity: 200);
        var command = new MaxSpawnsCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("maxspawns", ["default"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(spawnControl.SetValues, Is.EqualTo(new[] { 5 }));
            Assert.That(spawnControl.LastActor, Is.EqualTo("server"));
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.npc.maxspawns" }));
            Assert.That(command.Descriptor.AllowServer, Is.True);
        });
    }

    [Test]
    public async Task MaxSpawnsUsesFirstArgumentLikeTShockAndAcceptsCapacity()
    {
        var spawnControl = new RecordingNpcSpawnControl(maximumSpawns: 5, capacity: 200);
        var command = new MaxSpawnsCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("maxspawns", ["200", "ignored"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(spawnControl.SetValues, Is.EqualTo(new[] { 200 }));
        });
    }

    [TestCase("-1")]
    [TestCase("201")]
    [TestCase("not-a-number")]
    public async Task MaxSpawnsRejectsValuesOutsideRuntimeCapacity(string value)
    {
        var spawnControl = new RecordingNpcSpawnControl(maximumSpawns: 5, capacity: 200);
        var command = new MaxSpawnsCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("maxspawns", [value]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo("command.maxspawns.invalid-value"));
            Assert.That(spawnControl.SetValues, Is.Empty);
        });
    }

    [Test]
    public async Task SilentMaxSpawnsDoesNotBroadcast()
    {
        PlayerSnapshot observer = Player(3, "Alice", 100, 200);
        var messenger = new RecordingMessenger();
        var command = new MaxSpawnsCommand(
            new RecordingNpcSpawnControl(maximumSpawns: 5, capacity: 200),
            new RecordingPlayers(observer),
            messenger);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("maxspawns", ["10"], silent: true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(messenger.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task SpawnRateWithoutArgumentsReportsCurrentValue()
    {
        var spawnControl = new RecordingNpcSpawnControl(5, 200, spawnRate: 450);
        var command = new SpawnRateCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("spawnrate", []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages?.Single().DefaultText, Does.Contain("450"));
            Assert.That(spawnControl.SetSpawnRates, Is.Empty);
        });
    }

    [Test]
    public async Task SpawnRateDefaultUsesTerrariaDefaultAndPermission()
    {
        var spawnControl = new RecordingNpcSpawnControl(5, 200, spawnRate: 450);
        var command = new SpawnRateCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("spawnrate", ["default"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(spawnControl.SetSpawnRates, Is.EqualTo(new[] { 600 }));
            Assert.That(spawnControl.LastActor, Is.EqualTo("server"));
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.npc.spawnrate" }));
            Assert.That(command.Descriptor.AllowServer, Is.True);
        });
    }

    [Test]
    public async Task SpawnRateAcceptsZeroAndIgnoresTrailingArgumentsLikeTShock()
    {
        var spawnControl = new RecordingNpcSpawnControl(5, 200, spawnRate: 450);
        var command = new SpawnRateCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("spawnrate", ["0", "ignored"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(spawnControl.SetSpawnRates, Is.EqualTo(new[] { 0 }));
        });
    }

    [TestCase("-1")]
    [TestCase("not-a-number")]
    public async Task SpawnRateRejectsInvalidValues(string value)
    {
        var spawnControl = new RecordingNpcSpawnControl(5, 200, spawnRate: 450);
        var command = new SpawnRateCommand(spawnControl, new RecordingPlayers(), new NullMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("spawnrate", [value]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo("command.spawnrate.invalid-value"));
            Assert.That(spawnControl.SetSpawnRates, Is.Empty);
        });
    }

    [Test]
    public async Task SilentSpawnRateDoesNotBroadcast()
    {
        PlayerSnapshot observer = Player(3, "Alice", 100, 200);
        var messenger = new RecordingMessenger();
        var command = new SpawnRateCommand(
            new RecordingNpcSpawnControl(5, 200),
            new RecordingPlayers(observer),
            messenger);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("spawnrate", ["300"], silent: true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(result.Messages?.Single().DefaultText, Does.StartWith("Changed the spawn rate"));
            Assert.That(messenger.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task ClearAnglerWithoutArgumentsClearsEntireDailyList()
    {
        var service = new RecordingAnglerQuestService(removedEntries: 3);
        var command = new ClearAnglerCommand(service);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("clearangler", []),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(service.LastRequest?.PlayerName, Is.Null);
            Assert.That(result.Messages?.Single().DefaultText, Does.Contain("all users"));
            Assert.That(command.Descriptor.Permissions, Is.EqualTo(new[] { "vega.angler.clear" }));
            Assert.That(command.Descriptor.AllowServer, Is.True);
        });
    }

    [Test]
    public async Task ClearAnglerUsesFirstPlayerNameAndIgnoresTrailingArguments()
    {
        var service = new RecordingAnglerQuestService(removedEntries: 2);
        var command = new ClearAnglerCommand(service);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("clearangler", ["Alice", "ignored"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(service.LastRequest?.PlayerName, Is.EqualTo("Alice"));
            Assert.That(result.Messages?.Single().DefaultText, Does.Contain("2 player entries"));
        });
    }

    [Test]
    public async Task ClearAnglerReportsMissingDailyCompletion()
    {
        var service = new RecordingAnglerQuestService(
            removedEntries: 0,
            status: AnglerQuestClearStatus.NotFound);
        var command = new ClearAnglerCommand(service);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("clearangler", ["Alice"]),
            CancellationToken.None);

        Assert.That(result.ErrorCode, Is.EqualTo("command.clearangler.not-found"));
    }

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        bool silent = false) =>
        new(name, arguments, ServerPrincipal.Instance, NullOutput.Instance, silent)
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

    private static NpcSnapshot Npc(
        int slot,
        string name,
        bool friendly,
        bool town,
        bool boss,
        int? netId = null) =>
        new(
            new NpcHandle(WorldId.Primary, slot, netId ?? 100 + slot, slot),
            NetId: netId ?? 100 + slot,
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
        public List<PlayerId> TeleportedPlayers { get; } = [];
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
            TeleportedPlayers.Add(playerId);
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
        public List<NpcHandle> Killed { get; } = [];
        public List<(NpcHandle Handle, string Name)> Renamed { get; } = [];

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

        public ValueTask<NpcOperationResult> KillAsync(
            NpcKillRequest request,
            CancellationToken cancellationToken = default)
        {
            Killed.Add(request.Expected);
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));
        }

        public ValueTask<NpcOperationResult> RenameAsync(
            NpcRenameRequest request,
            CancellationToken cancellationToken = default)
        {
            Renamed.Add((request.Expected, request.Name));
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied));
        }
    }

    private sealed class RecordingNpcSpawnControl(int maximumSpawns, int capacity, int spawnRate = 600) : IWorldNpcSpawnControl
    {
        public List<int> SetValues { get; } = [];
        public List<int> SetSpawnRates { get; } = [];
        public string? LastActor { get; private set; }

        public ValueTask<NpcSpawnLimitSnapshot?> CaptureAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NpcSpawnLimitSnapshot?>(
                new NpcSpawnLimitSnapshot(worldId, maximumSpawns, capacity));

        public ValueTask<NpcSpawnLimitMutationResult> SetMaximumSpawnsAsync(
            WorldId worldId,
            int value,
            string actor,
            CancellationToken cancellationToken = default)
        {
            SetValues.Add(value);
            LastActor = actor;
            maximumSpawns = value;
            return ValueTask.FromResult(new NpcSpawnLimitMutationResult(
                NpcOperationStatus.Applied,
                new NpcSpawnLimitSnapshot(worldId, value, capacity)));
        }

        public ValueTask<NpcSpawnRateSnapshot?> CaptureSpawnRateAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NpcSpawnRateSnapshot?>(new NpcSpawnRateSnapshot(worldId, spawnRate));

        public ValueTask<NpcSpawnRateMutationResult> SetSpawnRateAsync(
            WorldId worldId,
            int value,
            string actor,
            CancellationToken cancellationToken = default)
        {
            SetSpawnRates.Add(value);
            LastActor = actor;
            spawnRate = value;
            return ValueTask.FromResult(new NpcSpawnRateMutationResult(
                NpcOperationStatus.Applied,
                new NpcSpawnRateSnapshot(worldId, value)));
        }
    }

    private sealed class RecordingAnglerQuestService(
        int removedEntries,
        AnglerQuestClearStatus status = AnglerQuestClearStatus.Applied) : IWorldAnglerQuestService
    {
        public AnglerQuestClearRequest? LastRequest { get; private set; }

        public ValueTask<AnglerQuestClearResult> ClearAsync(
            AnglerQuestClearRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new AnglerQuestClearResult(status, removedEntries));
        }
    }

    private sealed class RecordingNpcCatalog(params NpcDefinition[] definitions) : INpcCatalog
    {
        public NpcResolutionResult Resolve(string query)
        {
            NpcDefinition[] matches = definitions
                .Where(npc => npc.NetId.ToString() == query ||
                    npc.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return matches.Length switch
            {
                0 => new NpcResolutionResult(NpcResolutionStatus.NotFound),
                1 => new NpcResolutionResult(NpcResolutionStatus.Found, matches[0]),
                _ => new NpcResolutionResult(NpcResolutionStatus.Ambiguous, Matches: matches)
            };
        }
    }

    private sealed class NullMessenger : IPlayerMessenger
    {
        public ValueTask<PlayerOperationResult> SendMessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
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

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();

        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class PlayerPrincipal(
        PlayerId playerId,
        string name,
        params string[] permissions) : IPlayerCommandPrincipal
    {
        private readonly HashSet<string> granted = new(permissions, StringComparer.OrdinalIgnoreCase);

        public PlayerId PlayerId { get; } = playerId;
        public string PlayerName => name;
        public string? AccountName => null;
        public string Name => name;
        public bool IsServer => false;
        public string CultureSubjectId => $"player:{PlayerId.Slot}";
        public bool HasPermission(string permission) => granted.Contains(permission);
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
