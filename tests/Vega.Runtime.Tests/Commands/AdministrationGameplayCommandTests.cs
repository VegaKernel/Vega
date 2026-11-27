using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Modules.Administration;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class AdministrationGameplayCommandTests
{
    private static readonly WorldId World = new("test");

    [Test]
    public async Task NamedPrefixAndLegacyClearSelectorReuseExistingOperations()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new ItemCommand(fixture, fixture, resolvePrefix: name => name == "Legendary" ? 81 : null),
            new ClearCommand(fixture, fixture, fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("i", ["Sword", "1", "Legendary"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo("item:Alice:Sword:1:81"));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("i", ["Sword", "1", "unknown"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("clear", ["proj"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Removal?.Projectiles, Is.EqualTo(new[] { new WorldProjectileCleanupHandle(2, 20) }));
    }

    [Test]
    public async Task ItemHealAndBuffUseSessionBoundTargetsAndValidatedArguments()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new ItemCommand(fixture, fixture), new ItemCommand(fixture, fixture, true),
            new HealCommand(fixture, fixture), new BuffCommand(fixture, fixture), new BuffCommand(fixture, fixture, true)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("i", ["Torch", "20", "0"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo("item:Alice:Torch:20:0"));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("give", ["Torch", "OtherWorld"]))).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("give", ["Torch", "Bob", "0"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("i", ["Blue", "Torch", "20"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo("item:Alice:Blue Torch:20:0"));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("give", ["Torch", "Bob", "4"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo("item:Bob:Torch:4:0"));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("heal", ["Bob", "100"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo("heal:Bob:100"));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("buffplayer", ["Bob", "Regeneration", "60"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Last, Is.EqualTo("buff:Bob:Regeneration:60"));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("buff", ["Regeneration", "3601"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
    }

    [Test]
    public async Task SpawnCountAndClearAreBoundedAndScopedToCurrentWorld()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new SpawnMobCommand(fixture, fixture, fixture), new ClearCommand(fixture, fixture, fixture),
            new BroadcastCommand(fixture, fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sm", ["Slime", "21"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sm", ["Slime", "3"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Spawns, Has.Count.EqualTo(3));
        Assert.That(fixture.Spawns.All(s => s.WorldId == World && s.Type == 1), Is.True);
        Assert.That(fixture.Spawns.All(s => s.TargetPlayer?.SessionId == fixture.Alice.SessionId && !s.Boss), Is.True);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("clear", ["projectile"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Removal?.Projectiles, Is.EqualTo(new[] { new WorldProjectileCleanupHandle(2, 20) }));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("broadcast", ["Hello"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Messages, Is.EquivalentTo(new[] { new PlayerId(1), new PlayerId(2) }));
    }

    [Test]
    public async Task BossAndNpcAdministrationAreScopedAndRequireExplicitBossPermission()
    {
        var fixture = new Fixture();
        var dispatcher = new CommandDispatcher([new SpawnMobCommand(fixture, fixture, fixture, boss: true),
            new ButcherCommand(fixture, fixture), new RenameNpcCommand(fixture, fixture)]);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sb", ["Slime"]))).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sb", ["eye", "6"]))).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(fixture.Spawns, Is.Empty);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("sb", ["eye"]))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Spawns.Single(), Is.EqualTo(new NpcSpawnRequest(World, 4, fixture.Alice.Position, "Alice", 1)
            { TargetPlayer = fixture.Alice, Boss = true }));
        var stale = fixture.Command("sb", ["eye"]) with { Principal = new Principal(fixture.Alice with { SessionId = PlayerSessionId.New() }) };
        Assert.That((await dispatcher.DispatchAsync(stale)).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That(fixture.Spawns, Has.Count.EqualTo(1));

        Assert.That((await dispatcher.DispatchAsync(fixture.Command("butcher", []))).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Butchered, Is.EqualTo(new NpcButcherRequest(World, null, false, "Alice")));
        var bossButcher = fixture.Command("butcher", ["4"]);
        Assert.That((await dispatcher.DispatchAsync(bossButcher with { Principal = new Principal(fixture.Alice, false) })).Status,
            Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await dispatcher.DispatchAsync(bossButcher)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Butchered, Is.EqualTo(new NpcButcherRequest(World, 4, true, "Alice")));

        Assert.That((await dispatcher.DispatchAsync(fixture.Command("renamenpc", ["Guide", new string('x', 201)]))).Status,
            Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That(fixture.Renamed, Is.Null);
        Assert.That((await dispatcher.DispatchAsync(fixture.Command("renamenpc", ["Guide", "Test Guide"]))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Renamed, Is.EqualTo(new NpcRenameRequest(World, 22, "Test Guide", "Alice")));
    }

    private sealed class Fixture : IPlayerDirectory, IPlayerItemDelivery, IPlayerVitalOperations, IWorldNpcCatalog,
        IWorldNpcService, IWorldEntityMaintenance, IGameThreadDispatcher, IPlayerMessenger
    {
        private readonly PlayerSnapshot alice = Player(1, "Alice", World);
        private readonly PlayerSnapshot bob = Player(2, "Bob", World);
        private readonly PlayerSnapshot other = Player(3, "OtherWorld", new("other"));
        public PlayerSnapshot Alice => alice;
        public string? Last { get; private set; }
        public List<NpcSpawnRequest> Spawns { get; } = [];
        public NpcButcherRequest? Butchered { get; private set; }
        public NpcRenameRequest? Renamed { get; private set; }
        public WorldEntityRemovalRequest? Removal { get; private set; }
        public List<PlayerId> Messages { get; } = [];
        public CommandInvocation Command(string name, string[] args) => new(name, args, new Principal(alice), new Output()) { WorldId = World };
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [alice, bob, other];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = SnapshotOnlinePlayers().FirstOrDefault(p => p.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = SnapshotOnlinePlayers().FirstOrDefault(p => p.Name == name)) is not null;
        public ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string item, int stack, int prefix, CancellationToken cancellationToken = default)
        { Last = $"item:{player.Name}:{item}:{stack}:{prefix}"; return ValueTask.FromResult(PlayerOperationResult.Applied()); }
        public ValueTask<PlayerOperationResult> HealAsync(PlayerSnapshot player, int amount, CancellationToken cancellationToken = default)
        { Last = $"heal:{player.Name}:{amount}"; return ValueTask.FromResult(PlayerOperationResult.Applied()); }
        public ValueTask<PlayerOperationResult> BuffAsync(PlayerSnapshot player, string buff, int seconds, CancellationToken cancellationToken = default)
        { Last = $"buff:{player.Name}:{buff}:{seconds}"; return ValueTask.FromResult(PlayerOperationResult.Applied()); }
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerId id, string message, CancellationToken cancellationToken = default)
        { Messages.Add(id); return ValueTask.FromResult(PlayerOperationResult.Applied()); }
        public ValueTask<IReadOnlyList<NpcTypeDefinition>> FindTypesAsync(string query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<NpcTypeDefinition>>(query switch
            {
                "4" => [new(4, "Eye of Cthulhu", true)],
                "Guide" => [new(22, "Guide")],
                "Slime" => [new(1, "Slime")],
                _ => []
            });
        public ValueTask<NpcOperationResult> SpawnAsync(NpcSpawnRequest request, CancellationToken cancellationToken = default)
        { Spawns.Add(request); return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied)); }
        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(WorldId worldId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>([]);
        public ValueTask<NpcSnapshot?> CaptureAsync(NpcHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NpcOperationResult> TeleportAsync(NpcTeleportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NpcOperationResult> DespawnAsync(NpcDespawnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NpcBatchOperationResult> ButcherAsync(NpcButcherRequest request, CancellationToken cancellationToken = default)
        { Butchered = request; return ValueTask.FromResult(new NpcBatchOperationResult(NpcOperationStatus.Applied, 1)); }
        public ValueTask<NpcBatchOperationResult> RenameAsync(NpcRenameRequest request, CancellationToken cancellationToken = default)
        { Renamed = request; return ValueTask.FromResult(new NpcBatchOperationResult(NpcOperationStatus.Applied, 1)); }
        public WorldEntityCleanupSnapshot Snapshot(WorldId worldId) => new(worldId, [],
            [new(new(1, 10), true, false), new(new(2, 20), false, false)], DateTimeOffset.UtcNow);
        public WorldEntityRemovalResult Remove(WorldEntityRemovalRequest request)
        { Removal = request; return new(0, request.Projectiles.Count, 0, 0); }
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { action(); return true; }
        public bool TrySchedule(string name, Func<bool> step) { step(); return true; }
    }

    private static PlayerSnapshot Player(int id, string name, WorldId world) => new(new(id), name, false, 100, 100, 20, 20, new(1600, 1600), false, 0)
        { SessionId = PlayerSessionId.New(), WorldId = world };
    private sealed class Principal(PlayerSnapshot player, bool bossPermission = true) : IPlayerCommandPrincipal
    {
        public string Name => player.Name; public bool IsServer => false; public string CultureSubjectId => Name;
        public PlayerId PlayerId => player.Id; public PlayerSessionId? SessionId => player.SessionId;
        public string PlayerName => player.Name; public string? AccountName => null;
        public bool HasPermission(string permission) => permission != "vega.world.butcher.boss" || bossPermission;
    }
    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
