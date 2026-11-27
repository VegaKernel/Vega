using NUnit.Framework;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneManagedDisplayInteractionTests
{
    [TestCase(ManagedDisplayKind.ItemFrame)]
    [TestCase(ManagedDisplayKind.WeaponsRack)]
    public void ArmedDisplayPlacementRequiresConservedInventoryAndOneExplicitCommit(ManagedDisplayKind kind)
    {
        var world = new WorldId("managed-display");
        var sink = new Sink();
        var runtime = new SceneRuntime(sink);
        var opened = new PlayerSessionOpened(PlayerSessionId.New(),new(1),"player",DateTimeOffset.UtcNow);
        runtime.OpenSession(world,opened);
        var player = new PlayerSnapshot(opened.PlayerId,opened.PlayerName,false,100,100,20,20,new(1600,1600),false,0) { WorldId = world,SessionId = opened.SessionId };
        var host = new Host();
        var items = Enumerable.Range(0,59).Select(slot=>new InventoryItemSnapshot(slot,0,0,0,false,"")).ToArray();
        bool isFrame = kind == ManagedDisplayKind.ItemFrame;
        int itemType = isFrame ? 1 : global::Terraria.ID.ItemID.CopperShortsword;
        items[5] = new(5,itemType,isFrame ? 3 : 1,0,false,"Item");
        var inventory = new PlayerInventorySnapshot(player.Id,player.Name,1,items,DateTimeOffset.UtcNow) { WorldId = world,SessionId = player.SessionId };
        var frame = new SceneVirtualItemFrameDefinition(VirtualItemFrameId.New(),0,0,0,0,0,1);
        var rack = new SceneVirtualDisplayDefinition(VirtualDisplayId.New(),SceneVirtualDisplayKind.WeaponsRack,0,0,[],[],1);
        Guid entity = isFrame ? frame.Id.Value : rack.Id.Value;
        int size = isFrame ? 2 : 3;
        ushort tileType = isFrame ? SceneVirtualItemFrameDefinition.TileType : rack.TileType;
        var tiles = SceneTileArea.Create(size,size,(size,tileType),static (span,state)=>
        { for(int y=0;y<state.size;y++) for(int x=0;x<state.size;x++) span[y*state.size+x]=new(state.tileType,FrameX:(short)(18*x),FrameY:(short)(18*y),Flags:SceneTileFlags.Active); });
        using var scene = runtime.CreateScene(world,"display","test");
        var overlay = scene.AddOverlay(new SceneOverlayDefinition(default,"display",new(100,100,size,size),1,SceneObserverScope.Global,tiles)
        { VirtualItemFrames = isFrame ? [frame] : [],VirtualDisplays = isFrame ? [] : [rack] });
        Assert.That(runtime.TryResolveManagedDisplayTarget(player,scene.Id,overlay,entity,kind,out var target),Is.True);
        Assert.That(runtime.TryArmManagedDisplay(player,target,inventory,host),Is.True);
        Assert.That(runtime.TryArmManagedDisplay(player,target,inventory,host),Is.False);
        Assert.That(runtime.IsManagedChestPlayer(player),Is.True);
        var now = DateTimeOffset.UtcNow;
        // Inventory may be observed first; neither half mutates the provider.
        Assert.That(runtime.ObserveManagedInventory(player,new(5,isFrame ? itemType : 0,isFrame ? 2 : 0),now),Is.True);
        Assert.That(runtime.TryTakeManagedDisplayTransfer(scene.Id,overlay,entity,kind,out _),Is.False);
        Assert.That(runtime.ObserveManagedDisplayPlacement(player,kind,100,100,new(itemType,1),now),Is.True);
        Assert.That(runtime.TryTakeManagedDisplayTransfer(scene.Id,overlay,entity,kind,out var pending),Is.True);
        Assert.That(runtime.TryTakeManagedDisplayTransfer(scene.Id,overlay,entity,kind,out _),Is.False);
        Assert.That(runtime.TryClaimManagedDisplayTransfer(pending!.Value),Is.True);
        Assert.That(runtime.TryClaimManagedDisplayTransfer(pending.Value),Is.False);
        int commits=0;
        Assert.That(runtime.TryCommitManagedDisplayTransfer(player with { SessionId = PlayerSessionId.New() },pending.Value,()=>commits++,out _),Is.False);
        Assert.That(runtime.TryCommitManagedDisplayTransfer(player,pending.Value,()=>commits++,out var result),Is.True);
        Assert.That(result.Status,Is.EqualTo(ManagedDisplayTransferStatus.Applied));
        Assert.That(commits,Is.EqualTo(1));
        Assert.That(runtime.TryCommitManagedDisplayTransfer(player,pending.Value,()=>commits++,out _),Is.False);
        if(isFrame) Assert.That(result.ItemFrame!.Stack,Is.EqualTo(1));
        else Assert.That(result.Display!.Items.Single().Stack,Is.EqualTo(1));
        Assert.That(runtime.TryResolveManagedDisplayTarget(player,scene.Id,overlay,entity,kind,out _),Is.False,"Occupied displays cannot be armed for another deposit.");
        Assert.That(sink.Requests.Last().DirtyRegion,Is.EqualTo(new SceneBounds(100,100,size,size)));
        Assert.That(runtime.ObserveManagedInventory(player,new(5,1,2),now),Is.True);
        runtime.TickManagedChestGuis(new Directory(player),now);
        Assert.That(host.Corrections.Contains(5),Is.True);
        runtime.CloseSession(world,new(opened.SessionId,opened.PlayerId,opened.PlayerName,now));
        Assert.That(runtime.HasManagedChestPlayers,Is.False);
    }

    [Test]
    public void UnmatchedDisplayPlacementExpiresWithoutChangingMetadata()
    {
        var runtime=new SceneRuntime(); var world=WorldId.Primary;
        var opened=new PlayerSessionOpened(PlayerSessionId.New(),new(2),"viewer",DateTimeOffset.UtcNow);
        runtime.OpenSession(world,opened);
        var player=new PlayerSnapshot(opened.PlayerId,opened.PlayerName,false,100,100,20,20,new(1600,1600),false,0){SessionId=opened.SessionId};
        var frame=new SceneVirtualItemFrameDefinition(VirtualItemFrameId.New(),0,0,0,0,0,1);
        var tiles=new SceneTileArea(2,2,[new(395,FrameX:0,FrameY:0,Flags:SceneTileFlags.Active),new(395,FrameX:18,FrameY:0,Flags:SceneTileFlags.Active),
            new(395,FrameX:0,FrameY:18,Flags:SceneTileFlags.Active),new(395,FrameX:18,FrameY:18,Flags:SceneTileFlags.Active)]);
        using var scene=runtime.CreateScene(world,"frame","test");
        var overlay=scene.AddOverlay(new SceneOverlayDefinition(default,"frame",new(100,100,2,2),1,SceneObserverScope.Global,tiles){VirtualItemFrames=[frame]});
        var inventory=new PlayerInventorySnapshot(player.Id,player.Name,1,Enumerable.Range(0,59).Select(slot=>new InventoryItemSnapshot(slot,0,0,0,false,"")).ToArray(),DateTimeOffset.UtcNow)
        {SessionId=player.SessionId};
        var host=new Host();
        Assert.That(runtime.TryResolveManagedDisplayTarget(player,scene.Id,overlay,frame.Id.Value,ManagedDisplayKind.ItemFrame,out var target),Is.True);
        Assert.That(runtime.TryArmManagedDisplay(player,target,inventory,host),Is.True);
        var now=DateTimeOffset.UtcNow;
        Assert.That(runtime.ObserveManagedDisplayPlacement(player,ManagedDisplayKind.ItemFrame,100,100,new(1,1),now),Is.True);
        runtime.TickManagedChestGuis(new Directory(player),now.AddSeconds(1));
        Assert.That(runtime.TryTakeManagedDisplayTransfer(scene.Id,overlay,frame.Id.Value,ManagedDisplayKind.ItemFrame,out _),Is.False);
        Assert.That(runtime.SnapshotVisibleVirtualItemFrames(world,SceneSessionObserver.From(player),new(100,100,2,2)).Single().Definition,Is.SameAs(frame));
        Assert.That(host.Corrections,Does.Contain(58));
        Assert.That(runtime.IsManagedChestPlayer(player),Is.True);
    }

    private sealed class Directory(PlayerSnapshot player):IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers()=>[player];
        public bool TryGet(PlayerId id,out PlayerSnapshot? found){found=id==player.Id?player:null;return found is not null;}
        public bool TryFindExact(string name,out PlayerSnapshot? found){found=name==player.Name?player:null;return found is not null;}
    }
    private sealed class Host:IVirtualChestGuiHost
    {
        public HashSet<int> Corrections=[];
        public bool TryPresentManagedGui(ISceneRuntime scenes,SceneVirtualChestOpenRequest open)=>throw new NotSupportedException();
        public SceneVirtualChestTransferResult AcceptManagedGuiTransfer(ISceneRuntime scenes,SceneVirtualChestGuiTransferIntent intent)=>throw new NotSupportedException();
        public bool SynchronizeManagedGui(PlayerSnapshot player,short index,ChestIdentity? closedChest,IReadOnlyList<int> corrections){Corrections.UnionWith(corrections);return true;}
    }
    private sealed class Sink:ISceneRenderSink
    { public List<SceneRenderRequest> Requests=[]; public void Invalidate(SceneRenderRequest request)=>Requests.Add(request); }
}
