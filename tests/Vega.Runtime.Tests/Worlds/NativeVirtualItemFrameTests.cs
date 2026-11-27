using Multiplicity.Packets;
using NUnit.Framework;
using System.Reflection;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Terraria.Scenes;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class NativeVirtualItemFrameTests
{
    [Test]
    public void NativeProjectionReservesIdsRestoresCanonicalCollisionsAndDecodesOnClient()
    {
        var oldManager = TileEntity.manager;
        var oldById = TileEntity.ByID;
        var oldByPosition = TileEntity.ByPosition;
        var oldUpdates = TileEntity.UpdateEntities;
        var updateStart = typeof(TileEntity).GetField("_UpdateStart", BindingFlags.Static | BindingFlags.NonPublic)!;
        var updateEnd = typeof(TileEntity).GetField("_UpdateEnd", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? oldStart = updateStart.GetValue(null), oldEnd = updateEnd.GetValue(null);
        int oldNextId = TileEntity.TileEntitiesNextID;
        byte oldFrame = TEItemFrame.EntityTypeID, oldDummy = TETrainingDummy.EntityTypeID;
        int oldMode = global::Terraria.Main.netMode;
        bool oldDedicated = global::Terraria.Main.dedServ;
        var oldConnection = global::Terraria.Netplay.Connection;
        var oldDiagnostics = global::Terraria.Main._activeNetDiagnosticsUI;
        try
        {
            TileEntity.manager = new TileEntitiesManager();
            TileEntity.manager.Register(new TETrainingDummy());
            TileEntity.manager.Register(new TEItemFrame());
            TileEntity.ByID = [];
            TileEntity.ByPosition = [];
            TileEntity.UpdateEntities = [];
            TileEntity.TileEntitiesNextID = 100;
            global::Terraria.Main.netMode = 2;
            global::Terraria.Main.dedServ = true;
            var observer = new SceneSessionObserver(new PlayerId(1), PlayerSessionId.New());
            var other = new SceneSessionObserver(new PlayerId(2), PlayerSessionId.New());
            var socket = new NoSocket();
            var projection = new SceneItemFrameProjection();
            var item = new SceneVirtualItemFrameDefinition(VirtualItemFrameId.New(), 0, 0, ItemID.Torch, 1, 0, 1);
            var snapshot = new SceneVirtualItemFrameSnapshot(SceneId.New(), OverlayId.New(), WorldId.Primary,new(10,10),item);
            var region = new SceneBounds(10,10,2,2);
            var output = new List<byte[]>();
            UpdateTileEntity Parse(byte[] packet) => (UpdateTileEntity)TerrariaPacket.Deserialize(packet,0,packet.Length);

            projection.Reconcile(observer,socket,region,[snapshot],output.Add);
            Assert.That(output,Has.Count.EqualTo(1));
            var create = output[0];
            var createFrame = Parse(create);
            Assert.That(createFrame.Exists,Is.True);
            Assert.That(createFrame.Key,Is.EqualTo(100));
            Assert.That(TileEntity.ByID,Is.Empty);
            Assert.That(TileEntity.ByPosition,Is.Empty);
            Assert.That(TileEntity.TileEntitiesNextID,Is.EqualTo(101));
            output.Clear();
            projection.Reconcile(other,new NoSocket(),region,[],output.Add);
            Assert.That(output,Is.Empty);
            var updated = snapshot with { Definition = new(item.Id,0,0,ItemID.CopperShortsword,1,PrefixID.Large,2) };
            projection.Reconcile(observer,socket,region,[updated],output.Add);
            Assert.That(Parse(output.Single()).Key,Is.EqualTo(100));
            var update = output.Single();
            output.Clear();
            projection.Reconcile(observer,socket,new SceneBounds(11,11,1,1),[],output.Add);
            var remove = output.Single();
            Assert.That(Parse(remove).Exists,Is.False);
            Assert.That(Parse(remove).Key,Is.EqualTo(100));
            Assert.That(projection.HasPresentations(observer.Player),Is.False);

            output.Clear();
            projection.Reconcile(observer,socket,region,[snapshot],output.Add);
            byte[] secondCreate = output.Single();
            int secondId = Parse(output.Single()).Key;
            var canonicalItem = new global::Terraria.Item();
            canonicalItem.SetDefaults(ItemID.Wood);
            canonicalItem.stack = 2;
            var canonical = new TEItemFrame { ID = TileEntity.AssignNewID(), Position = new Point16(10,10), type = TEItemFrame.EntityTypeID, item = canonicalItem };
            TileEntity.ByID.Add(canonical.ID,canonical);
            TileEntity.ByPosition.Add(canonical.Position,canonical);
            output.Clear();
            projection.Reconcile(observer,socket,region,[snapshot],output.Add);
            Assert.That(output,Has.Count.EqualTo(2));
            Assert.That(Parse(output[0]).Exists,Is.False);
            Assert.That(Parse(output[0]).Key,Is.EqualTo(secondId));
            Assert.That(Parse(output[1]).Key,Is.EqualTo(canonical.ID));
            Assert.That(TileEntity.ByID.Values.Single(),Is.SameAs(canonical));
            Assert.That(TileEntity.ByPosition.Values.Single(),Is.SameAs(canonical));
            var canonicalRestore = output.ToArray();

            // The actual native client decoder consumes the three ordinary packets, without
            // connecting to a network or constructing a graphical client.
            TileEntity.ByID = [];
            TileEntity.ByPosition = [];
            global::Terraria.Main.netMode = 1;
            global::Terraria.Netplay.Connection = new global::Terraria.RemoteServer();
            global::Terraria.Main._activeNetDiagnosticsUI = new global::Terraria.UI.EmptyDiagnosticsUI();
            var buffer = new global::Terraria.MessageBuffer { whoAmI = 256 };
            void Decode(byte[] packet)
            {
                packet.AsSpan(2).CopyTo(buffer.readBuffer);
                buffer.GetData(0,packet.Length - 2,out int kind);
                Assert.That(kind,Is.EqualTo((int)PacketTypes.UpdateTileEntity));
            }
            Decode(create);
            var nativeFrame = (TEItemFrame)TileEntity.ByID[100];
            Assert.That(nativeFrame.item.type,Is.EqualTo(ItemID.Torch));
            Assert.That(TileEntity.ByPosition[nativeFrame.Position],Is.SameAs(nativeFrame));
            Decode(update);
            nativeFrame = (TEItemFrame)TileEntity.ByID[100];
            Assert.That(nativeFrame.item.type,Is.EqualTo(ItemID.CopperShortsword));
            Assert.That(nativeFrame.item.prefix,Is.EqualTo(PrefixID.Large));
            Decode(remove);
            Assert.That(TileEntity.ByID,Is.Empty);
            Assert.That(TileEntity.ByPosition,Is.Empty);
            Decode(secondCreate);
            Decode(canonicalRestore[1]); // Ordinary canonical creation can precede queued redraw.
            Decode(canonicalRestore[0]);
            Decode(canonicalRestore[1]);
            Assert.That(TileEntity.ByID.Keys,Is.EqualTo(new[] { canonical.ID }));
            Assert.That(TileEntity.ByPosition.Values.Single().ID,Is.EqualTo(canonical.ID));
        }
        finally
        {
            TileEntity.manager = oldManager;
            TileEntity.ByID = oldById;
            TileEntity.ByPosition = oldByPosition;
            TileEntity.UpdateEntities = oldUpdates;
            updateStart.SetValue(null, oldStart);
            updateEnd.SetValue(null, oldEnd);
            TileEntity.TileEntitiesNextID = oldNextId;
            TEItemFrame.EntityTypeID = oldFrame;
            TETrainingDummy.EntityTypeID = oldDummy;
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Netplay.Connection = oldConnection;
            global::Terraria.Main._activeNetDiagnosticsUI = oldDiagnostics;
        }
    }

    private sealed class NoSocket : ISocket
    {
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
        public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data,int offset,int size,SocketSendCallback callback,object? state = null) => throw new NotSupportedException();
        public void AsyncReceive(byte[] data,int offset,int size,SocketReceiveCallback callback,object? state = null) => throw new NotSupportedException();
    }
}
