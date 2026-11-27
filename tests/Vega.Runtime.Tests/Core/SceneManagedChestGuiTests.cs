using NUnit.Framework;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneManagedChestGuiTests
{
    [Test]
    public void BalancedMovementsAreOnlyIntentsAndLateChangesRemainManagedUntilDisconnect()
    {
        var runtime = new SceneRuntime();
        var world = new WorldId("managed");
        var opened = new PlayerSessionOpened(PlayerSessionId.New(),new(1),"viewer",DateTimeOffset.UtcNow);
        var player = new PlayerSnapshot(opened.PlayerId,opened.PlayerName,false,100,100,20,20,new(1600,1600),false,0)
        { WorldId = world, SessionId = opened.SessionId };
        runtime.OpenSession(world,opened);
        var host = new Host();
        var inventory = new PlayerInventorySnapshot(player.Id,player.Name,1,Enumerable.Range(0,59).Select(slot => new InventoryItemSnapshot(slot,0,0,0,false,"")).ToArray(),DateTimeOffset.UtcNow)
        { WorldId = world, SessionId = opened.SessionId };
        var chest = new SceneVirtualChestDefinition(VirtualChestId.New(),0,0,"Managed",1,[new(0,1,10,0)]);
        using var scene = runtime.CreateScene(world,"gui","test");
        var overlay = scene.AddOverlay(new SceneOverlayDefinition(default,"chest",new(100,100,2,2),1,SceneObserverScope.Global,
            SceneTileArea.Fill(2,2,new SceneTile(21,Flags:SceneTileFlags.Active))) { VirtualChests = [chest] });
        Assert.That(runtime.TryRequestVirtualChestOpen(player,100,100),Is.True);
        Assert.That(runtime.TryTakeVirtualChestOpen(scene.Id,overlay,chest.Id,out var open),Is.True);
        Assert.That(runtime.TryBeginManagedGui(player,open!.Value,10,inventory,host),Is.True);
        Assert.That(runtime.IsManagedChestPlayer(player),Is.True);
        Assert.That(runtime.IsManagedChestPlayer(player with { SessionId = PlayerSessionId.New() }),Is.False);
        var now = DateTimeOffset.UtcNow;
        Assert.That(runtime.ObserveManagedChest(player,10,new(0,1,6,0),now),Is.True);
        Assert.That(runtime.TryTakeManagedGuiTransfer(scene.Id,overlay,chest.Id,out _),Is.False);
        Assert.That(runtime.ObserveManagedInventory(player,new(58,1,4),now),Is.True);
        Assert.That(runtime.TryTakeManagedGuiTransfer(scene.Id,overlay,chest.Id,out var intent),Is.True);
        Assert.That(intent!.Value.Transfer.Direction,Is.EqualTo(VirtualChestTransferDirection.Withdraw));
        Assert.That(intent.Value.Transfer.Amount,Is.EqualTo(4));
        Assert.That(runtime.TryTakeManagedGuiTransfer(scene.Id,overlay,chest.Id,out _),Is.False);
        Assert.That(runtime.TryClaimManagedGuiTransfer(intent.Value,out var transfer),Is.True);
        Assert.That(runtime.TryClaimManagedGuiTransfer(intent.Value,out _),Is.False);
        var updated = new SceneVirtualChestDefinition(chest.Id,0,0,chest.Name,2,[new(0,1,6,0)]);
        Assert.That(runtime.TryResolveVirtualChest(world,open.Value.Observer,100,100,out var unchanged),Is.True);
        Assert.That(unchanged!.Value.Definition.Revision,Is.EqualTo(1));
        int nativeCommit = 0;
        Assert.That(runtime.TryCommitVirtualChestTransfer(player,transfer.Open,updated,() => nativeCommit++),Is.True);
        var slots = inventory.Items.ToArray(); slots[58] = new(58,1,4,0,false,"Stone");
        var updatedInventory = inventory with { Revision = 2, Items = slots };
        Assert.That(runtime.CompleteManagedGuiTransfer(intent.Value,new(VirtualChestTransferStatus.Applied,updated,2),updatedInventory,out short index,out var visible),Is.True);
        Assert.That(nativeCommit,Is.EqualTo(1));
        Assert.That(index,Is.EqualTo(10));
        Assert.That(visible!.Value.Definition,Is.SameAs(updated));

        // A single incomplete movement times out without changing either authoritative side.
        Assert.That(runtime.ObserveManagedInventory(player,new(58,1,2),now),Is.True);
        runtime.TickManagedChestGuis(new Directory(player),now.AddSeconds(1));
        Assert.That(host.Closed,Is.EqualTo(1));
        Assert.That(runtime.IsManagedChestPlayer(player),Is.True,"Closing the GUI must not release ordinary native inventory writes.");
        Assert.That(runtime.CanUseManagedGuiIndex(player,10,open.Value.Chest.Identity),Is.True);
        Assert.That(runtime.CanUseManagedGuiIndex(player,10,new ChestIdentity(world,101,101)),Is.False);
        Assert.That(runtime.CanUseManagedGuiIndex(player,11,new ChestIdentity(world,101,101)),Is.True);
        Assert.That(runtime.ObserveManagedInventory(player,new(58,1,2),now.AddSeconds(3)),Is.True);
        runtime.TickManagedChestGuis(new Directory(player),now.AddSeconds(3));
        Assert.That(host.Corrections.Contains(58),Is.True);
        Assert.That(runtime.TryResolveVirtualChest(world,open.Value.Observer,100,100,out visible),Is.True);
        Assert.That(visible!.Value.Definition.Items.Single().Stack,Is.EqualTo(6));
        runtime.CloseSession(world,new(opened.SessionId,opened.PlayerId,opened.PlayerName,DateTimeOffset.UtcNow));
        Assert.That(runtime.HasManagedChestPlayers,Is.False);
        Assert.That(runtime.ObserveManagedInventory(player,new(58,1,2),now),Is.False);
    }

    [Test]
    public void PlannerAcceptsOnlyOneConservedCompatibleStack()
    {
        Assert.That(SceneRuntime.TryPlanBalancedChestMovement(new(0,1,5,0),new(0,1,7,0),new(58,1,4,0,false,"Stone"),new(58,1,2),out var direction,out int amount),Is.True);
        Assert.That(direction,Is.EqualTo(VirtualChestTransferDirection.Deposit));
        Assert.That(amount,Is.EqualTo(2));
        Assert.That(SceneRuntime.TryPlanBalancedChestMovement(new(0,1,5,0),new(0,1,7,0),new(58,1,4,0,true,"Stone"),new(58,1,2,0,true),out _,out _),Is.False);
        Assert.That(SceneRuntime.TryPlanBalancedChestMovement(new(0,1,5,0),new(0,1,3,0),new(58,0,0,0,false,""),new(58,1,3),out _,out _),Is.False);
        Assert.That(SceneRuntime.TryPlanBalancedChestMovement(new(0,1,5,0),new(0,1,3,0),new(58,0,0,0,false,""),new(58,2,2),out _,out _),Is.False);
    }

    private sealed class Directory(PlayerSnapshot player) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];
        public bool TryGet(PlayerId id,out PlayerSnapshot? found) { found = id == player.Id ? player : null; return found is not null; }
        public bool TryFindExact(string name,out PlayerSnapshot? found) { found = name == player.Name ? player : null; return found is not null; }
    }
    private sealed class Host : IVirtualChestGuiHost
    {
        public int Closed;
        public HashSet<int> Corrections = [];
        public bool TryPresentManagedGui(ISceneRuntime scenes,SceneVirtualChestOpenRequest open) => throw new NotSupportedException();
        public SceneVirtualChestTransferResult AcceptManagedGuiTransfer(ISceneRuntime scenes,SceneVirtualChestGuiTransferIntent intent) => throw new NotSupportedException();
        public bool SynchronizeManagedGui(PlayerSnapshot player,short index,ChestIdentity? closedChest,IReadOnlyList<int> inventoryCorrections)
        { if (closedChest is not null) Closed++; Corrections.UnionWith(inventoryCorrections); return true; }
    }
}
