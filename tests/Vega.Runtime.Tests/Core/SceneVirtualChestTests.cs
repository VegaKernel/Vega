using NUnit.Framework;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneVirtualChestTests
{
    private static readonly WorldId Arena = new("virtual-chest-arena");

    [Test]
    public void VirtualChestUsesVisibleProviderExactSessionAndBoundedLatestIntent()
    {
        var sink = new RecordingSink();
        var runtime = new SceneRuntime(sink);
        var first = Open(runtime, 1);
        var other = Open(runtime, 2);
        var source = new List<ChestItemSnapshot> { new(0, 1, 4, 0) };
        var chest = new SceneVirtualChestDefinition(VirtualChestId.New(), 1, 1, "Treasure", 1, source);
        source[0] = new(0, 2, 99, 0);
        var tiles = SceneTileArea.Fill(4, 4, new SceneTile(21, Flags: SceneTileFlags.Active));
        var definition = new SceneOverlayDefinition(default, "personal", new(100, 100, 4, 4), 0,
            SceneObserverScope.PersonalSessions([SceneSessionObserver.From(first)]), tiles) { VirtualChests = [chest] };
        using var scene = runtime.CreateScene(Arena, "personal", "test");
        var overlay = scene.AddOverlay(definition);
        var observer = SceneSessionObserver.From(first);
        Assert.That(runtime.TryResolveVirtualChest(Arena, observer, 102, 102, out var resolved), Is.True);
        Assert.That(resolved!.Value.Identity, Is.EqualTo(new ChestIdentity(Arena, 101, 101)));
        Assert.That(resolved.Value.Definition.Items.Single().ItemType, Is.EqualTo(1));
        Assert.That(runtime.TryResolveVirtualChest(Arena, SceneSessionObserver.From(other), 101, 101, out _), Is.False);
        Assert.That(runtime.TryResolveVirtualChest(WorldId.Primary, observer, 101, 101, out _), Is.False);

        var player = Player(first, 101, 101);
        Assert.That(runtime.TryRequestVirtualChestOpen(player with { IsDead = true }, 101, 101), Is.False);
        Assert.That(runtime.TryRequestVirtualChestOpen(player with { Position = new(0, 0) }, 101, 101), Is.False);
        Assert.That(runtime.TryRequestVirtualChestOpen(player, 101, 101), Is.True);
        Assert.That(runtime.TryRequestVirtualChestOpen(player, 102, 102), Is.True);
        sink.Requests.Clear();
        var updated = new SceneVirtualChestDefinition(chest.Id, 1, 1, "Treasure", 2, [new(0, 1, 9, 0)]);
        definition = definition with { VirtualChests = [updated] };
        Assert.That(scene.UpdateOverlay(overlay, definition), Is.True);
        Assert.That(sink.Requests, Is.Empty, "Only logical contents changed; no tile redraw is necessary.");
        Assert.That(runtime.TryTakeVirtualChestOpen(scene.Id, overlay, chest.Id, out var intent), Is.True);
        Assert.That(intent!.Value.Observer, Is.EqualTo(observer));
        Assert.That(intent.Value.Chest.Definition.Revision, Is.EqualTo(2));
        Assert.That(runtime.TryTakeVirtualChestOpen(scene.Id, overlay, chest.Id, out _), Is.False);

        Assert.That(runtime.TryRequestVirtualChestOpen(player, 101, 101), Is.True);
        definition = definition with { Bounds = new(110, 100, 4, 4) };
        Assert.That(scene.UpdateOverlay(overlay, definition), Is.True);
        Assert.That(runtime.TryTakeVirtualChestOpen(scene.Id, overlay, chest.Id, out _), Is.False);
        Assert.That(runtime.TryResolveVirtualChest(Arena, observer, 101, 101, out _), Is.False);
        Assert.That(runtime.TryResolveVirtualChest(Arena, observer, 111, 101, out resolved), Is.True);
        Assert.That(resolved!.Value.Identity.X, Is.EqualTo(111));
        Assert.That(runtime.TryRequestVirtualChestOpen(Player(first, 111, 101), 111, 101), Is.True);
        using (var covering = runtime.CreateScene(Arena, "cover", "other"))
        {
            covering.AddOverlay(new(default, "one corner", new(112, 102, 1, 1), 5, SceneObserverScope.Global,
                SceneTileArea.Fill(1, 1, SceneTile.Air)));
            Assert.That(runtime.TryResolveVirtualChest(Arena, observer, 111, 101, out _), Is.False);
            Assert.That(runtime.TryTakeVirtualChestOpen(scene.Id, overlay, chest.Id, out _), Is.False);
        }
        Assert.That(runtime.TryRequestVirtualChestOpen(Player(first, 111, 101), 111, 101), Is.True);
        runtime.CloseSession(Arena, new(first.SessionId, first.PlayerId, first.PlayerName, DateTimeOffset.UtcNow));
        var replacement = Open(runtime, 1);
        Assert.That(runtime.TryTakeVirtualChestOpen(scene.Id, overlay, chest.Id, out _), Is.False);
        Assert.That(runtime.TryResolveVirtualChest(Arena, SceneSessionObserver.From(replacement), 111, 101, out _), Is.False);
    }

    [Test]
    public void SameRawOverlayIdentityDoesNotConfuseOwningScene()
    {
        var runtime = new SceneRuntime();
        var opened = Open(runtime, 1);
        var overlay = OverlayId.New();
        var chest = new SceneVirtualChestDefinition(VirtualChestId.New(), 0, 0, "same id", 1, []);
        var definition = new SceneOverlayDefinition(overlay, "shared id", new(20, 20, 2, 2), 0, SceneObserverScope.Global,
            SceneTileArea.Fill(2, 2, new SceneTile(21, Flags: SceneTileFlags.Active))) { VirtualChests = [chest] };
        using var lower = runtime.CreateScene(Arena, "lower", "plugin A");
        using var upper = runtime.CreateScene(Arena, "upper", "plugin B");
        lower.AddOverlay(definition);
        upper.AddOverlay(definition with { Order = 1 });
        Assert.That(runtime.TryRequestVirtualChestOpen(Player(opened, 20, 20), 20, 20), Is.True);
        Assert.That(runtime.TryTakeVirtualChestOpen(lower.Id, overlay, chest.Id, out _), Is.False);
        Assert.That(runtime.TryTakeVirtualChestOpen(upper.Id, overlay, chest.Id, out var request), Is.True);
        Assert.That(request!.Value.Chest.SceneId, Is.EqualTo(upper.Id));
    }

    [Test]
    public void VirtualChestsRejectOverlappingFootprintsAndInvalidLogicalItems()
    {
        Assert.Throws<ArgumentException>(() => new SceneVirtualChestDefinition(VirtualChestId.New(), 0, 0, "bad", 1,
            [new(40, 1, 1, 0)]));
        Assert.Throws<ArgumentException>(() => new SceneVirtualChestDefinition(VirtualChestId.New(), 0, 0, "bad", 1,
            [new(0, 0, 1, 0)]));
        var runtime = new SceneRuntime();
        using var scene = runtime.CreateScene(Arena, "chests", "test");
        var first = new SceneVirtualChestDefinition(VirtualChestId.New(), 0, 0, "first", 1, []);
        var second = new SceneVirtualChestDefinition(VirtualChestId.New(), 1, 1, "second", 1, []);
        var definition = new SceneOverlayDefinition(default, "tiles", new(0, 0, 3, 3), 0, SceneObserverScope.Global,
            SceneTileArea.Fill(3, 3, new SceneTile(21, Flags: SceneTileFlags.Active)));
        Assert.Throws<ArgumentException>(() => scene.AddOverlay(definition with { VirtualChests = [first, second] }));
        Assert.Throws<ArgumentException>(() => scene.AddOverlay(definition with
        { VirtualChests = [new(VirtualChestId.New(), 2, 2, "outside", 1, [])] }));
        Assert.That(runtime.HasActiveOverlays, Is.False);
    }

    [Test]
    public void PersonalAirMaskHidesGlobalVirtualContainerForOnlyItsObserver()
    {
        var runtime = new SceneRuntime();
        var first = Open(runtime, 1);
        var other = Open(runtime, 2);
        using var scene = runtime.CreateScene(Arena, "global", "test");
        var chest = new SceneVirtualChestDefinition(VirtualChestId.New(), 0, 0, "public", 1, []);
        scene.AddOverlay(new SceneOverlayDefinition(default, "global", new(50, 50, 2, 2), 1, SceneObserverScope.Global,
            SceneTileArea.Fill(2, 2, new SceneTile(21, Flags: SceneTileFlags.Active))) { VirtualChests = [chest] });
        using var personal = runtime.CreateScene(Arena, "mask", "test");
        var mask = new SceneOverlayDefinition(default, "mask", new(10, 10, 2, 2), 0,
            SceneObserverScope.PersonalSessions([SceneSessionObserver.From(first)]), SceneTileArea.Fill(2, 2, SceneTile.Air),
            FillOutsideWithAir: true);
        var maskId = personal.AddOverlay(mask);
        Assert.That(runtime.TryResolveVirtualChest(Arena, SceneSessionObserver.From(first), 50, 50, out _), Is.False);
        Assert.That(runtime.TryResolveVirtualChest(Arena, SceneSessionObserver.From(other), 50, 50, out _), Is.True);
        personal.UpdateOverlay(maskId, mask with { Enabled = false });
        Assert.That(runtime.TryResolveVirtualChest(Arena, SceneSessionObserver.From(first), 50, 50, out _), Is.True);
    }

    private static PlayerSessionOpened Open(SceneRuntime runtime, int slot)
    {
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), new(slot), $"player-{slot}", DateTimeOffset.UtcNow);
        runtime.OpenSession(Arena, opened);
        return opened;
    }

    private static PlayerSnapshot Player(PlayerSessionOpened opened, int x, int y) =>
        new(opened.PlayerId, opened.PlayerName, false, 100, 100, 20, 20, new(x * 16, y * 16), false, 0)
        { WorldId = Arena, SessionId = opened.SessionId };

    private sealed class RecordingSink : ISceneRenderSink
    {
        public List<SceneRenderRequest> Requests { get; } = [];
        public void Invalidate(SceneRenderRequest request) => Requests.Add(request);
    }
}
