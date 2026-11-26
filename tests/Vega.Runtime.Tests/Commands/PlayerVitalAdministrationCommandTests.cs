using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Modules.Administration;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class PlayerVitalAdministrationCommandTests
{
    private static readonly WorldId World = new("test");

    [Test]
    public async Task KillAndSlapUseCurrentWorldSessionAndRespectDamagePermission()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new KillCommand(fixture, fixture), new SlapCommand(fixture, fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("kill", ["Other"]))).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("kill", ["Dead Player"]))).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("kill", ["Alice"], killPermission: false))).Status,
            Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That(fixture.Last, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("slay", ["Alice"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("kill", fixture.Alice, 0)));

        Assert.That((await dispatcher.DispatchAsync(fixture.Command("slap", ["1", "32768"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("slap", ["1", "-1"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("slap", ["1", "100"], killPermission: false))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("slap", fixture.Alice, 15)));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("slap", ["Alice", "100"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("slap", fixture.Alice, 100)));
    }

    [Test]
    public async Task RespawnRequiresDeadTargetAndExplicitOthersPermission()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new RespawnCommand(fixture, fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("respawn", []))).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("respawn", ["Dead", "Player"], otherPermission: false))).Status,
            Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That(fixture.Last, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("respawn", ["Dead", "Player"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("respawn", fixture.Dead, 0)));
        var self = fixture.Command("respawn", [], otherPermission: false) with { Principal = new Principal(fixture.Dead) };
        Assert.That((await dispatcher.DispatchAsync(self)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        var stale = self with { Principal = new Principal(fixture.Dead with { SessionId = PlayerSessionId.New() }) };
        Assert.That((await dispatcher.DispatchAsync(stale)).Status, Is.EqualTo(CommandExecutionStatus.Failed));
    }

    [Test]
    public async Task AreaSyncIsBoundedAndUsesTheOriginalTargetSession()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new SyncCommand(fixture, fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sync", ["17"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sync", ["Dead Player"], otherPermission: false))).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sync", ["Other", "10"]))).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That(fixture.Last, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sync", ["16"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("sync", fixture.Alice, 16)));
    }

    [Test]
    public async Task CosmeticEffectsAreBoundedUniqueAndWorldScoped()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new AnnoyCommand(fixture, fixture), new FireworkCommand(fixture, fixture), new SetupCommand()]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("annoy", ["Alice", "31"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("annoy", ["Other", "5"]))).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("firework", ["Alice", "unknown"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(fixture.Last, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("annoy", ["Alice", "30"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("annoy", fixture.Alice, 30)));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("firework", ["1", "b2"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("firework", fixture.Alice, (int)FireworkStyle.Rings)));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("setup", []))).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
    }

    [Test]
    public async Task GodModeUsesNativeTargetAndRocketReportsUnavailableNativeSsc()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new GodModeCommand(fixture, fixture),
            new RocketCommand(fixture, (player, _) => ValueTask.FromResult(PlayerOperationResult.Failed("rocket.native_ssc_required")))]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("godmode", ["Dead Player", "on"], otherPermission: false))).Status,
            Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("godmode", ["Other", "on"]))).Status,
            Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("god", ["off"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("godmode", fixture.Alice, 0)));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("godmode", ["Dead Player", "on"]))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo(("godmode", fixture.Dead, 1)));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("rocket", ["Alice"]))).ErrorCode, Is.EqualTo("rocket.native_ssc_required"));
    }

    private sealed class Fixture : IPlayerDirectory, IPlayerVitalOperations, IPlayerOperations, IPlayerVisualOperations, IPlayerGodModeOperations
    {
        public PlayerSnapshot Alice { get; } = Player(1, "Alice", World, false);
        public PlayerSnapshot Dead { get; } = Player(2, "Dead Player", World, true);
        private readonly PlayerSnapshot other = Player(3, "Other", new("another"), false);
        public (string Operation, PlayerSnapshot Target, int Damage)? Last { get; private set; }

        public CommandInvocation Command(string name, string[] args, bool killPermission = true, bool otherPermission = true) =>
            new(name, args, new Principal(Alice, killPermission, otherPermission), new Output()) { WorldId = World };
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [Alice, Dead, other];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = SnapshotOnlinePlayers().FirstOrDefault(p => p.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = SnapshotOnlinePlayers().FirstOrDefault(p => p.Name == name)) is not null;
        public ValueTask<PlayerOperationResult> HealAsync(PlayerSnapshot player, int amount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> BuffAsync(PlayerSnapshot player, string buff, int seconds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> KillAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) => Record("kill", player, 0);
        public ValueTask<PlayerOperationResult> SlapAsync(PlayerSnapshot player, int damage, CancellationToken cancellationToken = default) => Record("slap", player, damage);
        public ValueTask<PlayerOperationResult> RespawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) => Record("respawn", player, 0);
        public ValueTask<PlayerOperationResult> AnnoyAsync(PlayerSnapshot player, int seconds, CancellationToken cancellationToken = default) => Record("annoy", player, seconds);
        public ValueTask<PlayerOperationResult> FireworkAsync(PlayerSnapshot player, FireworkStyle style, CancellationToken cancellationToken = default) => Record("firework", player, (int)style);
        public async ValueTask<(PlayerOperationResult Result, bool Enabled)> SetGodModeAsync(PlayerSnapshot player, bool? enabled, CancellationToken cancellationToken = default) =>
            (await Record("godmode", player, enabled == false ? 0 : 1), enabled ?? true);
        public ValueTask<PlayerOperationResult> ResynchronizeAreaAsync(PlayerSnapshot player, int radius, CancellationToken cancellationToken = default) => Record("sync", player, radius);
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private ValueTask<PlayerOperationResult> Record(string operation, PlayerSnapshot player, int damage)
        {
            Last = (operation, player, damage);
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private static PlayerSnapshot Player(int slot, string name, WorldId world, bool dead) => new(new(slot), name, dead,
        dead ? 0 : 100, 100, 20, 20, new(1600, 1600), false, 0) { SessionId = PlayerSessionId.New(), WorldId = world };

    private sealed class Principal(PlayerSnapshot player, bool killPermission = true, bool otherPermission = true) : IPlayerCommandPrincipal
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
            "vega.players.kill" => killPermission,
            "vega.players.respawn.others" or "vega.world.sync.others" or "vega.players.godmode.others" => otherPermission,
            _ => true
        };
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
