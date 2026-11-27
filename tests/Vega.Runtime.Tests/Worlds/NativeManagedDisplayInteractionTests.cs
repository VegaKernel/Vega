using Multiplicity.Packets;
using NUnit.Framework;
using System.Reflection;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
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
using Vega.Terraria.Scenes;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class NativeManagedDisplayInteractionTests
{
    [Test]
    public async Task ItemFrameAndWeaponRackCommitNativeInventoryAndProjectUpdatedMetadataWithoutWorldEntityRegistration()
    {
        const int slot=3;
        var world=new WorldId("managed-display-native");
        var dispatcher=new InlineDispatcher();
        var scenes=new SceneRuntime();
        using var boundary=new TerrariaPlayerBoundary(dispatcher,new TerrariaConnectionMetadataRegistry(),world);
        var inventory=new TerrariaPlayerInventoryService(dispatcher,boundary);
        var oldPlayer=global::Terraria.Main.player[slot];
        var oldClients=(global::Terraria.RemoteClient[])global::Terraria.Netplay.Clients.Clone();
        var oldBuffers=(global::Terraria.MessageBuffer[])global::Terraria.NetMessage.buffer.Clone();
        var oldConnection=global::Terraria.Netplay.Connection;
        var oldDiagnostics=global::Terraria.Main._activeNetDiagnosticsUI;
        int oldMode=global::Terraria.Main.netMode,oldWidth=global::Terraria.Main.maxTilesX,oldHeight=global::Terraria.Main.maxTilesY;
        bool oldDedicated=global::Terraria.Main.dedServ,oldSsc=global::Terraria.Main.ServerSideCharacter;
        var oldManager=TileEntity.manager;
        var oldById=TileEntity.ByID;
        var oldByPosition=TileEntity.ByPosition;
        var oldUpdates=TileEntity.UpdateEntities;
        int oldNext=TileEntity.TileEntitiesNextID;
        byte oldFrame=TEItemFrame.EntityTypeID,oldRack=TEWeaponsRack.EntityTypeID,oldDummy=TETrainingDummy.EntityTypeID;
        var updateStart=typeof(TileEntity).GetField("_UpdateStart",BindingFlags.Static|BindingFlags.NonPublic)!;
        var updateEnd=typeof(TileEntity).GetField("_UpdateEnd",BindingFlags.Static|BindingFlags.NonPublic)!;
        object? oldStart=updateStart.GetValue(null),oldEnd=updateEnd.GetValue(null);
        try
        {
            global::Terraria.Main.maxTilesX=global::Terraria.Main.maxTilesY=40;
            global::Terraria.Main.dedServ=true;
            global::Terraria.Main.netMode=2;
            global::Terraria.Main.ServerSideCharacter=true;
            TileEntity.manager=new TileEntitiesManager();
            TileEntity.manager.Register(new TETrainingDummy());
            TileEntity.manager.Register(new TEItemFrame());
            TileEntity.manager.Register(new TEWeaponsRack());
            TileEntity.ByID=[]; TileEntity.ByPosition=[]; TileEntity.UpdateEntities=[]; TileEntity.TileEntitiesNextID=100;
            var player=new global::Terraria.Player{active=true,whoAmI=slot,name="Artist",position=new(160,160),chest=-1};
            player.inventory[5].SetDefaults(ItemID.StoneBlock);player.inventory[5].stack=3;
            player.inventory[6].SetDefaults(ItemID.CopperShortsword);player.inventory[6].Prefix(PrefixID.Large);player.inventory[6].stack=1;
            global::Terraria.Main.player[slot]=player;
            for(int i=0;i<global::Terraria.NetMessage.buffer.Length;i++) global::Terraria.NetMessage.buffer[i]=new global::Terraria.MessageBuffer{whoAmI=i,broadcast=i==slot};
            for(int i=0;i<global::Terraria.Netplay.Clients.Length;i++) global::Terraria.Netplay.Clients[i]=new global::Terraria.RemoteClient{Id=i};
            var client=new global::Terraria.RemoteClient{Id=slot,State=10};
            global::Terraria.Netplay.Clients[slot]=client;
            var socket=new RecordingSocket();client.Activate(socket);client.State=10;
            boundary.Attach();boundary.Opened+=opened=>scenes.OpenSession(world,opened);
            TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(slot);
            Assert.That(boundary.TryGet(new PlayerId(slot),out var snapshot),Is.True);
            VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world,null,null,null,null,scenes,inventory));
            await using var scope=new HotModuleRegistrationScope(new NullRegistry(),new QuietLogger(),"Plugin.DisplayInteraction",Path.GetTempPath(),playerDirectory:boundary);
            var fake=((IVegaPluginContext)scope).World.FakeProvider;
            Assert.That(fake.SupportsManagedDisplayInteractions,Is.True);
            var observer=SceneSessionObserver.From(snapshot!);
            using var frameProvider=fake.CreatePersonal("frame",new(10,10,2,2),Tiles(395,2),[observer]);
            using var rackProvider=fake.CreatePersonal("rack",new(14,10,3,3),Tiles(471,3),[observer]);
            using var frame=frameProvider.CreateVirtualItemFrame(0,0,0,0);
            using var rack=rackProvider.CreateVirtualDisplay(SceneVirtualDisplayKind.WeaponsRack,0,0,[],[]);
            var canonical=new TEItemFrame{ID=1,Position=new Point16(10,10),type=TEItemFrame.EntityTypeID};
            TileEntity.ByID.Add(1,canonical);TileEntity.ByPosition.Add(canonical.Position,canonical);
            Assert.That(frame.TryArmManagedInteraction(snapshot!),Is.False);
            Assert.That(scenes.IsManagedChestPlayer(snapshot!),Is.False);
            TileEntity.ByID.Clear();TileEntity.ByPosition.Clear();
            Assert.That(frame.TryArmManagedInteraction(snapshot!),Is.True);
            var now=DateTimeOffset.UtcNow;
            Assert.That(scenes.ObserveManagedDisplayPlacement(snapshot!,ManagedDisplayKind.ItemFrame,10,10,new(ItemID.StoneBlock,1),now),Is.True);
            Assert.That(frame.TryTakeManagedTransfer(out _),Is.False);
            Assert.That(scenes.ObserveManagedInventory(snapshot!,new(5,ItemID.StoneBlock,2),now),Is.True);
            Assert.That(frame.TryTakeManagedTransfer(out var frameIntent),Is.True);
            Assert.That(frame.Definition.ItemType,Is.Zero);
            Assert.That(player.inventory[5].stack,Is.EqualTo(3));
            Assert.That(frame.AcceptManagedTransfer(frameIntent!.Value).Status,Is.EqualTo(ManagedDisplayTransferStatus.Applied));
            Assert.That(frame.Definition.ItemType,Is.EqualTo(ItemID.StoneBlock));
            Assert.That(frame.Definition.Stack,Is.EqualTo(1));
            Assert.That(player.inventory[5].stack,Is.EqualTo(2));
            Assert.That(frame.AcceptManagedTransfer(frameIntent.Value).Status,Is.EqualTo(ManagedDisplayTransferStatus.StaleInteraction));

            Assert.That(rack.TryArmManagedInteraction(snapshot!),Is.True);
            now=DateTimeOffset.UtcNow;
            Assert.That(scenes.ObserveManagedInventory(snapshot!,new(6,0,0),now),Is.True);
            Assert.That(scenes.ObserveManagedDisplayPlacement(snapshot!,ManagedDisplayKind.WeaponsRack,14,10,new(ItemID.CopperShortsword,1,PrefixID.Large),now),Is.True);
            Assert.That(rack.TryTakeManagedTransfer(out var rackIntent),Is.True);
            Assert.That(rack.AcceptManagedTransfer(rackIntent!.Value).Status,Is.EqualTo(ManagedDisplayTransferStatus.Applied));
            Assert.That(rack.Definition.Items.Single().ItemType,Is.EqualTo(ItemID.CopperShortsword));
            Assert.That(rack.Definition.Items.Single().Prefix,Is.EqualTo(PrefixID.Large));
            Assert.That(player.inventory[6].IsAir,Is.True);
            Assert.That(TileEntity.ByID,Is.Empty);
            Assert.That(TileEntity.ByPosition,Is.Empty);
            Assert.That(scenes.IsManagedChestPlayer(snapshot!),Is.True);

            // The ordinary native entity projector and client decoder consume the committed
            // metadata; no client-generated packets or graphical window are involved.
            var region=new SceneBounds(10,10,7,3);
            List<byte[]> output=[];
            var projection=new SceneDisplayProjection();
            projection.Reconcile(observer,socket,region,scenes.SnapshotVisibleVirtualItemFrames(world,observer,region),
                scenes.SnapshotVisibleVirtualDisplays(world,observer,region),output.Add);
            Assert.That(output,Has.Count.EqualTo(2));
            Assert.That(TileEntity.ByID,Is.Empty);
            global::Terraria.Main.netMode=1;
            global::Terraria.Netplay.Connection=new global::Terraria.RemoteServer();
            global::Terraria.Main._activeNetDiagnosticsUI=new global::Terraria.UI.EmptyDiagnosticsUI();
            var buffer=new global::Terraria.MessageBuffer{whoAmI=256};
            foreach(byte[] packet in output)
            { packet.AsSpan(2).CopyTo(buffer.readBuffer);buffer.GetData(0,packet.Length-2,out int kind);Assert.That(kind,Is.EqualTo((int)PacketTypes.UpdateTileEntity)); }
            Assert.That(TileEntity.ByID.Values.OfType<TEItemFrame>().Single().item.type,Is.EqualTo(ItemID.StoneBlock));
            Assert.That(TileEntity.ByID.Values.OfType<TEWeaponsRack>().Single().item.prefix,Is.EqualTo(PrefixID.Large));
        }
        finally
        {
            VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);
            global::Terraria.Main.player[slot]=oldPlayer;Array.Copy(oldClients,global::Terraria.Netplay.Clients,oldClients.Length);Array.Copy(oldBuffers,global::Terraria.NetMessage.buffer,oldBuffers.Length);
            global::Terraria.Main.netMode=oldMode;global::Terraria.Main.maxTilesX=oldWidth;global::Terraria.Main.maxTilesY=oldHeight;
            global::Terraria.Main.dedServ=oldDedicated;global::Terraria.Main.ServerSideCharacter=oldSsc;
            global::Terraria.Netplay.Connection=oldConnection;global::Terraria.Main._activeNetDiagnosticsUI=oldDiagnostics;
            TileEntity.manager=oldManager;TileEntity.ByID=oldById;TileEntity.ByPosition=oldByPosition;TileEntity.UpdateEntities=oldUpdates;TileEntity.TileEntitiesNextID=oldNext;
            TEItemFrame.EntityTypeID=oldFrame;TEWeaponsRack.EntityTypeID=oldRack;TETrainingDummy.EntityTypeID=oldDummy;updateStart.SetValue(null,oldStart);updateEnd.SetValue(null,oldEnd);
        }
    }

    private static SceneTileArea Tiles(ushort type,int size)=>SceneTileArea.Create(size,size,(type,size),static (tiles,state)=>
    { for(int y=0;y<state.size;y++) for(int x=0;x<state.size;x++) tiles[y*state.size+x]=new(state.type,FrameX:(short)(18*x),FrameY:(short)(18*y),Flags:SceneTileFlags.Active); });
    private sealed class InlineDispatcher:IGameThreadDispatcher
    { public bool IsGameThreadKnown=>true;public bool IsGameThread=>true;public void AssertGameThread(){}public bool TryPost(string name,Action action){action();return true;}public bool TrySchedule(string name,Func<bool> step)=>throw new NotSupportedException(); }
    private sealed class RecordingSocket:ISocket
    {
        public void Close(){}public bool IsConnected()=>true;public bool IsDataAvailable()=>false;public RemoteAddress GetRemoteAddress()=>throw new NotSupportedException();
        public void Connect(RemoteAddress address)=>throw new NotSupportedException();public bool StartListening(SocketConnectionAccepted callback)=>throw new NotSupportedException();public void StopListening()=>throw new NotSupportedException();
        public void AsyncSend(byte[] data,int offset,int size,SocketSendCallback callback,object? state=null)=>callback(state);
        public void AsyncReceive(byte[] data,int offset,int size,SocketReceiveCallback callback,object? state=null)=>throw new NotSupportedException();
    }
    private sealed class NullRegistry:ICommandRegistry { public IDisposable Register(ICommandHandler handler)=>throw new NotSupportedException(); }
    private sealed class QuietLogger:IOperationsLogger { public void Log(OperationsLogLevel level,string source,string text,Exception? exception=null,string? correlationId=null){} }
}
