using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Modules.Navigation;
using Vega.Persistence.Sqlite;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class HomeCommandTests
{
    private string root = null!;
    [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "vega-home-tests", Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Test]
    public async Task HomesCommandsPersistAndUseQuotaAuthenticationAndPhysicalWorld()
    {
        var options = new SqliteVegaDatabaseOptions(Path.Combine(root, "test.db"));
        var db = await SqliteVegaDatabase.OpenAsync(options);
        var group = await db.Authorization.CreateGroupAsync("homes", "default", "server");
        await db.Authorization.SetPermissionAsync("homes", "homes.set.2", PermissionEffect.Grant, group.After!.Revision, "server");
        var created = await db.Accounts.CreateAsync("alice", "fixture-password", "homes", "server");
        var bridge = new Bridge(created.After!);
        var commands = new CommandDispatcher(Enum.GetValues<HomeCommandAction>().Select(action => new HomeCommand(action, bridge, bridge, bridge, bridge, db.Authorization, db.Homes)));
        ValueTask<CommandExecutionResult> Run(string name, params string[] args) => commands.DispatchAsync(new(name, args, bridge, bridge) { WorldId = bridge.Player.WorldId });
        Assert.That((await Run("sethome")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await Run("sethome", "base")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await Run("sethome", "third")).ErrorCode, Is.EqualTo("homes.limit"));
        bridge.Player = bridge.Player with { Position = new(2500, 2100) };
        Assert.That((await Run("sethome", "BASE")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        var list = await Run("myhome", "-l");
        Assert.That(list.Messages!.Single().Arguments, Does.Contain("BASE, home"));
        var reopened = await SqliteVegaDatabase.OpenAsync(options);
        Assert.That((await reopened.Homes.FindAsync(created.After!.Id, bridge.Player.WorldId, bridge.WorldFile, "base"))!.Position, Is.EqualTo(new PlayerPosition(2500, 2100)));
        Assert.That((await Run("myhome", "base")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(bridge.Teleported, Is.EqualTo(new PlayerPosition(2500, 2100)));
        Guid previousWorld = bridge.WorldFile;
        bridge.WorldFile = Guid.NewGuid();
        Assert.That((await Run("myhome", "base")).ErrorCode, Is.EqualTo("homes.not_found"));
        bridge.WorldFile = previousWorld;
        bridge.InvalidateOnTeleport = true;
        Assert.That((await Run("myhome", "base")).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Run("delhome", "base")).ErrorCode, Is.EqualTo("homes.login"));
    }

    [Test]
    public async Task ConcurrentQuotaAndAccountIsolationAreDurable()
    {
        var db = await SqliteVegaDatabase.OpenAsync(new(Path.Combine(root, "test.db")));
        var first = (await db.Accounts.CreateAsync("first", "fixture-password", "default", "server")).After!;
        var second = (await db.Accounts.CreateAsync("second", "fixture-password", "default", "server")).After!;
        Guid file = Guid.NewGuid();
        var home = new PlayerHome(first.Id, WorldId.Primary, file, "a", new(1000, 1000));
        var writes = await Task.WhenAll(Task.Run(async () => await db.Homes.SetAsync(home, 1)), Task.Run(async () => await db.Homes.SetAsync(home with { Name = "b" }, 1)));
        Assert.That(writes, Is.EquivalentTo(new[] { HomeWriteResult.Applied, HomeWriteResult.LimitReached }));
        Assert.That(await db.Homes.ListAsync(first.Id, WorldId.Primary, file), Has.Count.EqualTo(1));
        Assert.That(await db.Homes.ListAsync(second.Id, WorldId.Primary, file), Is.Empty);
        Assert.That(await db.Homes.ListAsync(first.Id, new("sandbox"), file), Is.Empty);
        Assert.That(await db.Homes.SetAsync(home with { AccountId = second.Id }, 1), Is.EqualTo(HomeWriteResult.Applied));
        await db.Accounts.DeleteAsync(first.Name, first.Revision, "server");
        Assert.That(await db.Homes.ListAsync(first.Id, WorldId.Primary, file), Is.Empty);
        Assert.That(await db.Homes.ListAsync(second.Id, WorldId.Primary, file), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task LegacyHomesImportFiltersWorldAndDoesNotOverwriteExistingPoints()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.db");
        var db = await SqliteVegaDatabase.OpenAsync(new(Path.Combine(root, "target.db")));
        var account = (await db.Accounts.CreateAsync("alice", "fixture-password", "default", "server")).After!;
        await using (var connection = new SqliteConnection($"Data Source={source};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Homes (AccountId INT, Name TEXT, X REAL, Y REAL, WorldID INT); INSERT INTO Homes VALUES (@account,'base',1000.5,1400,12),(@account,'other',2000,2100,13);";
            command.Parameters.AddWithValue("@account", account.Id.Value);
            await command.ExecuteNonQueryAsync();
        }
        Guid file = Guid.NewGuid();
        var points = TShockHomesMigration.Read(TShockMigrationSource.Parse(source), 12, WorldId.Primary, file);
        Assert.That(points, Has.Count.EqualTo(1));
        Assert.That(await db.ImportHomesAsync(WorldId.Primary, file, points), Is.EqualTo(1));
        Assert.That((await db.Homes.FindAsync(account.Id, WorldId.Primary, file, "base"))!.Position, Is.EqualTo(new PlayerPosition(1000.5f, 1400)));
        Assert.ThrowsAsync<InvalidDataException>(async () => await db.ImportHomesAsync(WorldId.Primary, file, points));
    }

    private sealed class Bridge(AccountSnapshot account) : IPlayerDirectory, IPlayerNavigation, IPlayerAccountSessionService,
        IWorldEnvironmentService, IPlayerCommandPrincipal, ICommandOutput
    {
        public PlayerSnapshot Player { get; set; } = new(new(0), account.Name, false, 100, 100, 20, 20, new(1600, 1600), false, 0) { SessionId = PlayerSessionId.New() };
        public Guid WorldFile { get; set; } = Guid.NewGuid();
        public PlayerPosition? Teleported { get; private set; }
        public bool InvalidateOnTeleport { get; set; }
        private bool authenticated = true;
        private readonly Guid authenticationId = Guid.NewGuid();
        public string Name => account.Name;
        public bool IsServer => false;
        public string CultureSubjectId => Name;
        public PlayerId PlayerId => Player.Id;
        public PlayerSessionId? SessionId => Player.SessionId;
        public string PlayerName => Name;
        public string? AccountName => authenticated ? account.Name : null;
        public bool HasPermission(string permission) => permission is "homes.set" or "homes.set.2" or "homes.tp" or "homes.delete";
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [Player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = id == Player.Id ? Player : null) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = name == Player.Name ? Player : null) is not null;
        public bool TryGetAccount(PlayerId id, out AccountSnapshot? current) => (current = authenticated && id == Player.Id ? account : null) is not null;
        public bool TryGetBinding(PlayerSnapshot player, out PlayerAccountBinding binding)
        {
            binding = new(player.Id, player.SessionId!.Value, account.Id, authenticationId);
            return authenticated && player.SessionId == Player.SessionId;
        }
        public bool TryRunWithBinding<T>(PlayerAccountBinding binding, Func<T> operation, out T value)
        {
            value = default!;
            if (!authenticated || binding.AccountId != account.Id || binding.AuthenticationId != authenticationId) return false;
            value = operation(); return true;
        }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(WorldId worldId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(new(worldId, 4200, 1200, 100, 100, true, 0, 0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow) { FileIdentity = WorldFile });
        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(WorldEnvironmentMutationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> ToAuthenticatedPositionAsync(PlayerSnapshot player, PlayerPosition destination, Guid worldFileId,
            IPlayerAccountSessionService accounts, PlayerAccountBinding binding, CancellationToken cancellationToken = default)
        {
            if (InvalidateOnTeleport) authenticated = false;
            return ValueTask.FromResult(worldFileId == WorldFile && TryRunWithBinding(binding, () => { Teleported = destination; return PlayerOperationResult.Applied(); }, out var result) ? result : PlayerOperationResult.NotFound());
        }
        public ValueTask<PlayerOperationResult> ToPositionAsync(PlayerSnapshot player, PlayerPosition destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> ToPlayerAsync(PlayerSnapshot player, PlayerSnapshot destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> ToWorldSpawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> ToHomeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
