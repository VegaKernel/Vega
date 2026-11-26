using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Modules.Administration;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class WorldCommandTests
{
    [TestCase("day", true, 0)]
    [TestCase("noon", true, 27000)]
    [TestCase("night", false, 0)]
    [TestCase("midnight", false, 16200)]
    [TestCase("4:29", false, 32340)]
    [TestCase("04:30", true, 0)]
    public async Task TimeUsesWorldContextAndConvertsClockToPhase(string input, bool day, int ticks)
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new TimeCommand(world)]);
        var result = await dispatcher.DispatchAsync(Invocation("time", input));
        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request, Is.EqualTo(new WorldEnvironmentMutationRequest(new WorldId("test"),
            new WorldEnvironmentPatch(IsDaytime: day, TimeTicks: ticks), "test-admin")));
    }

    [TestCase("wind", "NaN")]
    [TestCase("wind", "41")]
    [TestCase("time", "24:00")]
    public async Task InvalidValuesNeverReachWorld(string command, string value)
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new TimeCommand(world), new WindCommand(world)]);
        Assert.That((await dispatcher.DispatchAsync(Invocation(command, value))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(world.Request, Is.Null);
    }

    [Test]
    public async Task WindChecksPermissionAndAppliesFiniteSpeed()
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new WindCommand(world)]);
        var invocation = Invocation("wind", "-40");
        Assert.That((await dispatcher.DispatchAsync(invocation with { Principal = new Principal(false) })).Status,
            Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That(world.Request, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(invocation)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.WindSpeed, Is.EqualTo(-0.8f));
    }

    [Test]
    public async Task WorldEventSelectsCorrectPhaseAndRequiresEventPermission()
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new WorldEventCommand(world), new WorldInfoCommand(world)]);
        var invocation = Invocation("worldevent", "bloodmoon");
        var result = await dispatcher.DispatchAsync(invocation);
        Assert.That(result.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch, Is.EqualTo(new WorldEnvironmentPatch(IsDaytime: false, TimeTicks: 0, BloodMoon: true, Eclipse: false)));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Arguments = ["unknown"] })).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        var info = await dispatcher.DispatchAsync(Invocation("worldinfo", "") with { Arguments = [] });
        Assert.That(info.Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(info.Messages, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task SpawnSettingsAndModeUseNativeWorldPatchWithBoundedValues()
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new WorldSpawnSettingsCommand(world, true), new WorldSpawnSettingsCommand(world, false), new WorldModeCommand(world)]);
        Assert.That((await dispatcher.DispatchAsync(Invocation("spawnrate", "0"))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(Invocation("maxspawns", "201"))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(Invocation("worldmode", "journey"))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(world.Request, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(Invocation("spawnrate", "300"))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.DefaultSpawnRate, Is.EqualTo(300));
        Assert.That((await dispatcher.DispatchAsync(Invocation("maxspawns", "20"))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.DefaultMaxSpawns, Is.EqualTo(20));
        Assert.That((await dispatcher.DispatchAsync(Invocation("gamemode", "master"))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.GameMode, Is.EqualTo(2));
    }

    [Test]
    public async Task SetWorldPointsUseTileCoordinatesAndSeparatePermission()
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new WorldPointCommand(world, new EmptyPlayers(), false), new WorldPointCommand(world, new EmptyPlayers(), true)]);
        var invocation = Invocation("setspawn", "100") with { Arguments = ["100", "200"] };
        Assert.That((await dispatcher.DispatchAsync(invocation with { Principal = new Principal(false) })).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(invocation)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch, Is.EqualTo(new WorldEnvironmentPatch(Spawn: new(100, 200))));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Name = "setdungeon" })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch, Is.EqualTo(new WorldEnvironmentPatch(Dungeon: new(100, 200))));
    }

    [Test]
    public async Task NativeEventsAndSeasonOverridesAreExplicitOperations()
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new WorldEventCommand(world), new ForceSeasonCommand(world, true), new ForceSeasonCommand(world, false), new ClearAnglerCommand(world)]);
        var invasionOff = Invocation("worldevent", "goblinarmy") with { Arguments = ["goblinarmy", "off"] };
        Assert.That((await dispatcher.DispatchAsync(invasionOff)).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(world.Request, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(Invocation("worldevent", "sandstorm"))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.NativeEvent, Is.EqualTo(new NativeWorldEventChange(NativeWorldEvent.Sandstorm, true)));
        Assert.That((await dispatcher.DispatchAsync(Invocation("forcehalloween", "on"))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.ForceHalloween, Is.True);
        Assert.That((await dispatcher.DispatchAsync(Invocation("forcexmas", "off"))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.ForceChristmas, Is.False);
        Assert.That((await dispatcher.DispatchAsync(Invocation("clearangler", "") with { Arguments = [] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.ClearAnglerCompletion, Is.True);
    }

    [Test]
    public async Task HardmodeOnlyStartsNativeTransformationAfterExplicitConfirmation()
    {
        var world = new RecordingWorld();
        var dispatcher = new CommandDispatcher([new HardmodeCommand(world)]);
        Assert.That((await dispatcher.DispatchAsync(Invocation("hardmode", "status"))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await dispatcher.DispatchAsync(Invocation("hardmode", "start"))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(world.Request, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(Invocation("hardmode", "start") with { Arguments = ["start", "confirm"] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(world.Request?.Patch.StartHardmode, Is.True);
    }

    private sealed class EmptyPlayers : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) { player = null; return false; }
        public bool TryFindExact(string name, out PlayerSnapshot? player) { player = null; return false; }
    }

    private static CommandInvocation Invocation(string name, string argument) =>
        new(name, [argument], new Principal(true), new Output()) { WorldId = new("test") };

    private sealed class Principal(bool allowed) : ICommandPrincipal
    {
        public string Name => "test-admin";
        public bool IsServer => false;
        public bool HasPermission(string permission) => allowed;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingWorld : IWorldEnvironmentService
    {
        public WorldEnvironmentMutationRequest? Request { get; private set; }
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(WorldId worldId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(new(worldId, 4200, 1200, 100, 100, true, 0, 0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow));
        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(WorldEnvironmentMutationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(new WorldEnvironmentMutationResult(WorldEnvironmentMutationStatus.Applied));
        }
    }
}
