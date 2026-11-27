using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Multiplicity.Packets;
using NUnit.Framework;
using Terraria.ID;
using Vega.Contracts.Accounts;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Persistence;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;
using Vega.Terraria.Worlds;
using Slot = Vega.Terraria.Worlds.NpcShopPurchasePlan.Slot;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class NpcShopTests
{
    [Test]
    [NonParallelizable]
    public void PersistentQuestAndWalletPurchaseUseExactAccountAndDurableReservations()
    {
        const int playerSlot = 2, npcSlot = 0;
        var oldPlayer = global::Terraria.Main.player[playerSlot];
        var oldClient = global::Terraria.Netplay.Clients[playerSlot];
        var oldBuffer = global::Terraria.NetMessage.buffer[playerSlot];
        var oldBroadcast = global::Terraria.NetMessage.buffer[256];
        var oldNpc = global::Terraria.Main.npc[npcSlot];
        int oldMode = global::Terraria.Main.netMode;
        bool oldDedicated = global::Terraria.Main.dedServ, oldSsc = global::Terraria.Main.ServerSideCharacter;
        float oldRight = global::Terraria.Main.rightWorld, oldBottom = global::Terraria.Main.bottomWorld;
        string root = Path.Combine(Path.GetTempPath(), "vega-npc-persistent-native", Guid.NewGuid().ToString("N"));
        var dispatcher = new PumpDispatcher();
        var world = new WorldId("account-shop");
        using var boundary = new TerrariaPlayerBoundary(dispatcher, new TerrariaConnectionMetadataRegistry(), world);
        var accountSessions = new BindingAccounts();
        var store = new FileNpcAccountStore(root);
        var provider = new TrackingEconomyProvider(store, dispatcher);
        bool ready = true;
        bool checkpointSucceeds = true;
        int checkpoints = 0;
        using var actors = new TerrariaNpcActorService(dispatcher, world, boundary, new WorldGameplayEventHub(), new QuietLogger(),
            accountSessions, store, provider, _ => ready, (player, expected, _) =>
            {
                Assert.That(dispatcher.IsGameThread, Is.False);
                Interlocked.Increment(ref checkpoints);
                return ValueTask.FromResult(checkpointSucceeds && accountSessions.TryGetBinding(player, out var binding) && binding == expected);
            })
        {
            SpawnNative = (_, _, type) =>
            {
                global::Terraria.Main.npc[npcSlot] = new global::Terraria.NPC { whoAmI = npcSlot, active = true,
                    type = type, townNPC = true, life = 100, generation = 4, width = 18, height = 40 };
                return npcSlot;
            }
        };
        try
        {
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.netMode = 2;
            global::Terraria.Main.ServerSideCharacter = true;
            global::Terraria.Main.rightWorld = global::Terraria.Main.bottomWorld = 10_000;
            global::Terraria.Main.npc[npcSlot] = new global::Terraria.NPC();
            global::Terraria.NetMessage.buffer[256] = new global::Terraria.MessageBuffer { whoAmI = 256 };
            boundary.Attach();
            PlayerSnapshot Connect()
            {
                var player = new global::Terraria.Player { whoAmI = playerSlot, active = true, name = "Buyer", position = new(100, 100) };
                global::Terraria.Main.player[playerSlot] = player;
                var client = new global::Terraria.RemoteClient { Id = playerSlot, State = 10 };
                global::Terraria.Netplay.Clients[playerSlot] = client;
                global::Terraria.NetMessage.buffer[playerSlot] = new global::Terraria.MessageBuffer { whoAmI = playerSlot, broadcast = true };
                client.Activate(new RecordingSocket());
                global::TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(playerSlot);
                Assert.That(boundary.TryGet(new PlayerId(playerSlot), out var current), Is.True);
                return current!;
            }
            void Conversation()
            {
                using var stream = new MemoryStream();
                new NpcTalk { PlayerId = playerSlot, NpcTalkTarget = npcSlot }.ToStream(stream, false);
                var buffer = new global::Terraria.MessageBuffer { whoAmI = playerSlot };
                byte packet = (byte)PacketTypes.NpcTalk;
                int start = 0, offset = 1, length = checked((int)stream.Length + 1), type = packet;
                buffer.readBuffer[0] = packet;
                stream.ToArray().CopyTo(buffer.readBuffer, offset);
                Assert.That(global::TerrariaRuntime.Hooks.MessageBuffer.InvokeGetData(buffer, ref packet, ref offset, ref start, ref length, ref type, 256), Is.True);
                global::Terraria.Main.player[playerSlot].talkNPC = npcSlot;
                dispatcher.Drain();
            }

            var buyer = Connect();
            global::Terraria.Main.netMode = 0;
            var shop = new NpcShopCatalog([new NpcShopOffer("wood", ItemID.Wood, 2, 0) { EconomyPrice = new("gems", 5) }]);
            var quests = new NpcQuestCatalog([new("torch", "Torch", "Bring two torches", new(ItemID.Torch, 2), new(ItemID.StoneBlock, 1))]);
            var creation = dispatcher.Pump(actors.CreateAsync(world, new NpcActorDefinition(17, "Account Merchant", Shop: shop)
                { Quests = quests, PersistentQuestScope = "npc.quest.test", EconomyScope = "npc.wallet.test" }, new(100, 100)));
            Assert.That(creation.Status, Is.EqualTo(NpcOperationStatus.Applied));
            using var actor = creation.Actor!;
            global::Terraria.Main.netMode = 2;
            Conversation();
            Assert.That(dispatcher.Pump(actor.AcceptQuestAsync(buyer, "torch", 1)).Status, Is.EqualTo(NpcQuestStatus.AccountRequired));
            Assert.That(dispatcher.Pump(actor.PurchaseAsync(buyer, "wood", 1)).Status, Is.EqualTo(NpcShopPurchaseStatus.AccountRequired));
            accountSessions.Bind(buyer, new AccountId(1));
            Guid credit = Guid.NewGuid();
            Assert.That(dispatcher.Pump(actor.CreditCurrencyAsync(buyer, new("gems", 10), credit)).Balance, Is.EqualTo(10));
            Assert.That(dispatcher.Pump(actor.CreditCurrencyAsync(buyer, new("gems", 10), credit)).Status, Is.EqualTo(NpcEconomyStatus.AlreadyApplied));
            Assert.That(dispatcher.Pump(actor.AcceptQuestAsync(buyer, "torch", 1)).Status, Is.EqualTo(NpcQuestStatus.Applied));
            var native = global::Terraria.Main.player[playerSlot];
            native.inventory[20].SetDefaults(ItemID.Torch);
            native.inventory[20].stack = 1;
            Assert.That(dispatcher.Pump(actor.TurnInQuestAsync(buyer, "torch", 1)).InventoryFailure, Is.EqualTo(NpcShopPurchaseStatus.InsufficientFunds));
            Assert.That(dispatcher.Pump(actor.GetQuestsAsync(buyer)).Single().State, Is.EqualTo(NpcQuestState.Accepted));
            native.inventory[20].stack = 2;
            ready = false;
            Assert.That(dispatcher.Pump(actor.TurnInQuestAsync(buyer, "torch", 1)).InventoryFailure, Is.EqualTo(NpcShopPurchaseStatus.InventoryAuthorityRequired));
            Assert.That(dispatcher.Pump(actor.PurchaseAsync(buyer, "wood", 1)).Status, Is.EqualTo(NpcShopPurchaseStatus.InventoryAuthorityRequired));
            Assert.That(dispatcher.Pump(actor.GetCurrencyBalanceAsync(buyer, "gems")).Balance, Is.EqualTo(10));
            ready = true;
            var finished = dispatcher.Pump(actor.TurnInQuestAsync(buyer, "torch", 1));
            Assert.That(finished.Quest?.State, Is.EqualTo(NpcQuestState.Completed));
            Assert.That(native.inventory.Take(50).Where(i => i.type == ItemID.StoneBlock).Sum(i => i.stack), Is.EqualTo(1));
            Assert.That(native.inventory[20].IsAir, Is.True);
            Guid purchaseId = Guid.NewGuid();
            var purchase = dispatcher.Pump(actor.PurchaseCurrencyAsync(buyer, "wood", 1, purchaseId));
            Assert.That(purchase.Status, Is.EqualTo(NpcShopPurchaseStatus.Committed));
            Assert.That(purchase.OperationId, Is.EqualTo(purchaseId));
            Assert.That(purchase.EconomyPaid, Is.EqualTo(new NpcEconomyPrice("gems", 5)));
            Assert.That(dispatcher.Pump(actor.GetCurrencyBalanceAsync(buyer, "gems")).Balance, Is.EqualTo(5));
            Assert.That(native.inventory.Take(50).Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(2));
            Assert.That(dispatcher.Pump(actor.PurchaseCurrencyAsync(buyer, "wood", 1, purchaseId)).Status, Is.EqualTo(NpcShopPurchaseStatus.RecoveryRequired));
            Assert.That(native.inventory.Take(50).Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(2));

            // Reauthentication after the asynchronous debit reservation must prevent native
            // mutation and refund the old account, never charge or grant to the replacement.
            provider.AfterReserve = () => accountSessions.Bind(buyer, new AccountId(2));
            Assert.That(dispatcher.Pump(actor.PurchaseAsync(buyer, "wood", 1)).Status, Is.EqualTo(NpcShopPurchaseStatus.InventoryChanged));
            provider.AfterReserve = null;
            Assert.That(native.inventory.Take(50).Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(2));
            accountSessions.Bind(buyer, new AccountId(1));
            Assert.That(dispatcher.Pump(actor.GetCurrencyBalanceAsync(buyer, "gems")).Balance, Is.EqualTo(5));

            checkpointSucceeds = false;
            var failedCheckpoint = dispatcher.Pump(actor.PurchaseAsync(buyer, "wood", 1));
            Assert.That(failedCheckpoint.Status, Is.EqualTo(NpcShopPurchaseStatus.RecoveryRequired));
            Assert.That(native.inventory.Take(50).Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(4));
            Assert.That(dispatcher.Pump(actor.GetCurrencyBalanceAsync(buyer, "gems")).PendingOperationId, Is.EqualTo(failedCheckpoint.OperationId));
            var walletScope = new NpcAccountScope(world, "npc.wallet.test", new AccountId(1));
            // Explicit reconciliation for the fixture's observed grant, not an automatic refund.
            Assert.That(store.FinalizeDebitAsync(walletScope, failedCheckpoint.OperationId!.Value, true).AsTask().GetAwaiter().GetResult(), Is.True);
            checkpointSucceeds = true;
            Assert.That(dispatcher.Pump(actor.CreditCurrencyAsync(buyer, new("gems", 5), Guid.NewGuid())).Balance, Is.EqualTo(5));
            provider.FailCommit = true;
            var uncertain = dispatcher.Pump(actor.PurchaseAsync(buyer, "wood", 1));
            Assert.That(uncertain.Status, Is.EqualTo(NpcShopPurchaseStatus.RecoveryRequired));
            Assert.That(native.inventory.Take(50).Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(6));
            var retry = dispatcher.Pump(actor.PurchaseCurrencyAsync(buyer, "wood", 1, uncertain.OperationId!.Value));
            Assert.That(retry.Status, Is.EqualTo(NpcShopPurchaseStatus.RecoveryRequired));
            Assert.That(retry.OperationId, Is.EqualTo(uncertain.OperationId));
            Assert.That(native.inventory.Take(50).Where(i => i.type == ItemID.Wood).Sum(i => i.stack), Is.EqualTo(6));
            Assert.That(checkpoints, Is.EqualTo(4));

            var reconnected = Connect();
            Assert.That(reconnected.SessionId, Is.Not.EqualTo(buyer.SessionId));
            accountSessions.Bind(reconnected, new AccountId(1));
            Conversation();
            Assert.That(dispatcher.Pump(actor.GetQuestsAsync(reconnected)).Single().State, Is.EqualTo(NpcQuestState.Completed));
            Assert.That(dispatcher.Pump(actor.TurnInQuestAsync(reconnected, "torch", 1)).Status, Is.EqualTo(NpcQuestStatus.AlreadyCompleted));
            Assert.That(dispatcher.Pump(actor.GetCurrencyBalanceAsync(reconnected, "gems")).PendingOperationId, Is.EqualTo(uncertain.OperationId));
            Assert.That(dispatcher.Pump(actor.CreditCurrencyAsync(buyer, new("gems", 1), Guid.NewGuid())).Status, Is.EqualTo(NpcEconomyStatus.AccountRequired));
            accountSessions.Bind(reconnected, new AccountId(2));
            Assert.That(dispatcher.Pump(actor.GetQuestsAsync(reconnected)).Single().State, Is.EqualTo(NpcQuestState.Available));
            Assert.That(dispatcher.Pump(actor.GetCurrencyBalanceAsync(reconnected, "gems")).Balance, Is.Zero);
            Assert.That(provider.Invocations, Is.GreaterThan(0));
            // Operator reconciliation after verifying the prior native grant, not an automatic retry.
            Assert.That(store.FinalizeDebitAsync(new NpcAccountScope(world, "npc.wallet.test", new AccountId(1)), uncertain.OperationId!.Value, true).AsTask().GetAwaiter().GetResult(), Is.True);
        }
        finally
        {
            global::Terraria.Main.player[playerSlot] = oldPlayer;
            global::Terraria.Netplay.Clients[playerSlot] = oldClient;
            global::Terraria.NetMessage.buffer[playerSlot] = oldBuffer;
            global::Terraria.NetMessage.buffer[256] = oldBroadcast;
            global::Terraria.Main.npc[npcSlot] = oldNpc;
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Main.ServerSideCharacter = oldSsc;
            global::Terraria.Main.rightWorld = oldRight;
            global::Terraria.Main.bottomWorld = oldBottom;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class PumpDispatcher : IGameThreadDispatcher
    {
        private readonly int id = Environment.CurrentManagedThreadId;
        private readonly ConcurrentQueue<Action> pending = new();
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => Environment.CurrentManagedThreadId == id;
        public void AssertGameThread() => Assert.That(IsGameThread, Is.True);
        public bool TryPost(string name, Action action) { pending.Enqueue(action); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void Drain() { AssertGameThread(); while (pending.TryDequeue(out var action)) action(); }
        public T Pump<T>(ValueTask<T> operation)
        {
            var task = operation.AsTask();
            long started = Stopwatch.GetTimestamp();
            while (!task.IsCompleted)
            {
                Drain();
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native actor test dispatch did not complete.");
                Thread.Sleep(1);
            }
            return task.GetAwaiter().GetResult();
        }
    }

    private sealed class BindingAccounts : IPlayerAccountSessionService
    {
        private readonly object sync = new();
        private PlayerAccountBinding? current;
        public void Bind(PlayerSnapshot player, AccountId account) { lock (sync) current = new(player.Id, player.SessionId!.Value, account, Guid.NewGuid()); }
        public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account) { account = null; return false; }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public bool TryGetBinding(PlayerSnapshot player, out PlayerAccountBinding binding)
        {
            lock (sync) { if (current is { } active && active.PlayerId == player.Id && active.SessionId == player.SessionId) { binding = active; return true; } }
            binding = default; return false;
        }
        public bool TryRunWithBinding<T>(PlayerAccountBinding expected, Func<T> operation, out T result)
        {
            lock (sync) { if (current == expected) { result = operation(); return true; } }
            result = default!; return false;
        }
    }

    private sealed class TrackingEconomyProvider(INpcEconomyProvider inner, PumpDispatcher dispatcher) : INpcEconomyProvider
    {
        private int invocations;
        public int Invocations => Volatile.Read(ref invocations);
        public bool FailCommit { get; set; }
        public Action? AfterReserve { get; set; }
        private void Check() { if (dispatcher.IsGameThread) throw new InvalidOperationException("Provider called on native thread."); Interlocked.Increment(ref invocations); }
        public ValueTask<NpcEconomyBalanceResult> ReadBalanceAsync(NpcAccountScope scope, string currencyId, CancellationToken cancellationToken = default)
        { Check(); return inner.ReadBalanceAsync(scope, currencyId, cancellationToken); }
        public ValueTask<NpcEconomyBalanceResult> CreditAsync(NpcAccountScope scope, NpcEconomyPrice credit, Guid operationId, CancellationToken cancellationToken = default)
        { Check(); return inner.CreditAsync(scope, credit, operationId, cancellationToken); }
        public async ValueTask<NpcEconomyReservationResult> ReserveDebitAsync(NpcAccountScope scope, NpcEconomyPrice price, Guid operationId, CancellationToken cancellationToken = default)
        {
            Check();
            var result = await inner.ReserveDebitAsync(scope, price, operationId, cancellationToken).ConfigureAwait(false);
            if (result.Status == NpcEconomyStatus.Applied) AfterReserve?.Invoke();
            return result;
        }
        public ValueTask<bool> FinalizeDebitAsync(NpcAccountScope scope, Guid operationId, bool committed, CancellationToken cancellationToken = default)
        { Check(); return committed && FailCommit ? ValueTask.FromResult(false) : inner.FinalizeDebitAsync(scope, operationId, committed, cancellationToken); }
    }

    [Test]
    [NonParallelizable]
    public async Task NativePurchaseCommitsOnceForTheExactConversationAndSession()
    {
        const int playerSlot = 2, npcSlot = 0;
        var oldPlayer = global::Terraria.Main.player[playerSlot];
        var oldClient = global::Terraria.Netplay.Clients[playerSlot];
        var oldPlayerBuffer = global::Terraria.NetMessage.buffer[playerSlot];
        var oldBroadcastBuffer = global::Terraria.NetMessage.buffer[256];
        var oldNpc = global::Terraria.Main.npc[npcSlot];
        var oldMode = global::Terraria.Main.netMode;
        bool oldDedicated = global::Terraria.Main.dedServ;
        bool oldSsc = global::Terraria.Main.ServerSideCharacter;
        float oldRight = global::Terraria.Main.rightWorld, oldBottom = global::Terraria.Main.bottomWorld;
        var dispatcher = new InlineDispatcher();
        var world = new WorldId("shop");
        using var boundary = new TerrariaPlayerBoundary(dispatcher, new TerrariaConnectionMetadataRegistry(), world);
        var events = new WorldGameplayEventHub();
        var logger = new QuietLogger();
        using var actors = new TerrariaNpcActorService(dispatcher, world, boundary, events, logger)
        {
            SpawnNative = (x, y, type) =>
            {
                global::Terraria.Main.npc[npcSlot] = new global::Terraria.NPC { whoAmI = npcSlot, active = true,
                    type = type, townNPC = true, life = 100, generation = 3, width = 18, height = 40 };
                return npcSlot;
            }
        };
        try
        {
            var socket = new RecordingSocket();
            var player = new global::Terraria.Player { whoAmI = playerSlot, active = true, name = "Buyer", position = new(100, 100) };
            global::Terraria.Main.player[playerSlot] = player;
            global::Terraria.Main.npc[npcSlot] = new global::Terraria.NPC();
            global::Terraria.Main.rightWorld = global::Terraria.Main.bottomWorld = 10000;
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.netMode = 2;
            var client = new global::Terraria.RemoteClient { Id = playerSlot, State = 10 };
            global::Terraria.Netplay.Clients[playerSlot] = client;
            global::Terraria.NetMessage.buffer[playerSlot] = new global::Terraria.MessageBuffer { whoAmI = playerSlot, broadcast = true };
            global::Terraria.NetMessage.buffer[256] = new global::Terraria.MessageBuffer { whoAmI = 256 };
            client.Activate(socket);
            boundary.Attach();
            global::TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(playerSlot);
            Assert.That(boundary.TryGet(new PlayerId(playerSlot), out var buyer), Is.True);
            Assert.That(buyer!.SessionId, Is.Not.Null);
            global::Terraria.Main.netMode = 0;
            var catalog = new NpcShopCatalog([new("stone", 1, 2, 125, BuyOnce: true, CoinPrice: 125)]);
            var oversized = new NpcShopCatalog([new("sword", ItemID.CopperShortsword, short.MaxValue, 100)]);
            var invalidPrefix = new NpcShopCatalog([new("stone", ItemID.StoneBlock, 1, 100, Prefix: PrefixID.Large)]);
            Assert.That((await actors.CreateAsync(world, new(17, "Merchant", Shop: oversized), new(100, 100))).Status,
                Is.EqualTo(NpcOperationStatus.Invalid));
            Assert.That((await actors.CreateAsync(world, new(17, "Merchant", Shop: invalidPrefix), new(100, 100))).Status,
                Is.EqualTo(NpcOperationStatus.Invalid));
            var created = await actors.CreateAsync(world, new(17, "Merchant", Shop: catalog), new(100, 100));
            var actor = created.Actor!;
            Assert.That(created.Status, Is.EqualTo(NpcOperationStatus.Applied));
            Assert.That(await actor.SetShopAsync(oversized), Is.False);
            Assert.That(await actor.SetShopAsync(invalidPrefix), Is.False);
            Assert.That((await actor.CaptureAsync())!.ShopRevision, Is.EqualTo(1));
            player.inventory[50].SetDefaults(ItemID.GoldCoin);
            player.inventory[50].stack = 1;
            Assert.That((await actor.PurchaseAsync(buyer, "stone", 1)).Status, Is.EqualTo(NpcShopPurchaseStatus.NotInteracting));

            // A normal NPC conversation message followed by its native committed talkNPC state.
            NpcActorInteractedEvent? interaction = null;
            using var conversationSubscription = events.Subscribe<NpcActorInteractedEvent>(value => interaction = value);
            var buffer = new global::Terraria.MessageBuffer { whoAmI = playerSlot };
            void ObserveConversation(int target = npcSlot)
            {
                using var stream = new MemoryStream();
                new NpcTalk { PlayerId = playerSlot, NpcTalkTarget = (short)target }.ToStream(stream, false);
                byte packetId = (byte)PacketTypes.NpcTalk;
                int start = 0, readOffset = 1, length = checked((int)stream.Length + 1), messageType = packetId;
                buffer.readBuffer[0] = packetId;
                stream.ToArray().CopyTo(buffer.readBuffer, readOffset);
                Assert.That(global::TerrariaRuntime.Hooks.MessageBuffer.InvokeGetData(buffer, ref packetId, ref readOffset, ref start, ref length, ref messageType, 256), Is.True);
                player.talkNPC = target;
                dispatcher.RunPending();
            }
            player.position = new(1000, 1000);
            ObserveConversation();
            Assert.That(interaction, Is.Null);
            player.position = new(100, 100);
            Assert.That(await actor.PresentShopAsync(buyer), Is.EqualTo(NpcShopPresentationStatus.NotInteracting));
            ObserveConversation();
            Assert.That(interaction!.Value.Actor, Is.EqualTo(actor.Id));
            Assert.That(interaction.Value.Player.SessionId, Is.EqualTo(buyer.SessionId));
            int nativeSendCounter = global::Terraria.NetMessage.buffer[playerSlot].spamCount;
            Assert.That(await actor.PresentShopAsync(buyer), Is.EqualTo(NpcShopPresentationStatus.Sent));
            Assert.That(socket.LastSend, Is.Not.Empty);
            var firstPresentation = socket.LastBuffer;
            var firstBytes = socket.LastSend;
            Assert.That(await actor.PresentShopAsync(buyer), Is.EqualTo(NpcShopPresentationStatus.Sent));
            Assert.That(socket.LastBuffer, Is.SameAs(firstPresentation));
            Assert.That(global::Terraria.NetMessage.buffer[playerSlot].spamCount, Is.EqualTo(nativeSendCounter),
                "Direct encoded shop delivery must not debit the native SendData counter.");
            Assert.That(await actor.PresentShopAsync(buyer with { SessionId = PlayerSessionId.New() }), Is.EqualTo(NpcShopPresentationStatus.InvalidSession));
            int committed = 0;
            using var subscription = events.Subscribe<NpcActorPurchasedEvent>(_ => committed++);
            using var failingObserver = events.Subscribe<NpcActorPurchasedEvent>(_ => throw new InvalidOperationException("post-commit observer"));
            var beforePurchase = player.inventory.Take(NpcShopPurchasePlan.Slots).Select(NpcShopPurchasePlan.Snapshot).ToArray();
            global::Terraria.Main.netMode = 2;
            global::Terraria.Main.ServerSideCharacter = false;
            Assert.That((await actor.PurchaseAsync(buyer, "stone", 1)).Status, Is.EqualTo(NpcShopPurchaseStatus.InventoryAuthorityRequired));
            Assert.That(player.inventory.Take(NpcShopPurchasePlan.Slots).Select(NpcShopPurchasePlan.Snapshot), Is.EqualTo(beforePurchase));
            Assert.That((await actor.CaptureAsync())!.ShopRevision, Is.EqualTo(1));
            Assert.That(committed, Is.Zero);
            global::Terraria.Main.ServerSideCharacter = true;
            socket.Sends.Clear();
            var purchase = await actor.PurchaseAsync(buyer, "stone", 1);
            Assert.That(purchase.Status, Is.EqualTo(NpcShopPurchaseStatus.Committed));
            Assert.That(purchase.CoinsPaid, Is.EqualTo(125));
            Assert.That(purchase.ShopRevision, Is.EqualTo(2));
            Assert.That(player.inventory.Take(50).Where(item => item.type == 1).Sum(item => item.stack), Is.EqualTo(2));
            Assert.That(player.inventory.Take(54).Sum(item => item.type switch { ItemID.GoldCoin => item.stack * 10000L,
                ItemID.SilverCoin => item.stack * 100L, ItemID.CopperCoin => item.stack, ItemID.PlatinumCoin => item.stack * 1000000L, _ => 0 }),
                Is.EqualTo(9875));
            Assert.That((await actor.PurchaseAsync(buyer, "stone", 1)).Status, Is.EqualTo(NpcShopPurchaseStatus.CatalogChanged));
            Assert.That((await actor.PurchaseAsync(buyer, "stone", 2)).Status, Is.EqualTo(NpcShopPurchaseStatus.OfferUnavailable));
            Assert.That(committed, Is.EqualTo(1));
            var changedSlots = Enumerable.Range(0, NpcShopPurchasePlan.Slots)
                .Where(i => NpcShopPurchasePlan.Snapshot(player.inventory[i]) != beforePurchase[i]).ToArray();
            var slotPackets = socket.Sends.Where(packet => packet.Length > 2 && packet[2] == MessageID.SyncEquipment)
                .Select(packet => (PlayerSlot)TerrariaPacket.Deserialize(packet, 0, packet.Length)).ToArray();
            Assert.That(slotPackets.Select(packet => (int)packet.SlotId), Is.EqualTo(changedSlots),
                "Native transaction must send each changed slot exactly once to the buying client. " + string.Join("\n", logger.Errors));
            foreach (var packet in slotPackets)
            {
                var item = player.inventory[packet.SlotId];
                Assert.That(packet.PlayerId, Is.EqualTo(playerSlot));
                Assert.That(packet.ItemNetId, Is.EqualTo(item.type));
                Assert.That(packet.Stack, Is.EqualTo(item.stack));
                Assert.That(packet.Prefix, Is.EqualTo(item.prefix));
            }
            Assert.That(await actor.PresentShopAsync(buyer), Is.EqualTo(NpcShopPresentationStatus.Sent));
            Assert.That(socket.LastBuffer, Is.Not.SameAs(firstPresentation));
            Assert.That(firstPresentation, Is.EqualTo(firstBytes), "Retained asynchronous sends must not be mutated.");
            int soldOutLength = BinaryPrimitives.ReadUInt16LittleEndian(socket.LastSend);
            Assert.That(((NpcShopItem)TerrariaPacket.Deserialize(socket.LastSend, 0, soldOutLength)).ItemType, Is.Zero);
            byte[] soldOutBuffer = socket.LastBuffer!;
            Assert.That(await actor.SetShopAsync(catalog), Is.True);
            Assert.That(await actor.PresentShopAsync(buyer), Is.EqualTo(NpcShopPresentationStatus.Sent));
            Assert.That(socket.LastBuffer, Is.Not.SameAs(soldOutBuffer));
            Assert.That(socket.LastSend, Is.EqualTo(firstBytes));

            // Dialogues are server-owned menus, not guesses about the stock client's local UI.
            NpcDialogueChoice[] choices = [new("more", "More", "details")];
            var dialogue = new NpcDialogueDefinition("greeting", [new("greeting", "Hello", choices),
                new("details", "Details", [new("bye", "Goodbye")])]);
            choices[0] = new("changed", "This must not affect the graph");
            Assert.That(await actor.SetDialogueAsync(dialogue), Is.True);
            int selected = 0;
            using var dialogueChoice = events.Subscribe<NpcActorDialogueChoiceEvent>(_ => selected++);
            var first = (await actor.BeginDialogueAsync(buyer)).View!;
            Assert.That(first.Choices.Single().Id, Is.EqualTo("more"));
            Assert.That((await actor.ChooseDialogueAsync(buyer, first.Revision, "missing")).Status, Is.EqualTo(NpcDialogueStatus.InvalidChoice));
            var second = (await actor.ChooseDialogueAsync(buyer, first.Revision, "more")).View!;
            Assert.That(second.NodeId, Is.EqualTo("details"));
            Assert.That((await actor.ChooseDialogueAsync(buyer, first.Revision, "more")).Status, Is.EqualTo(NpcDialogueStatus.StaleView));
            ObserveConversation(-1);
            Assert.That((await actor.ChooseDialogueAsync(buyer, second.Revision, "bye")).Status, Is.EqualTo(NpcDialogueStatus.NotInteracting));
            ObserveConversation();
            Assert.That((await actor.ChooseDialogueAsync(buyer, second.Revision, "bye")).Status, Is.EqualTo(NpcDialogueStatus.StaleView));
            Assert.That(selected, Is.EqualTo(1));

            // Alternate item currency stays out of the vanilla coin-price UI; commands use
            // the same detached, authoritative native inventory transaction as coin purchases.
            player.inventory[20].SetDefaults(ItemID.Torch);
            player.inventory[20].stack = 6;
            var alternate = new NpcShopCatalog([new NpcShopOffer("wood", ItemID.Wood, 3, 0, BuyOnce: true)
                { ItemPrice = new(ItemID.Torch, 4) }]);
            Assert.That(await actor.SetShopAsync(alternate), Is.True);
            long alternateRevision = (await actor.CaptureAsync())!.ShopRevision;
            Assert.That(await actor.PresentShopAsync(buyer), Is.EqualTo(NpcShopPresentationStatus.Sent));
            int hiddenLength = BinaryPrimitives.ReadUInt16LittleEndian(socket.LastSend);
            Assert.That(((NpcShopItem)TerrariaPacket.Deserialize(socket.LastSend, 0, hiddenLength)).ItemType, Is.Zero);
            var itemPurchase = await actor.PurchaseAsync(buyer, "wood", alternateRevision);
            Assert.That(itemPurchase.Status, Is.EqualTo(NpcShopPurchaseStatus.Committed));
            Assert.That(itemPurchase.ItemPaid, Is.EqualTo(new NpcItemAmount(ItemID.Torch, 4)));
            Assert.That(itemPurchase.CoinsPaid, Is.Zero);
            Assert.That(player.inventory[20].stack, Is.EqualTo(2));
            Assert.That(player.inventory.Take(50).Where(item => item.type == ItemID.Wood).Sum(item => item.stack), Is.EqualTo(3));

            var quests = new NpcQuestCatalog([new("light", "Light", "Bring two torches", new(ItemID.Torch, 2), new(ItemID.HealingPotion, 1))]);
            Assert.That(await actor.SetQuestsAsync(quests), Is.True);
            long questRevision = (await actor.CaptureAsync())!.QuestRevision;
            Assert.That((await actor.TurnInQuestAsync(buyer, "light", questRevision)).Status, Is.EqualTo(NpcQuestStatus.NotAccepted));
            int questEvents = 0;
            using var questSubscription = events.Subscribe<NpcActorQuestEvent>(_ => questEvents++);
            Assert.That((await actor.AcceptQuestAsync(buyer, "light", questRevision)).Status, Is.EqualTo(NpcQuestStatus.Applied));
            Assert.That((await actor.AcceptQuestAsync(buyer, "light", questRevision)).Status, Is.EqualTo(NpcQuestStatus.AlreadyAccepted));
            global::Terraria.Main.ServerSideCharacter = false;
            var rejected = await actor.TurnInQuestAsync(buyer, "light", questRevision);
            Assert.That(rejected.InventoryFailure, Is.EqualTo(NpcShopPurchaseStatus.InventoryAuthorityRequired));
            Assert.That(player.inventory[20].stack, Is.EqualTo(2));
            Assert.That((await actor.GetQuestsAsync(buyer)).Single().State, Is.EqualTo(NpcQuestState.Accepted));
            global::Terraria.Main.ServerSideCharacter = true;
            player.inventory[20].stack = 1;
            Assert.That((await actor.TurnInQuestAsync(buyer, "light", questRevision)).InventoryFailure, Is.EqualTo(NpcShopPurchaseStatus.InsufficientFunds));
            Assert.That(player.inventory[20].stack, Is.EqualTo(1));
            Assert.That((await actor.GetQuestsAsync(buyer)).Single().State, Is.EqualTo(NpcQuestState.Accepted));
            player.inventory[20].stack = 2;
            Assert.That((await actor.TurnInQuestAsync(buyer, "light", questRevision)).Quest?.State, Is.EqualTo(NpcQuestState.Completed));
            Assert.That(player.inventory[20].IsAir, Is.True);
            Assert.That(player.inventory.Take(50).Where(item => item.type == ItemID.HealingPotion).Sum(item => item.stack), Is.EqualTo(1));
            Assert.That((await actor.TurnInQuestAsync(buyer, "light", questRevision)).Status, Is.EqualTo(NpcQuestStatus.AlreadyCompleted));
            Assert.That(questEvents, Is.EqualTo(2));
            Assert.That(await actor.SetQuestsAsync(quests), Is.True);
            Assert.That((await actor.AcceptQuestAsync(buyer, "light", questRevision)).Status, Is.EqualTo(NpcQuestStatus.CatalogChanged));
            Assert.That((await actor.GetQuestsAsync(buyer)).Single().State, Is.EqualTo(NpcQuestState.Available));

            var waypoints = new PlayerPosition[] { new(100, 100), new(300, 100) };
            Assert.That(await actor.SetIntentAsync(new NpcActorIntent(NpcActorIntentKind.Patrol) { Waypoints = waypoints }), Is.True);
            waypoints[1] = new(0, 100);
            int thinks = 0;
            using var behavior = events.Subscribe<NpcActorThinkEvent>(value => { Assert.That(value.Intent, Is.EqualTo(NpcActorIntentKind.Patrol)); thinks++; });
            var nativeActor = global::Terraria.Main.npc[npcSlot];
            for (int i = 0; i < 60; i++) nativeActor.AI();
            Assert.That(nativeActor.velocity.X, Is.GreaterThan(0));
            Assert.That(thinks, Is.EqualTo(1));
            actor.Dispose();
        }
        finally
        {
            global::Terraria.Main.player[playerSlot] = oldPlayer;
            global::Terraria.Netplay.Clients[playerSlot] = oldClient;
            global::Terraria.NetMessage.buffer[playerSlot] = oldPlayerBuffer;
            global::Terraria.NetMessage.buffer[256] = oldBroadcastBuffer;
            global::Terraria.Main.npc[npcSlot] = oldNpc;
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Main.ServerSideCharacter = oldSsc;
            global::Terraria.Main.rightWorld = oldRight;
            global::Terraria.Main.bottomWorld = oldBottom;
        }
    }

    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        private readonly Queue<Action> pending = new();
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { pending.Enqueue(action); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void RunPending() { while (pending.TryDequeue(out var action)) action(); }
    }
    private sealed class QuietLogger : IOperationsLogger
    {
        public List<string> Errors { get; } = [];
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null)
        { if (exception is not null) Errors.Add($"{text} {exception}"); }
    }
    private sealed class RecordingSocket : global::Terraria.Net.Sockets.ISocket
    {
        public byte[] LastSend { get; private set; } = [];
        public byte[]? LastBuffer { get; private set; }
        public List<byte[]> Sends { get; } = [];
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public global::Terraria.Net.RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
        public void Connect(global::Terraria.Net.RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(global::Terraria.Net.Sockets.SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, global::Terraria.Net.Sockets.SocketSendCallback callback, object? state = null)
        {
            LastBuffer = data;
            LastSend = data.AsSpan(offset, size).ToArray();
            Sends.Add(LastSend);
            callback(state);
        }
        public void AsyncReceive(byte[] data, int offset, int size, global::Terraria.Net.Sockets.SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
    }

    [Test]
    public void CatalogIsDetachedAndEveryPresentationSlotIsFramed()
    {
        NpcShopOffer[] source = [new("torch", 8, 5, 120, BuyOnce: true, CoinPrice: 100), new("stone", 2, 1, 25)];
        var catalog = new NpcShopCatalog(source);
        source[0] = default;
        Assert.That(catalog.Offers[0].Id, Is.EqualTo("torch"));
        Assert.Throws<ArgumentException>(() => new NpcShopCatalog([catalog.Offers[0], catalog.Offers[0]]));
        byte[] data = TerrariaNpcActorService.EncodeShop(catalog, new HashSet<string> { "stone" });
        int offset = 0;
        for (int i = 0; i < NpcShopCatalog.MaximumOffers; i++)
        {
            int length = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));
            var packet = (NpcShopItem)TerrariaPacket.Deserialize(data, offset, length);
            Assert.That(packet.Slot, Is.EqualTo(i));
            Assert.That(packet.ItemType, Is.EqualTo(i == 0 ? 8 : 0));
            if (i == 0)
            {
                Assert.That(packet.Stack, Is.EqualTo(5));
                Assert.That(packet.Value, Is.EqualTo(120));
                Assert.That(packet.Flags, Is.EqualTo(NpcShopItemFlags.BuyOnce));
            }
            offset += length;
        }
        Assert.That(offset, Is.EqualTo(data.Length));
    }

    [Test]
    [NonParallelizable]
    public void NativeClientShopDecoderAppliesCatalogAndIgnoresClosedShop()
    {
        var oldMain = global::Terraria.Main.instance;
        int oldShop = global::Terraria.Main.npcShop;
        int oldMode = global::Terraria.Main.netMode;
        bool oldDedicated = global::Terraria.Main.dedServ;
        var oldConnection = global::Terraria.Netplay.Connection;
        var oldDiagnostics = global::Terraria.Main._activeNetDiagnosticsUI;
        try
        {
            // Only the data structures consumed by the pinned native decoder are needed.
            // Do not construct a graphical game or connect to a network.
            global::Terraria.Main.instance = (global::Terraria.Main)RuntimeHelpers.GetUninitializedObject(typeof(global::Terraria.Main));
            global::Terraria.Main.instance.shop = [new global::Terraria.Chest(), new global::Terraria.Chest()];
            global::Terraria.Netplay.Connection = new global::Terraria.RemoteServer();
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.netMode = 1;
            global::Terraria.Main._activeNetDiagnosticsUI = new global::Terraria.UI.EmptyDiagnosticsUI();
            var shop = global::Terraria.Main.instance.shop[1];
            for (int i = 0; i < shop.item.Length; i++)
            {
                shop.item[i] = new global::Terraria.Item();
                shop.item[i].SetDefaults(ItemID.Torch);
            }

            var catalog = new NpcShopCatalog([
                new("stone", ItemID.StoneBlock, 2, 125, BuyOnce: true),
                new("sword", ItemID.CopperShortsword, 1, 100, Prefix: PrefixID.Large),
                new("sold", ItemID.Wood, 1, 25)]);
            byte[] output = TerrariaNpcActorService.EncodeShop(catalog, new HashSet<string> { "sold" });
            var buffer = new global::Terraria.MessageBuffer { whoAmI = 256 };
            void Decode()
            {
                for (int offset = 0; offset < output.Length;)
                {
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(offset));
                    output.AsSpan(offset + 2, length - 2).CopyTo(buffer.readBuffer);
                    int type;
                    buffer.GetData(0, length - 2, out type);
                    Assert.That(type, Is.EqualTo((int)PacketTypes.NpcShopItem));
                    offset += length;
                }
            }

            global::Terraria.Main.npcShop = 0;
            Decode();
            Assert.That(shop.item.All(i => i.type == ItemID.Torch), Is.True, "A closed shop must remain untouched.");

            global::Terraria.Main.npcShop = 1;
            Decode();
            Assert.Multiple(() =>
            {
                Assert.That(shop.item[0].type, Is.EqualTo(ItemID.StoneBlock));
                Assert.That(shop.item[0].stack, Is.EqualTo(2));
                Assert.That(shop.item[0].value, Is.EqualTo(125));
                Assert.That(shop.item[0].buyOnce, Is.True);
                Assert.That(shop.item[1].type, Is.EqualTo(ItemID.CopperShortsword));
                Assert.That(shop.item[1].prefix, Is.EqualTo(PrefixID.Large));
                Assert.That(shop.item[1].value, Is.EqualTo(100));
                Assert.That(shop.item.Skip(2).All(i => i.IsAir), Is.True, "Sold-out and unused slots clear vanilla stock.");
            });
        }
        finally
        {
            global::Terraria.Main.instance = oldMain;
            global::Terraria.Main.npcShop = oldShop;
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Netplay.Connection = oldConnection;
            global::Terraria.Main._activeNetDiagnosticsUI = oldDiagnostics;
        }
    }

    [Test]
    public void PurchasePlanBalancesCoinsAndItemsWithoutMutatingSource()
    {
        var source = new Slot[NpcShopPurchasePlan.Slots];
        source[0] = new(8, 8, 0, 9999);
        source[50] = new(ItemID.GoldCoin, 2, 0, 9999);
        source[51] = new(ItemID.SilverCoin, 1, 0, 9999);
        var offer = new NpcShopOffer("torch", 8, 7, 0, CoinPrice: 10_125);
        var original = source.ToArray();
        Assert.That(NpcShopPurchasePlan.Prepare(source, offer, 9999, [9999, 9999, 9999, 9999], out var plan),
            Is.EqualTo(NpcShopPurchaseStatus.Committed));
        Assert.That(source, Is.EqualTo(original));
        Assert.That(plan![0].Stack, Is.EqualTo(15));
        Assert.That(plan.Sum(s => s.Type switch { ItemID.GoldCoin => s.Stack * 10_000L, ItemID.SilverCoin => s.Stack * 100L,
            ItemID.CopperCoin => s.Stack, ItemID.PlatinumCoin => s.Stack * 1_000_000L, _ => 0 }), Is.EqualTo(9_975));
        Assert.That(NpcShopPurchasePlan.Prepare(source, offer with { CoinPrice = 30_000 }, 9999, [9999, 9999, 9999, 9999], out plan),
            Is.EqualTo(NpcShopPurchaseStatus.InsufficientFunds));
        Assert.That(plan, Is.Null);
        Assert.That(source, Is.EqualTo(original));
    }

    [Test]
    public void FullInventoryOrUnrepresentableChangeDoesNotCommit()
    {
        var source = Enumerable.Repeat(new Slot(2, 1, 0, 1), NpcShopPurchasePlan.Slots).ToArray();
        source[50] = new(ItemID.GoldCoin, 1, 0, 9999);
        var backup = source.ToArray();
        Assert.That(NpcShopPurchasePlan.Prepare(source, new("item", 8, 1, 0, CoinPrice: 1), 9999,
            [9999, 9999, 9999, 9999], out var plan), Is.EqualTo(NpcShopPurchaseStatus.InventoryFull));
        Assert.That(plan, Is.Null);
        Assert.That(source, Is.EqualTo(backup));
        // A free offer must not reorganize existing coin stacks.
        source[1] = default;
        Assert.That(NpcShopPurchasePlan.Prepare(source, new("gift", 8, 1, 0, CoinPrice: 0), 9999,
            [9999, 9999, 9999, 9999], out plan), Is.EqualTo(NpcShopPurchaseStatus.Committed));
        Assert.That(plan![50], Is.EqualTo(source[50]));
    }

    [Test]
    public void ItemExchangeUsesExactPrefixAndMainInventoryOnlyWithoutPartialCommit()
    {
        var source = new Slot[NpcShopPurchasePlan.Slots];
        source[0] = new(ItemID.Torch, 2, 0, 9999);
        source[1] = new(ItemID.Torch, 5, 1, 9999);
        source[50] = new(ItemID.Torch, 100, 0, 9999);
        var offer = new NpcShopOffer("wood", ItemID.Wood, 3, 0) { ItemPrice = new(ItemID.Torch, 3) };
        var original = source.ToArray();
        Assert.That(NpcShopPurchasePlan.Prepare(source, offer, 9999, [], out var plan), Is.EqualTo(NpcShopPurchaseStatus.InsufficientFunds));
        Assert.That(source, Is.EqualTo(original));
        Assert.That(plan, Is.Null);
        source[2] = new(ItemID.Torch, 2, 0, 9999);
        original = source.ToArray();
        Assert.That(NpcShopPurchasePlan.Prepare(source, offer, 9999, [], out plan), Is.EqualTo(NpcShopPurchaseStatus.Committed));
        Assert.That(source, Is.EqualTo(original));
        Assert.That(plan![1], Is.EqualTo(source[1]));
        Assert.That(plan[50], Is.EqualTo(source[50]));
        Assert.That(plan.Take(50).Where(s => s.Type == ItemID.Torch && s.Prefix == 0).Sum(s => s.Stack), Is.EqualTo(1));
        Assert.That(plan.Take(50).Where(s => s.Type == ItemID.Wood).Sum(s => s.Stack), Is.EqualTo(3));
    }

    [Test]
    public void DialogueAndQuestCatalogRejectIncompleteGraphsAndDuplicateIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new NpcDialogueDefinition("start", [new("start", "Hello", [new("go", "Go", "missing")])]));
        Assert.Throws<ArgumentException>(() => new NpcDialogueDefinition("start", [new("start", "Hello", [new("go", "Go"), new("go", "Again")])]));
        var quest = new NpcQuestDefinition("a", "A", "Collect", new(ItemID.Torch, 2), new(ItemID.Wood, 1));
        Assert.Throws<ArgumentException>(() => new NpcQuestCatalog([quest, quest]));
        Assert.Throws<ArgumentException>(() => new NpcShopCatalog([new NpcShopOffer("a", ItemID.Wood, 1, 0, CoinPrice: 1)
            { ItemPrice = new(ItemID.Torch, 1) }]));
    }

    [Test]
    public void InteractionStateNeverCarriesMenusOrQuestAcceptanceIntoReplacementSession()
    {
        var player = new PlayerSnapshot(new PlayerId(1), "Player", false, 100, 100, 20, 20, default, false, 0)
            { SessionId = PlayerSessionId.New() };
        var dialogue = new NpcDialogueDefinition("start", [new("start", "Hello", [new("close", "Close")])]);
        var quests = new NpcQuestCatalog([new("a", "A", "Bring a torch", new(ItemID.Torch, 1), new(ItemID.Wood, 1))]);
        var state = new NpcActorInteractionState(dialogue, quests);
        state.OnConversation(player);
        var view = state.Begin(player).View!;
        Assert.That(state.Accept(player, "a", state.QuestRevision).Status, Is.EqualTo(NpcQuestStatus.Applied));
        var replacement = player with { SessionId = PlayerSessionId.New() };
        state.OnConversation(replacement);
        Assert.That(state.Choose(replacement, view.Revision, "close", out _).Status, Is.EqualTo(NpcDialogueStatus.StaleView));
        Assert.That(state.GetQuests(replacement).Single().State, Is.EqualTo(NpcQuestState.Available));
    }
}
