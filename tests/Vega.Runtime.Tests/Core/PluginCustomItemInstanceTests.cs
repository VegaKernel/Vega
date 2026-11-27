using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Items;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Modules;
using Vega.Persistence.Sqlite;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginCustomItemInstanceTests
{
    private string root = null!;
    private SqliteVegaDatabaseOptions options = null!;

    [SetUp]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "vega-item-instances", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        options = new SqliteVegaDatabaseOptions(Path.Combine(root, "vega.db"));
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public async Task ImmutableOwnershipSurvivesReloadAndConflictingTransfersAndRevocationCannotResurrect()
    {
        var db = await SqliteVegaDatabase.OpenAsync(options);
        var host = new Sessions();
        var alice = host.Login(1, 101);
        var bob = host.Login(2, 102);
        var charlie = host.Login(3, 103);
        var id = Guid.NewGuid();
        var definition = new CustomItemRecipe("sword", 24) { WorldStats = new() { Damage = 45, UseTime = 17 } };
        await using (var first = Scope(host, db.PluginData))
        {
            first.Players.Actions.CustomItems.Register(definition);
            var instances = first.Players.Actions.CustomItems.Instances;
            Assert.That(instances.IsAvailable, Is.True);
            var issued = await instances.IssueAsync(alice, id, "sword", 1);
            Assert.That(issued.Status, Is.EqualTo(CustomItemInstanceStatus.Applied));
            Assert.That(issued.Snapshot!.Revision, Is.EqualTo(1));
            Assert.That((await instances.IssueAsync(alice, id, "sword", 1)).Status, Is.EqualTo(CustomItemInstanceStatus.AlreadyIssued));
            Assert.That((await instances.IssueAsync(bob, id, "sword", 1)).Status, Is.EqualTo(CustomItemInstanceStatus.Conflict));
            Assert.That(await instances.FindOwnedAsync(bob, id), Is.Null);
            Assert.Throws<ArgumentException>(() => first.Data.ReadAsync("VEGA.CUSTOM-ITEM-INSTANCES", id.ToString("N")));
            Assert.Throws<ArgumentException>(() => first.Data.CommitAsync([new("vega.custom-item-instances", id.ToString("N"), 1, null)]));
            await using var foreign = Scope(host, db.PluginData, "other-plugin");
            Assert.That(await foreign.Players.Actions.CustomItems.Instances.FindAsync(id), Is.Null);
        }
        var reopened = await SqliteVegaDatabase.OpenAsync(options);
        await using var reloaded = Scope(host, reopened.PluginData);
        var recipes = reloaded.Players.Actions.CustomItems;
        recipes.Register(definition with { WorldStats = new() { Damage = 90, UseTime = 17 } });
        var ledger = recipes.Instances;
        var persisted = (await ledger.FindAsync(id))!;
        Assert.That(persisted.Instance.Definition, Is.EqualTo(definition));
        Assert.That((await ledger.IssueAsync(alice, id, "sword", 2)).Status, Is.EqualTo(CustomItemInstanceStatus.Conflict));

        var competing = await Task.WhenAll(ledger.TransferAsync(id, persisted.Revision, alice, bob).AsTask(),
            ledger.TransferAsync(id, persisted.Revision, alice, charlie).AsTask());
        Assert.That(competing.Count(result => result.Status == CustomItemInstanceStatus.Applied), Is.EqualTo(1));
        Assert.That(competing.Count(result => result.Status == CustomItemInstanceStatus.Conflict), Is.EqualTo(1));
        var moved = (await ledger.FindAsync(id))!;
        var recipient = moved.Instance.Owner.AccountId.Value == 102 ? bob : charlie;
        Assert.That(await ledger.FindOwnedAsync(alice, id), Is.Null);
        Assert.That(await ledger.FindOwnedAsync(recipient, id), Is.EqualTo(moved));
        Assert.That((await ledger.TransferAsync(id, persisted.Revision, recipient, alice)).Status, Is.EqualTo(CustomItemInstanceStatus.Conflict));

        // A newly created account cannot inherit this ownership merely by reusing a numeric ID.
        var replacement = host.Login(recipient.Id.Slot, moved.Instance.Owner.AccountId.Value, moved.Instance.Owner.RegisteredUtc.AddSeconds(1));
        Assert.That(await ledger.FindOwnedAsync(replacement, id), Is.Null);
        Assert.That((await ledger.RevokeAsync(id, moved.Revision)).Status, Is.EqualTo(CustomItemInstanceStatus.Applied));
        Assert.That((await ledger.IssueAsync(alice, id, "sword", 2)).Status, Is.EqualTo(CustomItemInstanceStatus.Revoked));
        Assert.That((await ledger.ListAsync()).Single().Instance.Revoked, Is.True);
        Assert.That(await ledger.ListAsync(afterId: id), Is.Empty);
        Assert.That(host.NativeDeliveryAttempts, Is.Zero, "Virtual ownership must not create or alter any Terraria item.");
        await reloaded.DisposeAsync();
        Assert.That(ledger.IsAvailable, Is.False);
        Assert.That((await ledger.IssueAsync(alice, Guid.NewGuid(), "sword", 2)).Status, Is.EqualTo(CustomItemInstanceStatus.Unavailable));
    }

    [Test]
    public async Task ReauthenticationDuringDatabaseReadRejectsStaleOwnershipMutation()
    {
        var db = await SqliteVegaDatabase.OpenAsync(options);
        var host = new Sessions();
        var alice = host.Login(1, 101);
        var gate = new BlockingRead(db.PluginData);
        await using var scope = Scope(host, gate);
        scope.Players.Actions.CustomItems.Register(new("sword", 24));
        var ledger = scope.Players.Actions.CustomItems.Instances;
        var id = Guid.NewGuid();
        var pending = ledger.IssueAsync(alice, id, "sword", 1).AsTask();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Reauthenticate(alice); // same slot, same native session and same account, new authentication tenure
        gate.Release.TrySetResult();
        Assert.That((await pending).Status, Is.EqualTo(CustomItemInstanceStatus.NotAuthenticated));
        Assert.That(await ledger.FindAsync(id), Is.Null);
        Assert.That(gate.Commits, Is.Zero);
    }

    private HotModuleRegistrationScope Scope(Sessions host, IPluginDocumentStore storage, string name = "fixture-items") =>
        new(new CommandDispatcher([]), new NullLogger(), name, root, playerDirectory: host, playerOperations: host,
            activityWorldId: WorldId.Primary, authentication: host, documentStore: storage);

    private sealed class Sessions : IPlayerDirectory, IPlayerOperations, IPlayerItemDelivery, IPlayerCustomWorldItemDelivery, IPlayerAccountSessionService
    {
        private readonly object sync = new();
        private readonly Dictionary<PlayerId, (PlayerSnapshot Player, AccountSnapshot Account, Guid Authentication)> current = [];
        public int NativeDeliveryAttempts { get; private set; }

        public PlayerSnapshot Login(int slot, int id, DateTimeOffset? registered = null)
        {
            var player = new PlayerSnapshot(new(slot), $"fixture-{slot}", false, 100, 100, 20, 20, new(0, 0), false, 0)
                { SessionId = PlayerSessionId.New(), WorldId = WorldId.Primary };
            lock (sync) current[player.Id] = (player, new AccountSnapshot(new(id), player.Name, "default", null)
                { RegisteredUtc = registered ?? DateTimeOffset.UnixEpoch.AddSeconds(id) }, Guid.NewGuid());
            return player;
        }

        public void Reauthenticate(PlayerSnapshot player)
        {
            lock (sync) { var old = current[player.Id]; current[player.Id] = (old.Player, old.Account, Guid.NewGuid()); }
        }
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() { lock (sync) return current.Values.Select(row => row.Player).ToArray(); }
        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        { lock (sync) { player = current.TryGetValue(id, out var row) ? row.Player : null; return player is not null; } }
        public bool TryFindExact(string name, out PlayerSnapshot? player)
        { player = SnapshotOnlinePlayers().FirstOrDefault(p => p.Name == name); return player is not null; }
        public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account)
        { lock (sync) { account = current.TryGetValue(playerId, out var row) ? row.Account : null; return account is not null; } }
        public bool TryGetBinding(PlayerSnapshot player, out PlayerAccountBinding binding)
        {
            lock (sync)
            {
                binding = default;
                if (!current.TryGetValue(player.Id, out var row) || row.Player.SessionId != player.SessionId || row.Player.WorldId != player.WorldId) return false;
                binding = new(player.Id, player.SessionId!.Value, row.Account.Id, row.Authentication);
                return true;
            }
        }
        public bool TryRunWithBinding<T>(PlayerAccountBinding binding, Func<T> operation, out T result)
        {
            lock (sync)
            {
                result = default!;
                if (!current.TryGetValue(binding.PlayerId, out var row) || row.Player.SessionId != binding.SessionId ||
                    row.Account.Id != binding.AccountId || row.Authentication != binding.AuthenticationId) return false;
                result = operation(); return true;
            }
        }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string item, int stack, int prefix, CancellationToken cancellationToken = default)
        { NativeDeliveryAttempts++; return ValueTask.FromResult(PlayerOperationResult.Applied()); }
        public ValueTask<PlayerOperationResult> GiveWorldItemAsync(PlayerSnapshot player, int itemType, int stack, int prefix, CustomWorldItemStats stats, CancellationToken cancellationToken = default)
        { NativeDeliveryAttempts++; return ValueTask.FromResult(PlayerOperationResult.Applied()); }
    }

    private sealed class BlockingRead(IPluginDocumentStore inner) : IPluginDocumentStore
    {
        private int first;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Commits { get; private set; }
        public async ValueTask<PluginDocument?> ReadAsync(string owner, string collection, string key, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            return await inner.ReadAsync(owner, collection, key, cancellationToken).ConfigureAwait(false);
        }
        public ValueTask<IReadOnlyList<PluginDocument>> ListAsync(string owner, string collection, string? afterKey, int limit, CancellationToken cancellationToken = default) =>
            inner.ListAsync(owner, collection, afterKey, limit, cancellationToken);
        public ValueTask<bool> CommitAsync(string owner, IReadOnlyList<PluginDocumentMutation> mutations, CancellationToken cancellationToken = default)
        { Commits++; return inner.CommitAsync(owner, mutations, cancellationToken); }
    }

    private sealed class NullLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
}
