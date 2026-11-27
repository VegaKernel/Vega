using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Authorization;
using Vega.Core.Commands;
using Vega.Core.Localization;
using Vega.Core.Modules;
using Vega.Core.Players;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PlayerPlaytimeServiceTests
{
    [Test]
    public async Task ExactEnteredAuthenticationTenuresAreMonotonicFinalAndRetryableAcrossWorlds()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vega-playtime-fixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(directory, "fixture.db"), TimeSpan.FromSeconds(5)));
            var created = await database.Accounts.CreateAsync("alice", "fixture-password", "default", "fixture");
            Assert.That(created.Succeeded, Is.True);
            var account = created.After!;
            var clock = new Clock();
            var logger = new QuietLogger();
            var store = new FailOnceStore(database.Playtime);
            var connection = new Lifecycle(WorldId.Primary);
            using (var authz = new PlayerSessionAuthorizationService(connection))
            using (var accounts = new PlayerAccountSessionService(database.Accounts, database.Authorization, authz, new LocalizationService("en-US"), connection))
            using (var playtime = new PlayerPlaytimeService(connection, connection, connection, accounts, store, WorldId.Primary, logger, clock, false))
            {
                PlayerSnapshot first = connection.Open();
                Assert.That((await accounts.LoginAsync(first.Id, "alice", "fixture-password", expectedSession: first.SessionId)).Succeeded, Is.True);
                clock.Advance(20);
                Assert.That(await playtime.ReadCurrentAsync(first, default), Is.Null, "Login before native world entry does not count.");
                connection.Enter(first);
                clock.Advance(5);
                connection.Enter(first); // duplicate entry must not reset the clock
                clock.Advance(5);
                clock.Utc = clock.Utc.AddDays(-1); // monotonic residence is independent of wall-clock drift
                Assert.That(store.Writes, Is.Zero, "Native events perform no SQL work.");
                Assert.ThrowsAsync<IOException>(async () => await playtime.FlushOnceAsync());
                await playtime.FlushOnceAsync();
                Assert.That((await playtime.ReadCurrentAsync(first, default))!.Current, Is.EqualTo(TimeSpan.FromSeconds(10)));
                var directorySnapshot = new DirectorySnapshot { Player = first };
                await using (var plugin = new HotModuleRegistrationScope(new CommandDispatcher([]), logger, "playtime-test", directory,
                    playerDirectory: directorySnapshot, activityWorldId: WorldId.Primary, playtime: playtime))
                {
                    Assert.That(plugin.Statistics.IsAvailable, Is.True);
                    Assert.That((await plugin.Statistics.ReadPlaytimeAsync(first))!.Current, Is.EqualTo(TimeSpan.FromSeconds(10)));
                    Assert.That(await plugin.Statistics.ReadPlaytimeAsync(first with { WorldId = new("foreign") }), Is.Null);
                }
                clock.Advance(3);
                Assert.That(accounts.Logout(first.Id, first.SessionId).Succeeded, Is.True);
                clock.Advance(40);
                Assert.That((await accounts.LoginAsync(first.Id, "alice", "fixture-password", expectedSession: first.SessionId)).Succeeded, Is.True);
                clock.Advance(7);
                connection.Close(first);
                PlayerSnapshot second = connection.Open();
                connection.Enter(second);
                Assert.That((await accounts.LoginAsync(second.Id, "alice", "fixture-password", expectedSession: second.SessionId)).Succeeded, Is.True);
                connection.Close(first); // stale closing/closed for old slot cannot stop the new tenure
                clock.Advance(2);
                Assert.That((await playtime.ReadCurrentAsync(second, default))!.Current, Is.EqualTo(TimeSpan.FromSeconds(2)));
                connection.Close(second);
                Assert.That(await playtime.ReadCurrentAsync(second, default), Is.Null);
                await playtime.FlushOnceAsync();
                Assert.That(await database.Playtime.ReadAsync(account.Id, WorldId.Primary), Is.EqualTo(new PlayerPlaytimeSummary(
                    account.Id, WorldId.Primary, TimeSpan.FromSeconds(22), TimeSpan.FromSeconds(22), 3, 3)));
            }

            var arena = new WorldId("arena");
            var transfer = new Lifecycle(arena);
            using (var authz = new PlayerSessionAuthorizationService(transfer))
            using (var accounts = new PlayerAccountSessionService(database.Accounts, database.Authorization, authz, new LocalizationService("en-US"), transfer))
            using (var playtime = new PlayerPlaytimeService(transfer, transfer, transfer, accounts, store, arena, logger, clock, false))
            {
                var player = transfer.Open();
                Assert.That(await accounts.EstablishTrustedTransferBindingAsync(player, account.Id.Value.ToString()), Is.Not.Null);
                clock.Advance(12);
                transfer.Enter(player);
                clock.Advance(8);
                // Disposal captures a final checkpoint even if native disconnect is not delivered.
            }
            Assert.That(await database.Playtime.ReadAsync(account.Id, arena), Is.EqualTo(new PlayerPlaytimeSummary(
                account.Id, arena, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(8), 4, 1)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task StatisticsQueriesRejectReplacedSessionAndCancelDuringRetirement()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-plugin-playtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var player = new PlayerSnapshot(new(3), "Alice", false, 100, 100, 20, 20, new(0, 0), false, 0)
            { SessionId = PlayerSessionId.New(), WorldId = WorldId.Primary };
            var directory = new DirectorySnapshot { Player = player };
            var blocking = new BlockingReader();
            var scope = new HotModuleRegistrationScope(new CommandDispatcher([]), new QuietLogger(), "stats-test", root,
                playerDirectory: directory, activityWorldId: WorldId.Primary, playtime: blocking);
            var query = scope.Statistics.ReadPlaytimeAsync(player).AsTask();
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            directory.Player = player with { SessionId = PlayerSessionId.New() };
            Assert.That(await scope.Statistics.ReadPlaytimeAsync(player), Is.Null);
            await scope.DisposeAsync();
            Assert.CatchAsync<OperationCanceledException>(async () => await query);
            Assert.CatchAsync<OperationCanceledException>(async () => await scope.Statistics.ReadPlaytimeAsync(directory.Player!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DirectorySnapshot : IPlayerDirectory
    {
        public PlayerSnapshot? Player { get; set; }
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Player is { } value ? [value] : [];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = Player?.Id == id ? Player : null) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = Player?.Name == name ? Player : null) is not null;
    }

    private sealed class BlockingReader : IPlayerPlaytimeSessionReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PlayerPlaytimeView?> ReadCurrentAsync(PlayerSnapshot player, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class Lifecycle(WorldId world) : IPlayerSessionLifecycle, IPlayerSessionClosing, IPlayerSessionEnteredWorld
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;
        public event Action<PlayerSessionClosed>? Closing;
        public event Action<PlayerSnapshot>? EnteredWorld;

        public PlayerSnapshot Open()
        {
            var player = new PlayerSnapshot(new(1), "Alice", false, 100, 100, 20, 20, new(0, 0), false, 0)
            { SessionId = PlayerSessionId.New(), WorldId = world };
            Opened?.Invoke(new(player.SessionId.Value, player.Id, player.Name, DateTimeOffset.UtcNow));
            return player;
        }

        public void Enter(PlayerSnapshot player) => EnteredWorld?.Invoke(player);

        public void Close(PlayerSnapshot player)
        {
            var closed = new PlayerSessionClosed(player.SessionId!.Value, player.Id, player.Name, DateTimeOffset.UtcNow);
            Closing?.Invoke(closed);
            Closed?.Invoke(closed);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public DateTimeOffset Utc { get; set; } = new(2026, 11, 27, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(int seconds)
        {
            ticks += TimeSpan.FromSeconds(seconds).Ticks;
            Utc = Utc.AddSeconds(seconds);
        }
    }

    private sealed class QuietLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }

    private sealed class FailOnceStore(IPlayerPlaytimeStore inner) : IPlayerPlaytimeStore
    {
        public int Writes { get; private set; }
        public ValueTask WriteAsync(IReadOnlyList<PlayerPlaytimeCheckpoint> checkpoints, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (Writes == 1) throw new IOException("Synthetic checkpoint failure.");
            return inner.WriteAsync(checkpoints, cancellationToken);
        }

        public ValueTask<PlayerPlaytimeSummary> ReadAsync(AccountId account, WorldId world, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(account, world, cancellationToken);
    }
}
