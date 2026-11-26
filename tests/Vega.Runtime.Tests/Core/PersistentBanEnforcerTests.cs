using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Bans;
using Vega.Core.Commands.Administration;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PersistentBanEnforcerTests
{
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "Vega.PersistentBan.Tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Test]
    public async Task IndependentWorldEnforcersObserveSharedPersistentBanWritesWithoutPerPlayerQueries()
    {
        var main = await OpenAsync();
        var sandbox = await OpenAsync();
        var mainRepo = new CountingReplica(main.GlobalBans);
        var sandboxRepo = new CountingReplica(sandbox.GlobalBans);
        var uuid = Guid.NewGuid().ToString("D");
        var first = Player(0, WorldId.Primary, "192.0.2.4");
        var second = Player(0, new WorldId("sandbox"), "192.0.2.5");
        var third = Player(1, second.WorldId, "198.51.100.17", uuid);
        var unaffected = Player(2, second.WorldId, "198.51.100.18", transport: "192.0.2.6");
        var mainPlayers = new DirectoryStub(first);
        var sandboxPlayers = new DirectoryStub(second, third, unaffected);
        var mainOps = new RecordingOperations();
        var sandboxOps = new RecordingOperations();
        using var mainEnforcer = Create(mainRepo, mainPlayers, mainOps, new InlineDispatcher());
        using var sandboxEnforcer = Create(sandboxRepo, sandboxPlayers, sandboxOps, new InlineDispatcher());

        await mainEnforcer.ScanOnceAsync();
        await sandboxEnforcer.ScanOnceAsync();
        Assert.That(mainOps.Kicked.Concat(sandboxOps.Kicked), Is.Empty);

        // Independent SQLite connections/repositories: a primary-world command writes these after both players joined.
        var writer = new GlobalBanService(main.GlobalBans);
        await writer.CreateAsync(Ban(GlobalBanSubjectKind.Network, "192.0.2.0/24"));
        await writer.CreateAsync(Ban(GlobalBanSubjectKind.Uuid, uuid));
        await mainEnforcer.ScanOnceAsync();
        await sandboxEnforcer.ScanOnceAsync();
        Assert.Multiple(() =>
        {
            Assert.That(mainOps.Kicked, Is.EqualTo(new[] { first }));
            Assert.That(sandboxOps.Kicked, Is.EqualTo(new[] { second, third }));
            Assert.That(mainRepo.Reads, Is.EqualTo(2));
            Assert.That(sandboxRepo.Reads, Is.EqualTo(2));
        });
        await mainEnforcer.ScanOnceAsync();
        await sandboxEnforcer.ScanOnceAsync();
        Assert.That(mainOps.Kicked.Concat(sandboxOps.Kicked).Count(), Is.EqualTo(3), "Successful requests are not duplicated every scan.");
    }

    [Test]
    public async Task EmptyWorldDoesNotQuerySharedRepository()
    {
        var repo = new CountingReplica((await OpenAsync()).GlobalBans);
        using var enforcer = Create(repo, new DirectoryStub(), new RecordingOperations(), new InlineDispatcher());
        await enforcer.ScanOnceAsync();
        Assert.That(repo.Reads, Is.Zero);
    }

    [Test]
    public async Task AccountKickIsCommittedOnlyUnderExactAuthenticationTenure()
    {
        var repo = (await OpenAsync()).GlobalBans;
        await new GlobalBanService(repo).CreateAsync(Ban(GlobalBanSubjectKind.Account, "alice"));
        var player = Player(0, WorldId.Primary, "192.0.2.4");
        var players = new DirectoryStub(player);
        var accounts = new AccountStub(player);
        var dispatcher = new ManualDispatcher();
        var ops = new RecordingOperations(() => Assert.That(accounts.InsideBinding, Is.True));
        using var enforcer = Create(repo, players, ops, dispatcher, accounts);

        Task staleScan = enforcer.ScanOnceAsync();
        accounts.Binding = accounts.Binding with { AuthenticationId = Guid.NewGuid() };
        dispatcher.ExecutePending();
        await staleScan;
        Assert.That(ops.Kicked, Is.Empty);

        Task currentScan = enforcer.ScanOnceAsync();
        dispatcher.ExecutePending();
        await currentScan;
        Assert.That(ops.Kicked, Is.EqualTo(new[] { player }));
    }

    [Test]
    public async Task ReusedPlayerSlotIsNotKickedByOlderScan()
    {
        var repo = (await OpenAsync()).GlobalBans;
        var player = Player(0, WorldId.Primary, "192.0.2.4");
        await new GlobalBanService(repo).CreateAsync(Ban(GlobalBanSubjectKind.PlayerName, player.Name));
        var players = new DirectoryStub(player);
        var dispatcher = new ManualDispatcher();
        var ops = new RecordingOperations();
        using var enforcer = Create(repo, players, ops, dispatcher);
        Task scan = enforcer.ScanOnceAsync();
        players.Online = [player with { SessionId = PlayerSessionId.New() }];
        dispatcher.ExecutePending();
        await scan;
        Assert.That(ops.Kicked, Is.Empty);
    }

    [Test]
    public async Task CancellationPreventsAnAlreadyQueuedKick()
    {
        var repo = (await OpenAsync()).GlobalBans;
        var player = Player(0, WorldId.Primary, "192.0.2.4");
        await new GlobalBanService(repo).CreateAsync(Ban(GlobalBanSubjectKind.PlayerName, player.Name));
        var dispatcher = new ManualDispatcher();
        var ops = new RecordingOperations();
        using var enforcer = Create(repo, new DirectoryStub(player), ops, dispatcher);
        using var cancellation = new CancellationTokenSource();
        Task scan = enforcer.ScanOnceAsync(cancellation.Token);
        cancellation.Cancel();
        try { await scan; Assert.Fail("The pending scan must observe cancellation."); }
        catch (OperationCanceledException) { }
        dispatcher.ExecutePending();
        Assert.That(ops.Kicked, Is.Empty);
    }

    private ValueTask<SqliteVegaDatabase> OpenAsync() => SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(root, "vega.db")));

    private static PersistentBanEnforcer Create(IGlobalBanReplica repo, DirectoryStub players, RecordingOperations ops,
        IGameThreadDispatcher dispatcher, IPlayerAccountSessionService? accounts = null) =>
        new(new GlobalBanService(repo), players, ops, new LifecycleStub(), new LoggerStub(), accounts, dispatcher,
            timeProvider: null, interval: null, startWorker: false);

    private static GlobalBanCreated Ban(GlobalBanSubjectKind kind, string value) =>
        new(GlobalBanId.New(), new GlobalBanSubject(kind, value), "test moderation", "Server", DateTimeOffset.UtcNow, null, 1);

    private static PlayerSnapshot Player(int slot, WorldId world, string address, string? uuid = null, string? transport = null) =>
        new(new PlayerId(slot), $"Player{slot}", false, 100, 100, 20, 20, default, false, 0)
        {
            SessionId = PlayerSessionId.New(), WorldId = world,
            RemoteEndpoint = $"{transport ?? "127.0.0.1"}:7777",
            Connection = new(new ConnectionId(slot), $"{transport ?? "127.0.0.1"}:7777", $"{address}:7777",
                ConnectionForwardingProvenance.TrustedForwarder, ClientPlatformInfo.Unknown, uuid, ConnectionLoginPhase.FullyConnected)
        };

    private sealed class CountingReplica(IGlobalBanReplica inner) : IGlobalBanReplica
    {
        public int Reads { get; private set; }
        public ValueTask<bool> ApplyCreatedAsync(GlobalBanCreated value, CancellationToken token = default) => inner.ApplyCreatedAsync(value, token);
        public ValueTask<bool> ApplyRevokedAsync(GlobalBanRevoked value, CancellationToken token = default) => inner.ApplyRevokedAsync(value, token);
        public IReadOnlyList<GlobalBanCreated> SnapshotActive(DateTimeOffset now) { Reads++; return inner.SnapshotActive(now); }
    }

    private sealed class DirectoryStub(params PlayerSnapshot[] online) : IPlayerDirectory
    {
        public PlayerSnapshot[] Online { get; set; } = online;
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Online;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) { player = Online.FirstOrDefault(p => p.Id == id); return player is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? player) { player = Online.FirstOrDefault(p => p.Name == name); return player is not null; }
    }

    private sealed class RecordingOperations(Action? assert = null) : IPlayerOperations
    {
        public List<PlayerSnapshot> Kicked { get; } = [];
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken token = default) =>
            throw new InvalidOperationException("An exact session snapshot is required.");
        public ValueTask<PlayerOperationResult> KickAsync(PlayerSnapshot player, string reason, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            assert?.Invoke();
            Kicked.Add(player);
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class LifecycleStub : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened { add { } remove { } }
        public event Action<PlayerSessionClosed>? Closed { add { } remove { } }
    }

    private sealed class LoggerStub : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }

    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { action(); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
    }

    private sealed class ManualDispatcher : IGameThreadDispatcher
    {
        private Action? pending;
        public bool IsGameThreadKnown => true;
        public bool IsGameThread { get; private set; }
        public void AssertGameThread() => Assert.That(IsGameThread, Is.True);
        public bool TryPost(string name, Action action) { Assert.That(pending is null, Is.True); pending = action; return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void ExecutePending()
        {
            Action action = pending ?? throw new InvalidOperationException("No queued kick");
            pending = null;
            IsGameThread = true;
            try { action(); }
            finally { IsGameThread = false; }
        }
    }

    private sealed class AccountStub(PlayerSnapshot player) : IPlayerAccountSessionService
    {
        private static readonly AccountSnapshot Account = new(new AccountId(1), "alice", "default", null);
        public PlayerAccountBinding Binding { get; set; } = new(player.Id, player.SessionId!.Value, Account.Id, Guid.NewGuid());
        public bool InsideBinding { get; private set; }
        public bool TryGetAccount(PlayerId id, out AccountSnapshot? account) { account = id == Binding.PlayerId ? Account : null; return account is not null; }
        public bool TryGetBinding(PlayerSnapshot snapshot, out PlayerAccountBinding binding)
        { binding = Binding; return binding.PlayerId == snapshot.Id && binding.SessionId == snapshot.SessionId; }
        public bool TryRunWithBinding<T>(PlayerAccountBinding binding, Func<T> operation, out T result)
        {
            if (binding != Binding) { result = default!; return false; }
            InsideBinding = true;
            try { result = operation(); return true; }
            finally { InsideBinding = false; }
        }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken token = default) => throw new NotSupportedException();
    }
}
