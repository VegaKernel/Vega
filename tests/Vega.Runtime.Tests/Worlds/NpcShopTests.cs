using System.Buffers.Binary;
using Multiplicity.Packets;
using NUnit.Framework;
using Terraria.ID;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
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
    public async Task NativePurchaseCommitsOnceForTheExactConversationAndSession()
    {
        const int playerSlot = 2, npcSlot = 0;
        var oldPlayer = global::Terraria.Main.player[playerSlot];
        var oldClient = global::Terraria.Netplay.Clients[playerSlot];
        var oldNpc = global::Terraria.Main.npc[npcSlot];
        var oldMode = global::Terraria.Main.netMode;
        bool oldDedicated = global::Terraria.Main.dedServ;
        float oldRight = global::Terraria.Main.rightWorld, oldBottom = global::Terraria.Main.bottomWorld;
        var dispatcher = new InlineDispatcher();
        var world = new WorldId("shop");
        using var boundary = new TerrariaPlayerBoundary(dispatcher, new TerrariaConnectionMetadataRegistry(), world);
        var events = new WorldGameplayEventHub();
        using var actors = new TerrariaNpcActorService(dispatcher, world, boundary, events, new QuietLogger())
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
            client.Activate(socket);
            boundary.Attach();
            global::TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(playerSlot);
            Assert.That(boundary.TryGet(new PlayerId(playerSlot), out var buyer), Is.True);
            Assert.That(buyer!.SessionId, Is.Not.Null);
            global::Terraria.Main.netMode = 0;
            var catalog = new NpcShopCatalog([new("stone", 1, 2, 125, BuyOnce: true, CoinPrice: 125)]);
            var created = await actors.CreateAsync(world, new(17, "Merchant", Shop: catalog), new(100, 100));
            var actor = created.Actor!;
            Assert.That(created.Status, Is.EqualTo(NpcOperationStatus.Applied));
            player.inventory[50].SetDefaults(ItemID.GoldCoin);
            player.inventory[50].stack = 1;
            Assert.That((await actor.PurchaseAsync(buyer, "stone", 1)).Status, Is.EqualTo(NpcShopPurchaseStatus.NotInteracting));

            // A normal NPC conversation message followed by its native committed talkNPC state.
            NpcActorInteractedEvent? interaction = null;
            using var conversationSubscription = events.Subscribe<NpcActorInteractedEvent>(value => interaction = value);
            using var stream = new MemoryStream();
            new NpcTalk { PlayerId = playerSlot, NpcTalkTarget = npcSlot }.ToStream(stream, false);
            var buffer = new global::Terraria.MessageBuffer { whoAmI = playerSlot };
            byte packetId = (byte)PacketTypes.NpcTalk;
            int start = 0, readOffset = 1, length = checked((int)stream.Length + 1), messageType = packetId;
            buffer.readBuffer[0] = packetId;
            stream.ToArray().CopyTo(buffer.readBuffer, readOffset);
            Assert.That(global::TerrariaRuntime.Hooks.MessageBuffer.InvokeGetData(buffer, ref packetId, ref readOffset, ref start, ref length, ref messageType, 256), Is.True);
            player.talkNPC = npcSlot;
            dispatcher.RunPending();
            Assert.That(interaction!.Value.Actor, Is.EqualTo(actor.Id));
            Assert.That(interaction.Value.Player.SessionId, Is.EqualTo(buyer.SessionId));
            Assert.That(await actor.PresentShopAsync(buyer), Is.EqualTo(NpcShopPresentationStatus.Sent));
            Assert.That(socket.LastSend, Is.Not.Empty);
            Assert.That(await actor.PresentShopAsync(buyer with { SessionId = PlayerSessionId.New() }), Is.EqualTo(NpcShopPresentationStatus.InvalidSession));
            int committed = 0;
            using var subscription = events.Subscribe<NpcActorPurchasedEvent>(_ => committed++);
            using var failingObserver = events.Subscribe<NpcActorPurchasedEvent>(_ => throw new InvalidOperationException("post-commit observer"));
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
            actor.Dispose();
        }
        finally
        {
            global::Terraria.Main.player[playerSlot] = oldPlayer;
            global::Terraria.Netplay.Clients[playerSlot] = oldClient;
            global::Terraria.Main.npc[npcSlot] = oldNpc;
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.dedServ = oldDedicated;
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
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
    private sealed class RecordingSocket : global::Terraria.Net.Sockets.ISocket
    {
        public byte[] LastSend { get; private set; } = [];
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public global::Terraria.Net.RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
        public void Connect(global::Terraria.Net.RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(global::Terraria.Net.Sockets.SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, global::Terraria.Net.Sockets.SocketSendCallback callback, object? state = null) =>
            LastSend = data.AsSpan(offset, size).ToArray();
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
}
