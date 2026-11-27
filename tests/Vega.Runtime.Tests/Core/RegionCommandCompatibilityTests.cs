using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Persistence.Sqlite;
using Vega.Regions;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class RegionCommandCompatibilityTests
{
    private string directory = null!;
    private static readonly WorldId Arena = new("region-arena");

    [SetUp]
    public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "vega-region-compat", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task AliasesRenamePersistenceAndDirectionalResizePreserveIdentityAndWorld()
    {
        var db = await OpenAsync();
        var account = (await db.Accounts.CreateAsync("Alice", "fixture-password", "default", "Server")).After!;
        Assert.That((await db.Authorization.CreateGroupAsync("builders", "default", "Server")).Succeeded, Is.True);
        var dispatcher = new CommandDispatcher([new RegionCommand(db.Regions, accounts: db.Accounts, authorization: db.Authorization, environment: new EnvironmentFixture())]);
        var principal = new Principal();
        async Task Run(params string[] args) => Assert.That((await Dispatch(dispatcher, principal, Arena, args)).Status, Is.EqualTo(CommandExecutionStatus.Success), string.Join(' ', args));
        await Run("create", "town square", "10", "15", "20", "25", "1", "Alice");
        RegionSnapshot initial = db.Regions.SnapshotRegions(Arena).Single();
        await Run("allow", "Alice", "town", "square");
        await Run("allowg", "builders", "town square");
        await Run("protect", "town square", "false");
        await Run("z", "town square", "7");
        await Run("resize", "town square", "up", "5");
        await Run("expand", "town square", "left", "2");
        await Run("resize", "town square", "r", "-3");
        await Run("resize", "town square", "d", "4");
        var before = db.Regions.SnapshotRegions(Arena).Single();
        Assert.That((before.X, before.Y, before.Width, before.Height), Is.EqualTo((8, 10, 19, 34)));
        Assert.That((await Dispatch(dispatcher, principal, Arena, ["resize", "town square", "l", "20"])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Dispatch(dispatcher, principal, Arena, ["resize", "town square", "r", "100"])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Dispatch(dispatcher, principal, Arena, ["allow", "missing", "town square"])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That(db.Regions.SnapshotRegions(Arena).Single().Revision, Is.EqualTo(before.Revision));

        // Same name in another world is legal and never changed by a world-local rename.
        Assert.That((await db.Regions.CreateAsync(WorldId.Primary, "plaza", 1, 1, 1, 1, 0, null, "Server")).Succeeded, Is.True);
        await Run("rename", "town square", "plaza");
        var after = db.Regions.SnapshotRegions(Arena).Single();
        Assert.That(after.Id, Is.EqualTo(initial.Id));
        Assert.That(after.Revision, Is.EqualTo(before.Revision + 1));
        Assert.That(after.Owner, Is.EqualTo(account.Name));
        Assert.That(after.Members, Does.Contain(account.Name));
        Assert.That(after.Groups, Does.Contain("builders"));
        Assert.That(after.Flags["protected"], Is.False);
        Assert.That(after.Priority, Is.EqualTo(7));
        Assert.That((await db.Regions.RenameAsync(Arena, "plaza", "stale", before.Revision, "Server")).Code, Is.EqualTo("region.revision-conflict"));
        Assert.That(db.Regions.SnapshotRegions(WorldId.Primary).Single().Name, Is.EqualTo("plaza"));
        await Run("create", "duplicate", "50", "50", "2", "2");
        Assert.That((await Dispatch(dispatcher, principal, Arena, ["rename", "plaza", "duplicate"])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        await Run("info", "plaza");
        await Run("list", "1");
        await Run("name", "10", "20");
        await Run("remove", "Alice", "plaza");
        await Run("removeg", "builders", "plaza");
        await Run("protect", "plaza", "true");
        var reopened = await OpenAsync();
        var stored = reopened.Regions.SnapshotRegions(Arena).Single(region => region.Name == "plaza");
        Assert.That(stored.Id, Is.EqualTo(initial.Id));
        Assert.That(stored.Members, Is.Empty);
        Assert.That(stored.Groups, Is.Empty);
        Assert.That(stored.Flags["protected"], Is.True);
    }

    [Test]
    public async Task SelectionAndTeleportRequireExactSessionWorldAndPermission()
    {
        var db = await OpenAsync();
        var account = (await db.Accounts.CreateAsync("Alice", "fixture-password", "default", "Server")).After!;
        var player = new PlayerSnapshot(new(4), "Alice", false, 100, 100, 20, 20, new(160, 320), false, 0)
        { SessionId = PlayerSessionId.New(), WorldId = Arena };
        var fixture = new PlayerFixture(player, account);
        var dispatcher = new CommandDispatcher([new RegionCommand(db.Regions, fixture, fixture, db.Accounts, db.Authorization, new EnvironmentFixture(), fixture)]);
        var principal = new Principal(player.SessionId, true, true);
        async Task<CommandExecutionResult> Run(params string[] args) => await Dispatch(dispatcher, principal, Arena, args);
        Assert.That((await Run("set", "1")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await Run("set", "2", "20", "30")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await Run("define", "selection")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        var region = db.Regions.SnapshotRegions(Arena).Single();
        Assert.That((region.X, region.Y, region.Width, region.Height), Is.EqualTo((10, 20, 11, 11)));
        Assert.That(region.Owner, Is.EqualTo(account.Name));
        Assert.That((await Run("define", "not-consumed")).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Run("tp", "selection")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Teleports.Single(), Is.EqualTo((player, new PlayerPosition(248, 408))));
        Assert.That((await Dispatch(dispatcher, new Principal(player.SessionId, true, false), Arena, ["tp", "selection"])).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await Dispatch(dispatcher, new Principal(player.SessionId, false, true), Arena, ["tp", "selection"])).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await Dispatch(dispatcher, principal, WorldId.Primary, ["tp", "selection"])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Run("set", "1")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await Run("set", "2", "20", "21")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        fixture.Player = player with { SessionId = PlayerSessionId.New() };
        Assert.That((await Run("define", "stale session")).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Run("tp", "selection")).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That(fixture.Teleports, Has.Count.EqualTo(1));
        var newer = new Principal(fixture.Player.SessionId, true, true);
        Assert.That((await Dispatch(dispatcher, newer, Arena, ["define", "new session"])).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That(db.Regions.SnapshotRegions(Arena), Has.Count.EqualTo(1));
    }

    private Task<SqliteVegaDatabase> OpenAsync() => SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(directory, "regions.db"))).AsTask();

    private static ValueTask<CommandExecutionResult> Dispatch(CommandDispatcher dispatcher, Principal principal, WorldId world, string[] args) =>
        dispatcher.DispatchAsync(new CommandInvocation("region", args, principal, new NullOutput()) { WorldId = world });

    private sealed class Principal(PlayerSessionId? session = null, bool manage = true, bool teleport = true) : IPlayerCommandPrincipal
    {
        public string Name => session.HasValue ? "Alice" : "Server";
        public bool IsServer => !session.HasValue;
        public bool HasPermission(string permission) => permission == "vega.regions.manage" ? manage : permission == "vega.navigation.tp" && teleport;
        public string CultureSubjectId => Name;
        public PlayerId PlayerId => new(4);
        public PlayerSessionId? SessionId => session;
        public string PlayerName => Name;
        public string? AccountName => session.HasValue ? "alice" : null;
    }

    private sealed class NullOutput : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class EnvironmentFixture : IWorldEnvironmentService
    {
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(WorldId worldId, CancellationToken cancellationToken = default) => ValueTask.FromResult<WorldEnvironmentSnapshot?>(
            new(worldId, 100, 100, 40, 40, true, 0, 0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow));
        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(WorldEnvironmentMutationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PlayerFixture(PlayerSnapshot player, AccountSnapshot account) : IPlayerDirectory, IPlayerNavigation, IPlayerAccountSessionService
    {
        private readonly Guid authentication = Guid.NewGuid();
        public PlayerSnapshot Player { get; set; } = player;
        public List<(PlayerSnapshot, PlayerPosition)> Teleports { get; } = [];
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [Player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? result) { result = Player.Id == id ? Player : null; return result is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? result) { result = Player.Name == name ? Player : null; return result is not null; }
        public bool TryGetAccount(PlayerId id, out AccountSnapshot? result) { result = id == Player.Id ? account : null; return result is not null; }
        public bool TryGetBinding(PlayerSnapshot candidate, out PlayerAccountBinding binding)
        {
            binding = new(Player.Id, Player.SessionId!.Value, account.Id, authentication);
            return candidate == Player;
        }
        public ValueTask<bool> SetCultureAsync(PlayerId id, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask<PlayerOperationResult> ToPositionAsync(PlayerSnapshot candidate, PlayerPosition destination, CancellationToken cancellationToken = default)
        {
            if (candidate != Player) return ValueTask.FromResult(PlayerOperationResult.NotFound());
            Teleports.Add((candidate, destination));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
        public ValueTask<PlayerOperationResult> ToPlayerAsync(PlayerSnapshot player, PlayerSnapshot destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> ToWorldSpawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> ToHomeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
