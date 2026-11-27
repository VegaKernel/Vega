using NUnit.Framework;
using Multiplicity.Packets;
using System.Buffers.Binary;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Modules;
using Vega.Core.Scenes;
using Vega.PluginSdk;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class NativeVirtualChestTransferTests
{
    [Test]
    public async Task ManagedGuiPresentsStockChestAndCommitsOnlyBalancedPluginApprovedMovement()
    {
        const int slot = 3;
        var world = new WorldId("managed-native-chest");
        var dispatcher = new InlineDispatcher();
        var scenes = new SceneRuntime();
        using var boundary = new TerrariaPlayerBoundary(dispatcher,new TerrariaConnectionMetadataRegistry(),world);
        var inventory = new TerrariaPlayerInventoryService(dispatcher,boundary);
        var oldPlayer = global::Terraria.Main.player[slot];
        var oldClients = (global::Terraria.RemoteClient[])global::Terraria.Netplay.Clients.Clone();
        var oldBuffers = (global::Terraria.MessageBuffer[])global::Terraria.NetMessage.buffer.Clone();
        var oldChests = global::Terraria.Main.chest;
        var oldChestCoordinates = global::Terraria.Chest._chestsByCoords;
        var oldTiles = global::Terraria.Main.tile;
        var oldConnection = global::Terraria.Netplay.Connection;
        var oldDiagnostics = global::Terraria.Main._activeNetDiagnosticsUI;
        var oldGlowTime = global::Terraria.UI.ItemSlot.inventoryGlowTimeChest;
        var oldGlowHue = global::Terraria.UI.ItemSlot.inventoryGlowHueChest;
        var oldCoinEntries = global::Terraria.UI.CoinSlot.ChestEntries;
        int oldMode = global::Terraria.Main.netMode, oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY, oldMyPlayer = global::Terraria.Main.myPlayer;
        bool oldDedicated = global::Terraria.Main.dedServ,oldSsc = global::Terraria.Main.ServerSideCharacter,oldInventoryUi = global::Terraria.Main.playerInventory,
            oldPips = global::Terraria.Main.PipsUseGrid;
        try
        {
            global::Terraria.Main.chest = new global::Terraria.Chest[oldChests.Length];
            global::Terraria.Chest._chestsByCoords = [];
            global::Terraria.Main.maxTilesX = global::Terraria.Main.maxTilesY = 20;
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.netMode = 2;
            global::Terraria.Main.ServerSideCharacter = true;
            var player = new global::Terraria.Player { active = true,whoAmI = slot,name = "Viewer",position = new(160,160),chest = -1 };
            global::Terraria.Main.player[slot] = player;
            for(int i=0;i<global::Terraria.NetMessage.buffer.Length;i++) global::Terraria.NetMessage.buffer[i] = new global::Terraria.MessageBuffer { whoAmI=i,broadcast=i==slot };
            for(int i=0;i<global::Terraria.Netplay.Clients.Length;i++) global::Terraria.Netplay.Clients[i] = new global::Terraria.RemoteClient { Id=i };
            var client = new global::Terraria.RemoteClient { Id=slot,State=10 };
            global::Terraria.Netplay.Clients[slot] = client;
            var socket = new RecordingSocket();
            client.Activate(socket); client.State = 10;
            boundary.Attach();
            boundary.Opened += opened => scenes.OpenSession(world,opened);
            TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(slot);
            Assert.That(boundary.TryGet(new PlayerId(slot),out var snapshot),Is.True);
            VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world,null,null,null,null,scenes,inventory));
            await using var scope = new HotModuleRegistrationScope(new NullRegistry(),new QuietLogger(),"Plugin.GUIView",Path.GetTempPath(),playerDirectory:boundary);
            var fake = ((IVegaPluginContext)scope).World.FakeProvider;
            Assert.That(fake.SupportsManagedVirtualChestGui,Is.True);
            var area = new SceneTileArea(2,2,[new(21,FrameX:0,FrameY:0,Flags:SceneTileFlags.Active),new(21,FrameX:18,FrameY:0,Flags:SceneTileFlags.Active),
                new(21,FrameX:0,FrameY:18,Flags:SceneTileFlags.Active),new(21,FrameX:18,FrameY:18,Flags:SceneTileFlags.Active)]);
            using var provider = fake.CreatePersonal("managed chest",new(10,10,2,2),area,[SceneSessionObserver.From(snapshot!)]);
            using var chest = provider.CreateVirtualChest(0,0,"Managed",[new(0,ItemID.StoneBlock,10,0)]);
            Assert.That(scenes.TryRequestVirtualChestOpen(snapshot!,10,10),Is.True);
            Assert.That(chest.TryTakeOpenRequest(out var open),Is.True);
            global::Terraria.Chest.CreateWorldChest(0,10,10);
            Assert.That(chest.TryPresentManagedGui(open!.Value),Is.False);
            Assert.That(scenes.IsManagedChestPlayer(snapshot!),Is.False);
            global::Terraria.Chest.RemoveChest(0);
            Assert.That(chest.TryPresentManagedGui(open!.Value),Is.True);
            byte[] opening = socket.LastSend;
            var packets = Split(opening);
            Assert.That(packets.Count,Is.EqualTo(42));
            Assert.That(packets[0][2],Is.EqualTo((byte)PacketTypes.ChestName));
            var opened = (ChestOpen)TerrariaPacket.Deserialize(packets[^1],0,packets[^1].Length);
            short alias = opened.ChestId;
            Assert.That(alias,Is.GreaterThanOrEqualTo(0));
            Assert.That(global::Terraria.Main.chest.All(value => value is null),Is.True);
            Assert.That(player.chest,Is.EqualTo(-1));
            int mouse = PlayerItemSlotID.InventoryMouseItem;
            Assert.That(mouse,Is.EqualTo(58));

            var now = DateTimeOffset.UtcNow;
            Assert.That(scenes.ObserveManagedChest(snapshot!,alias,new(0,ItemID.StoneBlock,6,0),now),Is.True);
            Assert.That(chest.TryTakeManagedGuiTransfer(out _),Is.False);
            Assert.That(scenes.ObserveManagedInventory(snapshot!,new(mouse,ItemID.StoneBlock,4),now),Is.True);
            Assert.That(chest.TryTakeManagedGuiTransfer(out var withdraw),Is.True);
            Assert.That(chest.Definition.Items.Single().Stack,Is.EqualTo(10));
            Assert.That(player.inventory[mouse].IsAir,Is.True);
            Assert.That(chest.AcceptManagedGuiTransfer(withdraw!.Value).Status,Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(chest.Definition.Items.Single().Stack,Is.EqualTo(6));
            Assert.That(player.inventory[mouse].stack,Is.EqualTo(4));
            byte[] refreshed = socket.LastSend;
            Assert.That(Split(refreshed).Count,Is.EqualTo(41));
            Assert.That(chest.AcceptManagedGuiTransfer(withdraw.Value).Status,Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));

            now = DateTimeOffset.UtcNow;
            Assert.That(scenes.ObserveManagedInventory(snapshot!,new(mouse,0,0),now),Is.True);
            Assert.That(scenes.ObserveManagedChest(snapshot!,alias,new(0,ItemID.StoneBlock,10,0),now),Is.True);
            Assert.That(chest.TryTakeManagedGuiTransfer(out var deposit),Is.True);
            Assert.That(chest.AcceptManagedGuiTransfer(deposit!.Value).Status,Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(chest.Definition.Items.Single().Stack,Is.EqualTo(10));
            Assert.That(player.inventory[mouse].IsAir,Is.True);
            Assert.That(chest.TryCloseManagedGui(SceneSessionObserver.From(snapshot!)),Is.True);
            socket.Packets.Clear();
            scenes.TickManagedChestGuis(boundary,DateTimeOffset.UtcNow);
            Assert.That(socket.Packets.Any(bytes => bytes[2] == (byte)PacketTypes.ChestOpen),Is.True);
            var close = socket.Packets.First(bytes => bytes[2] == (byte)PacketTypes.ChestOpen);
            Assert.That(((ChestOpen)TerrariaPacket.Deserialize(close,0,close.Length)).ChestId,Is.EqualTo(-1));
            Assert.That(scenes.IsManagedChestPlayer(snapshot!),Is.True);
            Assert.That(scenes.ObserveManagedInventory(snapshot!,new(mouse,ItemID.StoneBlock,4),DateTimeOffset.UtcNow),Is.True);
            scenes.TickManagedChestGuis(boundary,DateTimeOffset.UtcNow);
            Assert.That(player.inventory[mouse].IsAir,Is.True);
            Assert.That(global::Terraria.Main.chest.All(value => value is null),Is.True);

            // Headless stock client decoder consumes the server-produced metadata/open/close responses.
            var grid = new global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>(20,20);
            for(int x=0;x<20;x++) for(int y=0;y<20;y++) grid[x,y] = new global::Terraria.Tile();
            for(int x=10;x<12;x++) for(int y=10;y<12;y++)
            { grid[x,y].type=TileID.Containers; grid[x,y].active(true); grid[x,y].frameX=(short)((x-10)*18); grid[x,y].frameY=(short)((y-10)*18); }
            global::Terraria.Main.tile = grid;
            global::Terraria.Main.netMode = 1;
            global::Terraria.Main.myPlayer = slot;
            global::Terraria.Netplay.Connection = new global::Terraria.RemoteServer();
            global::Terraria.Main._activeNetDiagnosticsUI = new global::Terraria.UI.EmptyDiagnosticsUI();
            // Native ChestOpen initializes UI glow/coin metadata; provide ordinary backing
            // arrays without creating a graphics window or changing the decoder.
            global::Terraria.UI.ItemSlot.inventoryGlowTimeChest = new int[40];
            global::Terraria.UI.ItemSlot.inventoryGlowHueChest = new float[40];
            global::Terraria.UI.CoinSlot.ChestEntries = new global::Terraria.UI.CoinSlot.CoinEntry[40];
            var buffer = new global::Terraria.MessageBuffer { whoAmI=256 };
            void Decode(byte[] bytes)
            { bytes.AsSpan(2).CopyTo(buffer.readBuffer); buffer.GetData(0,bytes.Length-2,out int kind); Assert.That(kind,Is.EqualTo(bytes[2])); }
            foreach(var packet in packets) Decode(packet);
            Assert.That(global::Terraria.Main.chest[alias].item[0].stack,Is.EqualTo(10));
            Assert.That(player.chest,Is.EqualTo(alias));
            foreach(var packet in Split(refreshed)) Decode(packet);
            Assert.That(global::Terraria.Main.chest[alias].item[0].stack,Is.EqualTo(6));
            Decode(close);
            Assert.That(player.chest,Is.EqualTo(-1));
        }
        finally
        {
            VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);
            global::Terraria.Main.player[slot]=oldPlayer;
            Array.Copy(oldClients,global::Terraria.Netplay.Clients,oldClients.Length);
            Array.Copy(oldBuffers,global::Terraria.NetMessage.buffer,oldBuffers.Length);
            global::Terraria.Main.chest=oldChests;
            global::Terraria.Chest._chestsByCoords=oldChestCoordinates;
            global::Terraria.Main.tile=oldTiles;
            global::Terraria.Main.maxTilesX=oldWidth; global::Terraria.Main.maxTilesY=oldHeight;
            global::Terraria.Main.netMode=oldMode; global::Terraria.Main.myPlayer=oldMyPlayer;
            global::Terraria.Main.dedServ=oldDedicated; global::Terraria.Main.ServerSideCharacter=oldSsc;
            global::Terraria.Main.playerInventory=oldInventoryUi; global::Terraria.Main.PipsUseGrid=oldPips;
            global::Terraria.Netplay.Connection=oldConnection; global::Terraria.Main._activeNetDiagnosticsUI=oldDiagnostics;
            global::Terraria.UI.ItemSlot.inventoryGlowTimeChest=oldGlowTime; global::Terraria.UI.ItemSlot.inventoryGlowHueChest=oldGlowHue;
            global::Terraria.UI.CoinSlot.ChestEntries=oldCoinEntries;
        }
    }

    private static List<byte[]> Split(byte[] data)
    {
        List<byte[]> packets = [];
        for (int offset=0;offset<data.Length;)
        {
            int length=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            if (length < 3 || length > data.Length-offset) throw new InvalidDataException("Invalid framed native response.");
            packets.Add(data.AsSpan(offset,length).ToArray()); offset+=length;
        }
        return packets;
    }
    [Test]
    public async Task PluginChestTransfersNativeItemsOnceAndPreservesBothSidesOnRejection()
    {
        const int slot = 3;
        var world = new WorldId("virtual-chest-native");
        var dispatcher = new InlineDispatcher();
        var scenes = new SceneRuntime();
        using var boundary = new TerrariaPlayerBoundary(dispatcher, new TerrariaConnectionMetadataRegistry(), world);
        var inventory = new TerrariaPlayerInventoryService(dispatcher, boundary);
        var oldPlayer = global::Terraria.Main.player[slot];
        var oldClients = (global::Terraria.RemoteClient[])global::Terraria.Netplay.Clients.Clone();
        int oldMode = global::Terraria.Main.netMode;
        bool oldDedicated = global::Terraria.Main.dedServ;
        bool oldSsc = global::Terraria.Main.ServerSideCharacter;
        var oldBuffers = (global::Terraria.MessageBuffer[])global::Terraria.NetMessage.buffer.Clone();
        try
        {
            var player = new global::Terraria.Player { active = true, whoAmI = slot, name = "Buyer", position = new(160, 160) };
            global::Terraria.Main.player[slot] = player;
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.netMode = 2;
            for (int i = 0; i < global::Terraria.NetMessage.buffer.Length; i++)
                global::Terraria.NetMessage.buffer[i] = new global::Terraria.MessageBuffer { whoAmI = i, broadcast = i == slot };
            for (int i = 0; i < global::Terraria.Netplay.Clients.Length; i++)
                global::Terraria.Netplay.Clients[i] = new global::Terraria.RemoteClient { Id = i };
            var client = new global::Terraria.RemoteClient { Id = slot, State = 10 };
            global::Terraria.Netplay.Clients[slot] = client;
            var socket = new RecordingSocket();
            client.Activate(socket);
            client.State = 10;
            boundary.Attach();
            boundary.Opened += opened => scenes.OpenSession(world, opened);
            boundary.Closed += closed => scenes.CloseSession(world, closed);
            TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(slot);
            Assert.That(boundary.TryGet(new PlayerId(slot), out var snapshot), Is.True);
            Assert.That(snapshot!.SessionId, Is.Not.Null);
            // Native server output is counted by an in-process recording socket.

            VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, scenes, inventory));
            await using var scope = new HotModuleRegistrationScope(new NullRegistry(), new QuietLogger(), "Plugin.NativeChest", Path.GetTempPath(), playerDirectory: boundary);
            var fake = ((IVegaPluginContext)scope).World.FakeProvider;
            Assert.That(fake.SupportsVirtualChestTransfers, Is.True);
            using var provider = fake.CreatePersonal("Reward", new(10, 10, 2, 2),
                SceneTileArea.Fill(2, 2, new SceneTile(21, Flags: SceneTileFlags.Active)), [SceneSessionObserver.From(snapshot)]);
            using var chest = provider.CreateVirtualChest(0, 0, "Reward", [new(0, ItemID.StoneBlock, 10, 0)]);

            SceneVirtualChestOpenRequest Open()
            {
                Assert.That(scenes.TryRequestVirtualChestOpen(snapshot, 10, 10), Is.True);
                Assert.That(chest.TryTakeOpenRequest(out var request), Is.True);
                return request!.Value;
            }

            var initial = await inventory.CaptureAsync(snapshot);
            Assert.That(initial, Is.Not.Null);
            var first = new SceneVirtualChestTransferRequest(Open(), initial!.Revision, 0, 0, 4, VirtualChestTransferDirection.Withdraw);
            global::Terraria.Main.ServerSideCharacter = false;
            Assert.That(chest.Transfer(first).Status, Is.EqualTo(VirtualChestTransferStatus.AuthorityRequired));
            global::Terraria.Main.ServerSideCharacter = true;
            Assert.That(chest.Transfer(first with { Open = first.Open with { RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1) } }).Status,
                Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));
            var withdrawn = chest.Transfer(first);
            Assert.That(withdrawn.Status, Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(withdrawn.Error, Is.Null);
            Assert.That(socket.LastSend, Has.Length.GreaterThanOrEqualTo(3));
            Assert.That(socket.LastSend[2], Is.EqualTo(MessageID.SyncEquipment));
            Assert.That(chest.Definition.Revision, Is.EqualTo(2));
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(6));
            Assert.That(player.inventory[0].type, Is.EqualTo(ItemID.StoneBlock));
            Assert.That(player.inventory[0].stack, Is.EqualTo(4));
            Assert.That(chest.Transfer(first).Status, Is.EqualTo(VirtualChestTransferStatus.RevisionConflict));
            Assert.That(chest.Transfer(first with { InventoryRevision = withdrawn.InventoryRevision }).Status, Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));

            var deposit = new SceneVirtualChestTransferRequest(Open(), withdrawn.InventoryRevision, 0, 0, 2, VirtualChestTransferDirection.Deposit);
            player.position = new(2000, 2000);
            Assert.That(chest.Transfer(deposit).Status, Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));
            player.position = new(160, 160);
            using (var cover = scenes.CreateScene(world, "cover", "test"))
            {
                cover.AddOverlay(new(default, "cover", new(10, 10, 1, 1), 5, SceneObserverScope.Global, SceneTileArea.Fill(1, 1, SceneTile.Air)));
                Assert.That(chest.Transfer(deposit).Status, Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));
            }
            Assert.That(chest.Transfer(deposit with { Amount = 99 }).Status, Is.EqualTo(VirtualChestTransferStatus.InvalidItem));
            var deposited = chest.Transfer(deposit);
            Assert.That(deposited.Status, Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(8));
            Assert.That(player.inventory[0].stack, Is.EqualTo(2));
            var final = new SceneVirtualChestTransferRequest(Open(), deposited.InventoryRevision, 0, 0, 2, VirtualChestTransferDirection.Deposit);
            Assert.That(chest.Transfer(final).Status, Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(10));
            Assert.That(player.inventory[0].IsAir, Is.True);
            provider.Move(1, 0); // A later provider edit must not restore stale chest contents.
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(snapshot), 11, 10, out var resolved), Is.True);
            Assert.That(resolved!.Value.Definition.Revision, Is.EqualTo(4));
            Assert.That(resolved.Value.Definition.Items.Single().Stack, Is.EqualTo(10));

            // Editable signs retain the updated revision through subsequent SDK edits and movement.
            Assert.That(fake.SupportsVirtualSignEdits, Is.True);
            using var signProvider = fake.CreatePersonal("Sign", new(10, 13, 2, 2),
                SceneTileArea.Fill(2, 2, new SceneTile(55, Flags: SceneTileFlags.Active)), [SceneSessionObserver.From(snapshot)]);
            using var sign = signProvider.CreateVirtualSign(0, 0, "Old", allowEdits: true);
            Assert.That(scenes.TryRequestVirtualSignRead(snapshot, 10, 13), Is.True);
            Assert.That(scenes.TryRequestVirtualSignEdit(snapshot, 10, 13, "New"), Is.True);
            Assert.That(sign.TryTakeEditRequest(out var edit), Is.True);
            Assert.That(sign.TryAcceptEdit(edit!.Value), Is.True);
            Assert.That(sign.Definition.Text, Is.EqualTo("New"));
            Assert.That(sign.TrySetText(2, "Updated"), Is.True);
            Assert.That(sign.Definition.AllowEdits, Is.True);
            signProvider.Move(1, 0);
            Assert.That(scenes.TryResolveVirtualSign(world, SceneSessionObserver.From(snapshot), 11, 13, out var signResolved), Is.True);
            Assert.That(signResolved!.Value.Definition.Text, Is.EqualTo("Updated"));
        }
        finally
        {
            VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);
            global::Terraria.Main.player[slot] = oldPlayer;
            Array.Copy(oldClients, global::Terraria.Netplay.Clients, oldClients.Length);
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Main.ServerSideCharacter = oldSsc;
            Array.Copy(oldBuffers, global::Terraria.NetMessage.buffer, oldBuffers.Length);
        }
    }

    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { action(); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
    }

    private sealed class RecordingSocket : ISocket
    {
        public byte[] LastSend { get; private set; } = [];
        public List<byte[]> Packets { get; } = [];
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
        public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
        {
            LastSend = data.AsSpan(offset, size).ToArray();
            Packets.Add(LastSend);
            callback(state);
        }
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
    }

    private sealed class QuietLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
    private sealed class NullRegistry : ICommandRegistry
    {
        public IDisposable Register(ICommandHandler handler) => throw new NotSupportedException();
    }
}
