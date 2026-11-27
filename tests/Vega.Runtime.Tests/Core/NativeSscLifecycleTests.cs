using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using Vega.Contracts.Accounts;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Authorization;
using Vega.Core.Localization;
using Vega.Persistence.Sqlite;
using Vega.Sandboxes;
using Vega.Terraria.Hooking;
using Vega.Terraria.Players;
using static Vega.Terraria.Players.TerrariaSandboxSscCharacterAdapter;

namespace Vega.Runtime.Tests.Core;

[TestFixture, NonParallelizable]
public sealed class NativeSscLifecycleTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void NativeJoinLoginLogoutAndReconnectRespectServerCharacterAuthority(bool authoritative)
    {
        using var native = new NativeFixture(authoritative);
        var database = native.Database;
        var alice = CreateAccount(database, "alice");
        var bob = CreateAccount(database, "bob");
        var store = new SandboxSscStore(Path.Combine(native.Root, "ssc.json"));
        using var boundary = new TerrariaPlayerBoundary(native.Dispatcher, worldId: WorldId.Primary, connectionMetadata: new());
        using var permissions = new PlayerSessionAuthorizationService(boundary);
        using var accounts = new PlayerAccountSessionService(database.Accounts, database.Authorization, permissions, new LocalizationService("en-US"), boundary);
        var adapter = new TerrariaSandboxSscCharacterAdapter(native.Dispatcher, boundary);
        using var coordinator = new SandboxSscSessionCoordinator(boundary, boundary, accounts, boundary, store, adapter,
            WorldId.Primary, SandboxSscMode.Inherit, native.Log, primaryCharacterStore: authoritative ? store : null);
        using var hooks = new NativeSscLifecycleHooks(adapter, boundary, accounts, native.Dispatcher, native.Log, authoritative);
        boundary.Attach(); hooks.Attach();
        var first = native.Open(boundary);
        Assert.That(native.Player.inventory[0].type, Is.EqualTo(authoritative ? 0 : ItemID.StoneBlock));
        Assert.That(native.Player.statLife, Is.EqualTo(authoritative ? 100 : 250));
        if (authoritative) Assert.That(native.Socket.PacketIds, Does.Contain(MessageID.SyncEquipment));

        Assert.That(Login(accounts, first, "alice").Succeeded, Is.True);
        if (!authoritative)
        {
            Assert.That(coordinator.GetCharacterReadiness(first), Is.EqualTo(SscCharacterReadiness.NotManaged));
            Assert.That(accounts.Logout(first.Id, first.SessionId).Succeeded, Is.True);
            Assert.That(native.Player.inventory[0].stack, Is.EqualTo(500));
            Assert.That(store.Read(alice.Id.Value.ToString(), WorldId.Primary, SandboxSscMode.Inherit), Is.Null);
            var next = native.Reconnect(boundary);
            Assert.That(next.SessionId, Is.Not.EqualTo(first.SessionId));
            Assert.That(Login(accounts, next, "alice").Succeeded, Is.True);
            Assert.That(native.Player.inventory[0].stack, Is.EqualTo(500));
            return;
        }

        native.Dispatcher.Until(() => coordinator.IsCharacterReady(first));
        Assert.That(native.Player.inventory[0].type, Is.EqualTo(ItemID.CopperShortsword));
        Assert.That(native.Player.statLife, Is.EqualTo(100));
        Assert.That(accounts.TryGetBinding(first, out var firstBinding), Is.True);
        var player = native.Player;
        SetItem(player.inventory[5], ItemID.StoneBlock, 37);
        SetItem(player.bank.item[2], ItemID.DirtBlock, 11);
        SetItem(player.Loadouts[1].Armor[0], ItemID.CopperHelmet, 1);
        player.CurrentLoadoutIndex = 1;
        player.statLife = 240; player.statLifeMax = 400; player.statMana = 80; player.statManaMax = 100;
        Assert.That(accounts.Logout(first.Id, first.SessionId).Succeeded, Is.True);
        Assert.That(player.inventory[5].type, Is.Zero);
        Assert.That(player.bank.item[2].type, Is.Zero);
        Assert.That(coordinator.GetCharacterReadiness(first), Is.EqualTo(SscCharacterReadiness.Unauthenticated));
        native.Dispatcher.Until(() => Read(store, alice.Id, WorldId.Primary, SandboxSscMode.Inherit)?.Inventory.ElementAtOrDefault(5).Stack == 37);

        Assert.That(Login(accounts, first, "bob").Succeeded, Is.True);
        native.Dispatcher.Until(() => coordinator.IsCharacterReady(first));
        Assert.That(player.inventory[5].type, Is.Zero, "A new account on the same native socket must not inherit the old account.");
        Assert.That(accounts.TryGetBinding(first, out var secondBinding), Is.True);
        Assert.That(secondBinding.AccountId, Is.EqualTo(bob.Id));
        Assert.That(secondBinding.AuthenticationId, Is.Not.EqualTo(firstBinding.AuthenticationId));
        Assert.That(accounts.Logout(first.Id, first.SessionId).Succeeded, Is.True);
        Assert.That(Login(accounts, first, "alice").Succeeded, Is.True);
        native.Dispatcher.Until(() => coordinator.IsCharacterReady(first));
        AssertRestored(player);
        player.inventory[5].stack = 41;
        var reconnected = native.Reconnect(boundary);
        Assert.That(reconnected.SessionId, Is.Not.EqualTo(first.SessionId));
        Assert.That(coordinator.IsCharacterReady(first), Is.False);
        Assert.That(native.Player.inventory[5].type, Is.Zero);
        Assert.That(Login(accounts, reconnected, "alice").Succeeded, Is.True);
        native.Dispatcher.Until(() => coordinator.IsCharacterReady(reconnected));
        AssertRestored(native.Player, expectedStack: 41);
        Assert.That(native.Socket.PacketIds, Does.Contain(MessageID.SyncEquipment));
        Assert.That(native.Socket.PacketIds, Does.Contain((byte)16)); // native life sync
        Assert.That(native.Socket.PacketIds, Does.Contain((byte)42)); // native mana sync
    }

    [TestCase(SandboxSscMode.Inherit)]
    [TestCase(SandboxSscMode.CopyOnEnter)]
    [TestCase(SandboxSscMode.Isolated)]
    [TestCase(SandboxSscMode.Disabled)]
    public void ConsumedTransferRestoresActualNativeCharacterForEachMode(SandboxSscMode mode)
    {
        using var native = new NativeFixture(mode != SandboxSscMode.Disabled);
        var account = CreateAccount(native.Database, "alice");
        string accountId = account.Id.Value.ToString();
        var target = new WorldId("arena");
        var store = new SandboxSscStore(Path.Combine(native.Root, "ssc.json"));
        var now = DateTimeOffset.UtcNow;
        string payload = Payload(17);
        Assert.That(store.Save(accountId, WorldId.Primary, SandboxSscMode.Inherit, payload, 0, now).IsSuccess, Is.True);
        if (mode == SandboxSscMode.Isolated)
            Assert.That(store.Save(accountId, target, mode, Payload(29), 0, now).IsSuccess, Is.True);
        var preparation = store.PrepareTransfer(accountId, WorldId.Primary, target, SandboxSscMode.Inherit, mode, now, TimeSpan.FromSeconds(30));
        Assert.That(preparation.Accepted, Is.True, preparation.Message);
        Assert.That(store.StageTransferState(preparation.Lease!.LeaseId, accountId, payload, now).Accepted, Is.True);
        var tickets = new SandboxTransferTicketService(Path.Combine(native.Root, "control.key"));
        var admissions = new SandboxTransferAdmissionService(Path.Combine(native.Root, "admissions.json"), tickets, store);
        var ticket = tickets.Issue(accountId, WorldId.Primary, target, preparation.Lease.LeaseId);
        // Typed local rendezvous, not a fabricated packet or network session.
        Assert.That(admissions.Register(ticket, "127.0.0.1", "fixture-client-token", now).Accepted, Is.True);
        var accepted = admissions.Consume(target, "127.0.0.1", "fixture-client-token", DateTimeOffset.UtcNow);
        Assert.That(accepted.Accepted, Is.True, accepted.Message);
        Assert.That(admissions.Consume(target, "127.0.0.1", "fixture-client-token", DateTimeOffset.UtcNow).Accepted, Is.False);

        using var boundary = new TerrariaPlayerBoundary(native.Dispatcher, new(), target);
        var directory = new AdmittedDirectory(boundary, accepted.Ticket!.AccountId);
        using var permissions = new PlayerSessionAuthorizationService(boundary);
        using var accounts = new PlayerAccountSessionService(native.Database.Accounts, native.Database.Authorization, permissions, new LocalizationService("en-US"), boundary);
        var adapter = new TerrariaSandboxSscCharacterAdapter(native.Dispatcher, boundary);
        using var coordinator = new SandboxSscSessionCoordinator(directory, boundary, accounts, boundary, store, adapter, target, mode, native.Log);
        using var hooks = new NativeSscLifecycleHooks(adapter, boundary, accounts, native.Dispatcher, native.Log, mode != SandboxSscMode.Disabled);
        boundary.Attach(); hooks.Attach();
        native.Open(boundary);
        var player = directory.SnapshotOnlinePlayers().Single(p => p.Id.Slot == NativeFixture.Slot);
        native.Dispatcher.Until(() => accounts.TryGetBinding(player, out var binding) && binding.AccountId == account.Id &&
            (mode == SandboxSscMode.Disabled || coordinator.IsCharacterReady(player)));
        if (mode == SandboxSscMode.Disabled)
        {
            Assert.That(native.Player.inventory[0].stack, Is.EqualTo(500));
            Assert.That(coordinator.GetCharacterReadiness(player), Is.EqualTo(SscCharacterReadiness.NotManaged));
            return;
        }
        int expected = mode == SandboxSscMode.Isolated ? 29 : 17;
        Assert.That(native.Player.inventory[0].stack, Is.EqualTo(expected));
        Assert.That(native.Player.statLife, Is.EqualTo(220));
        native.Player.inventory[0].stack = 73;
        TerrariaRuntime.Hooks.Netplay.NotifyServerConnectionClosing(native.Client);
        native.Dispatcher.Until(() => Read(store, account.Id, target, mode)?.Inventory[0].Stack == 73);
        Assert.That(Read(store, account.Id, WorldId.Primary, SandboxSscMode.Inherit)?.Inventory[0].Stack,
            Is.EqualTo(mode == SandboxSscMode.Inherit ? 73 : 17));
    }

    private static AccountSnapshot CreateAccount(SqliteVegaDatabase database, string name) =>
        database.Accounts.CreateAsync(name, "fixture-password", "default", "fixture").AsTask().GetAwaiter().GetResult().After!;

    private static PlayerAccountSessionResult Login(PlayerAccountSessionService accounts, PlayerSnapshot player, string name) =>
        accounts.LoginAsync(player.Id, name, "fixture-password", expectedSession: player.SessionId).AsTask().GetAwaiter().GetResult();

    private static CharacterState? Read(SandboxSscStore store, AccountId account, WorldId world, SandboxSscMode mode) =>
        store.Read(account.Value.ToString(), world, mode) is { } state ? JsonSerializer.Deserialize<CharacterState>(state.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;

    private static string Payload(int stack) => SerializeImportedCharacter(new CharacterState
    {
        Version = 1, Life = 220, MaxLife = 400, Mana = 60, MaxMana = 100, SpawnX = -1, SpawnY = -1,
        Inventory = [new(ItemID.StoneBlock, stack, 0, false)]
    });

    private static void SetItem(global::Terraria.Item item, int type, int stack) { item.SetDefaults(type); item.stack = stack; }

    private static void AssertRestored(global::Terraria.Player player, int expectedStack = 37) => Assert.Multiple(() =>
    {
        Assert.That(player.inventory[5].type, Is.EqualTo(ItemID.StoneBlock));
        Assert.That(player.inventory[5].stack, Is.EqualTo(expectedStack));
        Assert.That(player.bank.item[2].stack, Is.EqualTo(11));
        Assert.That(player.Loadouts[1].Armor[0].type, Is.EqualTo(ItemID.CopperHelmet));
        Assert.That(player.CurrentLoadoutIndex, Is.EqualTo(1));
        Assert.That(player.statLife, Is.EqualTo(240));
        Assert.That(player.statLifeMax, Is.EqualTo(400));
        Assert.That(player.statMana, Is.EqualTo(80));
        Assert.That(player.statManaMax, Is.EqualTo(100));
    });

    private sealed class AdmittedDirectory(TerrariaPlayerBoundary boundary, string accountId) : IPlayerDirectory
    {
        private PlayerSnapshot Admitted(PlayerSnapshot player) => player with { Connection = player.Connection! with { TrustedTransferAccountId = accountId } };
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => boundary.SnapshotOnlinePlayers().Select(Admitted).ToArray();
        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        { player = boundary.TryGet(id, out var live) && live is not null ? Admitted(live) : null; return player is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? player)
        { player = boundary.TryFindExact(name, out var live) && live is not null ? Admitted(live) : null; return player is not null; }
    }

    private sealed class NativeFixture : IDisposable
    {
        public const int Slot = 3;
        private readonly global::Terraria.Player oldPlayer = global::Terraria.Main.player[Slot];
        private readonly global::Terraria.RemoteClient[] oldClients = (global::Terraria.RemoteClient[])global::Terraria.Netplay.Clients.Clone();
        private readonly global::Terraria.MessageBuffer[] oldBuffers = (global::Terraria.MessageBuffer[])global::Terraria.NetMessage.buffer.Clone();
        private readonly int oldMode = global::Terraria.Main.netMode;
        private readonly bool oldDedicated = global::Terraria.Main.dedServ, oldSsc = global::Terraria.Main.ServerSideCharacter;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vega-native-ssc", Guid.NewGuid().ToString("N"));
        public SqliteVegaDatabase Database { get; }
        public OwnerDispatcher Dispatcher { get; } = new();
        public QuietLogger Log { get; } = new();
        public global::Terraria.Player Player => global::Terraria.Main.player[Slot];
        public global::Terraria.RemoteClient Client => global::Terraria.Netplay.Clients[Slot];
        public RecordingSocket Socket { get; private set; } = new();

        public NativeFixture(bool authoritative)
        {
            Database = SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(Root, "vega.db"))).AsTask().GetAwaiter().GetResult();
            global::Terraria.Main.netMode = 2;
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.ServerSideCharacter = authoritative;
            for (int i = 0; i < global::Terraria.Netplay.Clients.Length; i++) global::Terraria.Netplay.Clients[i] = new global::Terraria.RemoteClient { Id = i };
            for (int i = 0; i < global::Terraria.NetMessage.buffer.Length; i++) global::Terraria.NetMessage.buffer[i] = new global::Terraria.MessageBuffer { whoAmI = i, broadcast = i == Slot };
        }

        public PlayerSnapshot Open(TerrariaPlayerBoundary boundary)
        {
            global::Terraria.Main.player[Slot] = new global::Terraria.Player { active = true, whoAmI = Slot, name = "fixture", statLife = 250, statLifeMax = 400, statMana = 60, statManaMax = 100 };
            SetItem(Player.inventory[0], ItemID.StoneBlock, 500);
            global::Terraria.Netplay.Clients[Slot] = new global::Terraria.RemoteClient { Id = Slot };
            Socket = new RecordingSocket();
            Client.Activate(Socket); Client.State = 2;
            TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerBeforeWorldData(Slot);
            Client.State = 10;
            TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(Slot);
            Assert.That(boundary.TryGet(new(Slot), out var snapshot), Is.True);
            Assert.That(snapshot!.SessionId, Is.Not.Null);
            return snapshot;
        }

        public PlayerSnapshot Reconnect(TerrariaPlayerBoundary boundary)
        { TerrariaRuntime.Hooks.Netplay.NotifyServerConnectionClosing(Client); return Open(boundary); }

        public void Dispose()
        {
            global::Terraria.Main.player[Slot] = oldPlayer;
            Array.Copy(oldClients, global::Terraria.Netplay.Clients, oldClients.Length);
            Array.Copy(oldBuffers, global::Terraria.NetMessage.buffer, oldBuffers.Length);
            global::Terraria.Main.netMode = oldMode; global::Terraria.Main.dedServ = oldDedicated; global::Terraria.Main.ServerSideCharacter = oldSsc;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class OwnerDispatcher : IGameThreadDispatcher
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        private readonly ConcurrentQueue<Action> pending = new();
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => Environment.CurrentManagedThreadId == owner;
        public void AssertGameThread() => Assert.That(IsGameThread, Is.True);
        public bool TryPost(string name, Action action) { pending.Enqueue(action); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void Until(Func<bool> condition)
        {
            long started = Stopwatch.GetTimestamp();
            while (!condition())
            {
                while (pending.TryDequeue(out var action)) action();
                Assert.That(Stopwatch.GetElapsedTime(started), Is.LessThan(TimeSpan.FromSeconds(10)), "SSC lifecycle did not reach expected state.");
                Thread.Sleep(5);
            }
        }
    }

    private sealed class RecordingSocket : ISocket
    {
        public HashSet<byte> PacketIds { get; } = [];
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => new TcpAddress(System.Net.IPAddress.Loopback, 12345);
        public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
        { if (size >= 3) PacketIds.Add(data[offset + 2]); callback(state); }
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
    }
    private sealed class QuietLogger : IOperationsLogger
    { public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { } }
}
