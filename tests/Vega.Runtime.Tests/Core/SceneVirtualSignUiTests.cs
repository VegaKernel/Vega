using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;
using Vega.Core.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneVirtualSignUiTests
{
    [Test]
    public void NativeAliasIsBoundToExactReadAndRetainedAfterSceneRemovalUntilCanonicalRead()
    {
        var sink = new Presenter();
        var runtime = new SceneRuntime(sink);
        var opened = new PlayerSessionOpened(PlayerSessionId.New(),new(4),"viewer",DateTimeOffset.UtcNow);
        var player = new PlayerSnapshot(opened.PlayerId,opened.PlayerName,false,100,100,20,20,new(1600,1600),false,0) { SessionId = opened.SessionId };
        runtime.OpenSession(WorldId.Primary,opened);
        var directory = new Directory(player);
        var policy = new SceneInteractionAuthorityPolicy(runtime,WorldId.Primary,directory);
        using var scene = runtime.CreateScene(WorldId.Primary,"sign","test");
        var sign = new SceneVirtualSignDefinition(VirtualSignId.New(),0,0,"Old",1,true);
        var overlay = scene.AddOverlay(new SceneOverlayDefinition(default,"sign",new(100,100,2,2),1,SceneObserverScope.Global,
            SceneTileArea.Fill(2,2,new SceneTile(55,Flags:SceneTileFlags.Active))) { VirtualSigns = [sign] });
        Assert.That(Process(policy,new SignRead { X = 100,Y = 100 },PacketTypes.SignRead,player),Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Assert.That(runtime.TryTakeVirtualSignRead(scene.Id,overlay,sign.Id,out var read),Is.True);
        Assert.That(runtime.TryPresentVirtualSign(player,read!.Value),Is.True);
        Assert.That(sink.Sent.Single(),Is.EqualTo(("Old",true)));
        Assert.That(runtime.IsVirtualSignIndex(player,999),Is.True);
        var changed = new SignNew { SignId = 999,X = 100,Y = 100,Text = "New",PlayerId = (byte)player.Id.Slot };
        Assert.That(Process(policy,new SignNew { SignId = 998,X = 100,Y = 100,Text = "Wrong index",PlayerId = (byte)player.Id.Slot },PacketTypes.SignNew,player),
            Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Assert.That(runtime.TryTakeVirtualSignEdit(scene.Id,overlay,sign.Id,out _),Is.False);
        Assert.That(Process(policy,changed,PacketTypes.SignNew,player),Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Assert.That(runtime.TryTakeVirtualSignEdit(scene.Id,overlay,sign.Id,out var edit),Is.True);
        Assert.That(runtime.TryApplyVirtualSignEdit(player,edit!.Value,out _),Is.True);
        Assert.That(runtime.TryRefreshVirtualSign(player,edit.Value),Is.True);
        Assert.That(sink.Sent.Last(),Is.EqualTo(("New",false)));
        scene.Dispose();
        directory.Canonical = new WorldTilePosition(100,100);
        Assert.That(Process(policy,changed,PacketTypes.SignNew,player),Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")),"A disposed virtual UI never becomes a canonical edit.");
        Assert.That(Process(policy,new SignRead { X = 100,Y = 100 },PacketTypes.SignRead,player),Is.EqualTo(PacketProcessingDecision.Allow));
        runtime.ObserveCanonicalSignPresented(player,999,100,100);
        Assert.That(runtime.IsVirtualSignIndex(player,999),Is.False);
        Assert.That(Process(policy,changed,PacketTypes.SignNew,player),Is.EqualTo(PacketProcessingDecision.Allow));
        Assert.That(runtime.IsVirtualSignIndex(player with { SessionId = PlayerSessionId.New() },999),Is.False);
    }

    private static PacketProcessingDecision Process(IPacketProcessor<PacketView> policy, TerrariaPacket packet, PacketTypes type, PlayerSnapshot player)
    {
        using var stream = new MemoryStream();
        packet.ToStream(stream,false);
        var view = PacketView.FromPayload(type,stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(view,PacketDirection.Inbound,new ConnectionId(player.Id.Slot),player.Id,DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }
    private sealed class Presenter : ISceneRenderSink, ISceneVirtualSignUiSink
    {
        public List<(string Text,bool Open)> Sent { get; } = [];
        public void Invalidate(SceneRenderRequest request) { }
        public bool TryGetVirtualSignIndex(PlayerSnapshot player, SceneVirtualSignReadRequest read,out short index) { index = 999; return true; }
        public bool TrySendVirtualSign(PlayerSnapshot player, SceneVirtualSignSnapshot sign,short index,bool open) { Sent.Add((sign.Definition.Text,open)); return true; }
    }
    private sealed class Directory(PlayerSnapshot player) : IPlayerDirectory,IWorldSignState
    {
        public WorldTilePosition? Canonical { get; set; }
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];
        public bool TryGet(PlayerId id,out PlayerSnapshot? found) { found = id == player.Id ? player : null; return found is not null; }
        public bool TryFindExact(string name,out PlayerSnapshot? found) { found = name == player.Name ? player : null; return found is not null; }
        public bool TryGetSignPosition(int signId,out WorldTilePosition position) { position = Canonical.GetValueOrDefault(); return signId == 999 && Canonical is not null; }
    }
}
