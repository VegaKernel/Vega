using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Modules.Administration;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class WorldNativeActionCommandTests
{
    private static readonly WorldId World = new("arena");

    [Test]
    public async Task EvilAndSettleAreWorldScopedAndReportNativeState()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new EvilCommand(fixture), new SettleCommand(fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("evil", ["unknown"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(fixture.Action, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("evil", ["crimson"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Action, Is.EqualTo("evil"));
        Assert.That(fixture.Crimson, Is.True);
        Assert.That(fixture.RequestedWorld, Is.EqualTo(World));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("evil", []))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Crimson, Is.False);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("settle", []))).Messages!.Single().Key, Is.EqualTo("settle.started"));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("settle", []))).Messages!.Single().Key, Is.EqualTo("settle.already_active"));
    }

    [Test]
    public async Task GrowthRequiresExplicitAdministratorBypassAndPreservesPlayerSession()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new GrowCommand(fixture, fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("grow", ["basic"], admin: false))).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("grow", ["shadewood"], evil: false))).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("grow", ["basic", "100", "200"], console: false))).Status,
            Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(fixture.GrowRequest, Is.Null);

        Assert.That((await dispatcher.DispatchAsync(fixture.Command("grow", ["basic"], console: false))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.GrowRequest, Is.EqualTo(new WorldGrowRequest(World, WorldGrowKind.Basic, fixture.Player)));
        var stale = fixture.Command("grow", ["basic"], console: false) with
        { Principal = new PlayerPrincipal(fixture.Player with { SessionId = PlayerSessionId.New() }) };
        Assert.That((await dispatcher.DispatchAsync(stale)).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("grow", ["sakura", "100", "200"], console: true))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.GrowRequest, Is.EqualTo(new WorldGrowRequest(World, WorldGrowKind.Sakura, Coordinates: new(100, 200))));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("grow", ["1", "100", "200"], console: true))).Status,
            Is.EqualTo(CommandExecutionStatus.InvalidUsage));
    }

    private sealed class Fixture : IWorldNativeMaintenanceOperations, IPlayerDirectory
    {
        public PlayerSnapshot Player { get; } = new(new(1), "Alice", false, 100, 100, 20, 20, new(1600, 1600), false, 0)
        { SessionId = PlayerSessionId.New(), WorldId = World };
        public string? Action { get; private set; }
        public WorldId? RequestedWorld { get; private set; }
        public bool Crimson { get; private set; }
        private bool settling;
        public WorldGrowRequest? GrowRequest { get; private set; }

        public CommandInvocation Command(string name, string[] args, bool admin = true, bool evil = true, bool console = false) =>
            new(name, args, console ? new ConsolePrincipal() : new PlayerPrincipal(Player, admin, evil), new Output()) { WorldId = World };
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [Player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = id == Player.Id ? Player : null) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = name == Player.Name ? Player : null) is not null;

        public ValueTask<WorldNativeActionResult> SwitchEvilAsync(WorldId worldId, bool? crimson, CancellationToken cancellationToken = default)
        {
            RequestedWorld = worldId;
            Action = "evil";
            Crimson = crimson ?? !Crimson;
            return ValueTask.FromResult(new WorldNativeActionResult(WorldNativeActionStatus.Applied, Crimson: Crimson));
        }
        public ValueTask<WorldNativeActionResult> SettleAsync(WorldId worldId, CancellationToken cancellationToken = default)
        {
            RequestedWorld = worldId;
            Action = "settle";
            var result = new WorldNativeActionResult(settling ? WorldNativeActionStatus.AlreadyActive : WorldNativeActionStatus.Applied);
            settling = true;
            return ValueTask.FromResult(result);
        }
        public ValueTask<WorldNativeActionResult> GrowAsync(WorldGrowRequest request, CancellationToken cancellationToken = default)
        {
            GrowRequest = request;
            return ValueTask.FromResult(new WorldNativeActionResult(WorldNativeActionStatus.Applied, Position: request.Coordinates ?? new(100, 103)));
        }
    }

    private sealed class PlayerPrincipal(PlayerSnapshot player, bool admin = true, bool evil = true) : IPlayerCommandPrincipal
    {
        public string Name => player.Name;
        public bool IsServer => false;
        public string CultureSubjectId => Name;
        public PlayerId PlayerId => player.Id;
        public PlayerSessionId? SessionId => player.SessionId;
        public string PlayerName => player.Name;
        public string? AccountName => null;
        public bool HasPermission(string permission) => permission switch
        {
            "vega.regions.bypass" or "vega.world.build.bypass" or "vega.world.spawn.bypass" => admin,
            "vega.world.grow.evil" => evil,
            _ => true
        };
    }

    private sealed class ConsolePrincipal : ICommandPrincipal
    {
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
