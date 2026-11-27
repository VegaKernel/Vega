using Multiplicity.Packets;
using NUnit.Framework;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Scenes;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;
using Vega.Terraria.Scenes;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class NativeVirtualSignUiTests
{
    [Test]
    public void NativeSignPresenterSendsExactReadAndRefreshWithoutCanonicalRegistration()
    {
        const int slot = 5;
        var oldPlayer = global::Terraria.Main.player[slot];
        var oldClient = global::Terraria.Netplay.Clients[slot];
        var oldSigns = global::Terraria.Main.sign;
        var oldTiles = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY;
        int oldMode = global::Terraria.Main.netMode, oldMyPlayer = global::Terraria.Main.myPlayer;
        bool oldDedicated = global::Terraria.Main.dedServ, oldSignType = global::Terraria.Main.tileSign[TileID.Signs];
        var oldConnection = global::Terraria.Netplay.Connection;
        var oldDiagnostics = global::Terraria.Main._activeNetDiagnosticsUI;
        var world = new WorldId("sign-ui");
        var dispatcher = new InlineDispatcher();
        using var boundary = new TerrariaPlayerBoundary(dispatcher,new TerrariaConnectionMetadataRegistry(),world);
        using var renderer = new TerrariaSceneRenderSink(dispatcher,boundary,new QuietLogger(),world);
        var runtime = new SceneRuntime(renderer);
        renderer.Attach(runtime,boundary);
        try
        {
            global::Terraria.Main.sign = new global::Terraria.Sign[oldSigns.Length];
            global::Terraria.Main.maxTilesX = global::Terraria.Main.maxTilesY = 20;
            global::Terraria.Main.tileSign[TileID.Signs] = true;
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.netMode = 2;
            var player = new global::Terraria.Player { active = true,whoAmI = slot,name = "Reader",position = new(160,160) };
            global::Terraria.Main.player[slot] = player;
            var socket = new RecordingSocket();
            var client = new global::Terraria.RemoteClient { Id = slot,State = 10 };
            global::Terraria.Netplay.Clients[slot] = client;
            client.Activate(socket);
            client.State = 10;
            boundary.Attach();
            boundary.Opened += opened => runtime.OpenSession(world,opened);
            TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(slot);
            Assert.That(boundary.TryGet(new PlayerId(slot),out var snapshot),Is.True);
            Assert.That(snapshot!.SessionId,Is.Not.Null);
            using var scene = runtime.CreateScene(world,"sign","test");
            var sign = new SceneVirtualSignDefinition(VirtualSignId.New(),0,0,"Old",1,true);
            var area = new SceneTileArea(2,2,[new(55,FrameX:0,FrameY:0,Flags:SceneTileFlags.Active),new(55,FrameX:18,FrameY:0,Flags:SceneTileFlags.Active),
                new(55,FrameX:0,FrameY:18,Flags:SceneTileFlags.Active),new(55,FrameX:18,FrameY:18,Flags:SceneTileFlags.Active)]);
            var overlay = scene.AddOverlay(new SceneOverlayDefinition(default,"sign",new(10,10,2,2),1,SceneObserverScope.PersonalSessions([SceneSessionObserver.From(snapshot)]),area)
                { VirtualSigns = [sign] });
            Assert.That(runtime.TryRequestVirtualSignRead(snapshot,10,10),Is.True);
            Assert.That(runtime.TryTakeVirtualSignRead(scene.Id,overlay,sign.Id,out var read),Is.True);
            Assert.That(boundary.TryGetSessionPlayer(snapshot,out var live),Is.True);
            Assert.That(live.dead,Is.False);
            Assert.That(global::Terraria.Main.sign.Length,Is.GreaterThan(0));
            Assert.That(renderer.TryGetVirtualSignIndex(snapshot,read!.Value,out var index),Is.True,"Native sign recipient/slot validation");
            Assert.That(runtime.TryPresentVirtualSign(snapshot,read!.Value),Is.True);
            var open = socket.LastSend;
            var packet = (SignNew)TerrariaPacket.Deserialize(open,0,open.Length);
            Assert.That(packet.PlayerId,Is.EqualTo(slot));
            Assert.That(packet.SuppressOpen,Is.False);
            Assert.That(packet.Text,Is.EqualTo("Old"));
            Assert.That(runtime.IsVirtualSignIndex(snapshot,packet.SignId),Is.True);
            Assert.That(global::Terraria.Main.sign.All(value => value is null),Is.True);
            Assert.That(runtime.TryRequestPresentedVirtualSignEdit(snapshot,packet.SignId,10,10,"New"),Is.True);
            Assert.That(runtime.TryTakeVirtualSignEdit(scene.Id,overlay,sign.Id,out var edit),Is.True);
            Assert.That(runtime.TryApplyVirtualSignEdit(snapshot,edit!.Value,out _),Is.True);
            Assert.That(runtime.TryRefreshVirtualSign(snapshot,edit.Value),Is.True);
            var refresh = socket.LastSend;
            var refreshed = (SignNew)TerrariaPacket.Deserialize(refresh,0,refresh.Length);
            Assert.That(refreshed.SignId,Is.EqualTo(packet.SignId));
            Assert.That(refreshed.SuppressOpen,Is.True);
            Assert.That(refreshed.Text,Is.EqualTo("New"));
            Assert.That(global::Terraria.Main.sign.All(value => value is null),Is.True);

            // Native client sign metadata decoder; the graphical window and input subsystem
            // are intentionally not constructed. The opening response itself is stock-framed above.
            var grid = new global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>(20,20);
            for(int x=0;x<20;x++) for(int y=0;y<20;y++) grid[x,y] = new global::Terraria.Tile();
            for(int x=10;x<12;x++) for(int y=10;y<12;y++)
            {
                grid[x,y].type = TileID.Signs;
                grid[x,y].active(true);
                grid[x,y].frameX = (short)((x-10)*18);
                grid[x,y].frameY = (short)((y-10)*18);
            }
            global::Terraria.Main.tile = grid;
            global::Terraria.Main.netMode = 1;
            global::Terraria.Main.myPlayer = slot;
            global::Terraria.Netplay.Connection = new global::Terraria.RemoteServer();
            global::Terraria.Main._activeNetDiagnosticsUI = new global::Terraria.UI.EmptyDiagnosticsUI();
            var buffer = new global::Terraria.MessageBuffer { whoAmI = 256 };
            refresh.AsSpan(2).CopyTo(buffer.readBuffer);
            buffer.GetData(0,refresh.Length-2,out int kind);
            Assert.That(kind,Is.EqualTo((int)PacketTypes.SignNew));
            Assert.That(global::Terraria.Main.sign[packet.SignId].text,Is.EqualTo("New"));
        }
        finally
        {
            global::Terraria.Main.player[slot] = oldPlayer;
            global::Terraria.Netplay.Clients[slot] = oldClient;
            global::Terraria.Main.sign = oldSigns;
            global::Terraria.Main.tile = oldTiles;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.myPlayer = oldMyPlayer;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Main.tileSign[TileID.Signs] = oldSignType;
            global::Terraria.Netplay.Connection = oldConnection;
            global::Terraria.Main._activeNetDiagnosticsUI = oldDiagnostics;
        }
    }

    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name,Action action) { action(); return true; }
        public bool TrySchedule(string name,Func<bool> step) => true;
    }
    private sealed class RecordingSocket : ISocket
    {
        public byte[] LastSend { get; private set; } = [];
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
        public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data,int offset,int size,SocketSendCallback callback,object? state = null) { LastSend = data.AsSpan(offset,size).ToArray(); callback(state); }
        public void AsyncReceive(byte[] data,int offset,int size,SocketReceiveCallback callback,object? state = null) => throw new NotSupportedException();
    }
    private sealed class QuietLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level,string source,string text,Exception? exception = null,string? correlationId = null)
        { if (exception is not null) TestContext.Out.WriteLine(exception); }
    }
}
