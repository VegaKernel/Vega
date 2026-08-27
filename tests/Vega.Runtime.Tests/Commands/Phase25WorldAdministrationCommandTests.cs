using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;
using Vega.Terraria.Commands;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class Phase25WorldAdministrationCommandTests
{
    [Test]
    public async Task ProtectSpawnUsesCurrentSpawnAndTShockDefaultRadius()
    {
        var protection = new RuntimeWorldBuildProtectionService();
        var command = new ProtectSpawnCommand(
            new RecordingEnvironment(spawnX: 100, spawnY: 50),
            protection,
            new RecordingPlayers(),
            new RecordingMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("protectspawn", [], ServerPrincipal.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(protection.IsSpawnProtectionEnabled, Is.True);
            Assert.That(protection.SpawnProtectionRadiusTiles, Is.EqualTo(10));
            Assert.That(protection.IsSpawnProtected(110, 50), Is.True);
            Assert.That(protection.IsSpawnProtected(111, 50), Is.False);
            Assert.That(protection.IsSpawnProtected(106, 58), Is.True);
            Assert.That(protection.IsSpawnProtected(108, 58), Is.False);
        });
    }

    [Test]
    public async Task SetSpawnMovesActiveSpawnProtectionOrigin()
    {
        var protection = new RuntimeWorldBuildProtectionService();
        protection.ToggleSpawnProtection(0, 0, 10);
        var world = new RecordingWorldAdministration
        {
            SetSpawnResult = new WorldAdministrationResult(
                WorldAdministrationStatus.Applied,
                Value: "spawn",
                TileX: 120,
                TileY: 80)
        };
        var command = new SetSpawnCommand(world, protection);
        var principal = new PlayerPrincipal(new PlayerId(3), "Alice", ["vega.world.setspawn"]);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("setspawn", [], principal),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(world.LastPlayerId, Is.EqualTo(new PlayerId(3)));
            Assert.That(protection.IsSpawnProtected(120, 80), Is.True);
            Assert.That(protection.IsSpawnProtected(0, 0), Is.False);
        });
    }

    [Test]
    public async Task WorldEventParsesInvasionVariantAndWave()
    {
        var world = new RecordingWorldAdministration();
        var command = new WorldEventCommand(
            world,
            new RecordingPlayers(),
            new RecordingMessenger());

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("worldevent", ["invasion", "pumpkinmoon", "7"], ServerPrincipal.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(world.LastEvent, Is.EqualTo(new WorldEventRequest("invasion", "pumpkinmoon", 7)));
        });
    }

    [Test]
    public async Task GrowPassesSeparateEvilPermissionToWorldBoundary()
    {
        var world = new RecordingWorldAdministration();
        var command = new GrowCommand(world);
        var denied = new PlayerPrincipal(new PlayerId(2), "Alice", ["vega.world.grow"]);
        var allowed = new PlayerPrincipal(
            new PlayerId(2),
            "Alice",
            ["vega.world.grow", "vega.world.grow.evil"]);

        await command.ExecuteAsync(
            Invocation("grow", ["ebonwood"], denied),
            CancellationToken.None);
        bool deniedFlag = world.LastAllowEvil;

        await command.ExecuteAsync(
            Invocation("grow", ["ebonwood"], allowed),
            CancellationToken.None);
        bool allowedFlag = world.LastAllowEvil;

        Assert.Multiple(() =>
        {
            Assert.That(deniedFlag, Is.False);
            Assert.That(allowedFlag, Is.True);
            Assert.That(world.LastPlant, Is.EqualTo("ebonwood"));
        });
    }

    [Test]
    public async Task HardmodePreservesBoundaryRejection()
    {
        var world = new RecordingWorldAdministration
        {
            HardmodeResult = new WorldAdministrationResult(
                WorldAdministrationStatus.Rejected,
                "world.admin.hardmode_cancelled")
        };
        var command = new HardmodeCommand(world);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("hardmode", [], ServerPrincipal.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("world.admin.hardmode_cancelled"));
        });
    }

    [Test]
    public async Task SettleTreatsAlreadyRunningAsStableSuccess()
    {
        var world = new RecordingWorldAdministration
        {
            SettleResult = new WorldAdministrationResult(
                WorldAdministrationStatus.AlreadyRunning,
                "world.admin.liquids.already_settling")
        };
        var command = new SettleCommand(world);

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("settle", [], ServerPrincipal.Instance),
            CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
    }

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        ICommandPrincipal principal) =>
        new(name, arguments, principal, NullOutput.Instance) { WorldId = WorldId.Primary };

    private sealed class RecordingWorldAdministration : IWorldAdministrationService
    {
        public WorldEventRequest? LastEvent { get; private set; }
        public PlayerId? LastPlayerId { get; private set; }
        public string? LastPlant { get; private set; }
        public bool LastAllowEvil { get; private set; }

        public WorldAdministrationResult HardmodeResult { get; init; } = Applied("on");
        public WorldAdministrationResult SetSpawnResult { get; init; } = Applied("spawn", 100, 50);
        public WorldAdministrationResult SettleResult { get; init; } = Applied("settling");

        public ValueTask<WorldAdministrationResult> ToggleHalloweenAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Applied("enabled"));

        public ValueTask<WorldAdministrationResult> ToggleXmasAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Applied("enabled"));

        public ValueTask<WorldAdministrationResult> ToggleHardmodeAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(HardmodeResult);

        public ValueTask<WorldAdministrationResult> ToggleEvilAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Applied("crimson"));

        public ValueTask<WorldAdministrationResult> TriggerEventAsync(
            WorldId worldId,
            WorldEventRequest request,
            CancellationToken cancellationToken = default)
        {
            LastEvent = request;
            return ValueTask.FromResult(Applied(request.EventType));
        }

        public ValueTask<WorldAdministrationResult> GrowAsync(
            WorldId worldId,
            PlayerId playerId,
            string plant,
            bool allowEvil,
            CancellationToken cancellationToken = default)
        {
            LastPlayerId = playerId;
            LastPlant = plant;
            LastAllowEvil = allowEvil;
            return ValueTask.FromResult(Applied(plant));
        }

        public ValueTask<WorldAdministrationResult> SetSpawnAsync(
            WorldId worldId,
            PlayerId playerId,
            CancellationToken cancellationToken = default)
        {
            LastPlayerId = playerId;
            return ValueTask.FromResult(SetSpawnResult);
        }

        public ValueTask<WorldAdministrationResult> SetDungeonAsync(
            WorldId worldId,
            PlayerId playerId,
            CancellationToken cancellationToken = default)
        {
            LastPlayerId = playerId;
            return ValueTask.FromResult(Applied("dungeon", 100, 50));
        }

        public ValueTask<WorldAdministrationResult> SettleLiquidsAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SettleResult);

        private static WorldAdministrationResult Applied(
            string value,
            int? tileX = null,
            int? tileY = null) =>
            new(WorldAdministrationStatus.Applied, Value: value, TileX: tileX, TileY: tileY);
    }

    private sealed class RecordingEnvironment(int spawnX, int spawnY) : IWorldEnvironmentService
    {
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(new WorldEnvironmentSnapshot(
                worldId,
                8400,
                2400,
                spawnX,
                spawnY,
                true,
                0,
                0,
                false,
                false,
                false,
                0,
                0,
                false,
                DateTimeOffset.UtcNow));

        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(
            WorldEnvironmentMutationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new WorldEnvironmentMutationResult(
                WorldEnvironmentMutationStatus.Applied));
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

    private sealed class RecordingMessenger : IPlayerMessenger
    {
        public ValueTask<PlayerOperationResult> SendMessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class PlayerPrincipal(
        PlayerId playerId,
        string name,
        IReadOnlyCollection<string> permissions) : IPlayerCommandPrincipal
    {
        public PlayerId PlayerId { get; } = playerId;
        public string PlayerName { get; } = name;
        public string? AccountName => null;
        public string CultureSubjectId => $"player:{PlayerId.Slot}";
        public string Name { get; } = name;
        public bool IsServer => false;
        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
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