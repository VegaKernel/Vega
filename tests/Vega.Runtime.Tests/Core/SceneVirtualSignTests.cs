using NUnit.Framework;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneVirtualSignTests
{
    [Test]
    public void SignReadUsesExactSessionVisibleFootprintAndCurrentRevision()
    {
        var world = new WorldId("sign-arena");
        var sink = new RecordingSink();
        var runtime = new SceneRuntime(sink);
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "viewer", DateTimeOffset.UtcNow);
        runtime.OpenSession(world, opened);
        var observer = SceneSessionObserver.From(opened);
        var player = new PlayerSnapshot(opened.PlayerId, opened.PlayerName, false, 100, 100, 20, 20, new(1600, 1600), false, 0)
        { WorldId = world, SessionId = opened.SessionId };
        var sign = new SceneVirtualSignDefinition(VirtualSignId.New(), 0, 0, "Welcome", 1);
        var definition = new SceneOverlayDefinition(default, "sign", new(100, 100, 2, 2), 0,
            SceneObserverScope.PersonalSessions([observer]), SceneTileArea.Fill(2, 2, new SceneTile(55, Flags: SceneTileFlags.Active)))
            { VirtualSigns = [sign] };
        using var scene = runtime.CreateScene(world, "sign", "test");
        var overlay = scene.AddOverlay(definition);
        Assert.That(runtime.TryResolveVirtualSign(world, observer, 101, 101, out var resolved), Is.True);
        Assert.That(resolved!.Value.Position, Is.EqualTo(new WorldTilePosition(100, 100)));
        Assert.That(runtime.TryResolveVirtualSign(WorldId.Primary, observer, 100, 100, out _), Is.False);
        Assert.That(runtime.TryRequestVirtualSignRead(player with { IsDead = true }, 100, 100), Is.False);
        Assert.That(runtime.TryRequestVirtualSignRead(player with { Position = new(0, 0) }, 100, 100), Is.False);
        Assert.That(runtime.TryRequestVirtualSignRead(player, 100, 100), Is.True);
        Assert.That(runtime.TryRequestVirtualSignRead(player, 101, 101), Is.True);
        sink.Requests.Clear();
        definition = definition with { VirtualSigns = [new(sign.Id, 0, 0, "Updated", 2)] };
        Assert.That(scene.UpdateOverlay(overlay, definition), Is.True);
        Assert.That(sink.Requests, Is.Empty);
        Assert.That(runtime.TryTakeVirtualSignRead(scene.Id, overlay, sign.Id, out var request), Is.True);
        Assert.That(request!.Value.Sign.Definition.Text, Is.EqualTo("Updated"));
        Assert.That(request.Value.Sign.Definition.Revision, Is.EqualTo(2));
        Assert.That(runtime.TryTakeVirtualSignRead(scene.Id, overlay, sign.Id, out _), Is.False);
        Assert.That(runtime.TryRequestVirtualSignRead(player, 100, 100), Is.True);
        using (var covering = runtime.CreateScene(world, "cover", "test"))
        {
            covering.AddOverlay(new(default, "corner", new(101, 101, 1, 1), 5, SceneObserverScope.Global,
                SceneTileArea.Fill(1, 1, SceneTile.Air)));
            Assert.That(runtime.TryResolveVirtualSign(world, observer, 100, 100, out _), Is.False);
            Assert.That(runtime.TryTakeVirtualSignRead(scene.Id, overlay, sign.Id, out _), Is.False);
        }
        Assert.That(runtime.TryRequestVirtualSignRead(player, 100, 100), Is.True);
        definition = definition with { Bounds = new(110, 100, 2, 2) };
        Assert.That(scene.UpdateOverlay(overlay, definition), Is.True);
        Assert.That(runtime.TryTakeVirtualSignRead(scene.Id, overlay, sign.Id, out _), Is.False);
        Assert.That(runtime.TryResolveVirtualSign(world, observer, 110, 100, out _), Is.True);
        var replacement = opened with { SessionId = PlayerSessionId.New() };
        runtime.OpenSession(world, replacement);
        Assert.That(runtime.TryResolveVirtualSign(world, observer, 110, 100, out _), Is.False);
        Assert.That(runtime.TryResolveVirtualSign(world, SceneSessionObserver.From(replacement), 110, 100, out _), Is.False);
    }

    [Test]
    public void MixedVirtualObjectsCannotOverlapAndDefinitionListsAreFrozen()
    {
        var runtime = new SceneRuntime();
        using var scene = runtime.CreateScene(WorldId.Primary, "mixed", "test");
        var chest = new SceneVirtualChestDefinition(VirtualChestId.New(), 0, 0, "container", 1, []);
        var signs = new List<SceneVirtualSignDefinition> { new(VirtualSignId.New(), 1, 1, "overlap", 1) };
        var definition = new SceneOverlayDefinition(default, "mixed", new(10, 10, 4, 4), 0, SceneObserverScope.Global,
            SceneTileArea.Fill(4, 4, new SceneTile(55, Flags: SceneTileFlags.Active))) { VirtualSigns = signs, VirtualChests = [chest] };
        Assert.Throws<ArgumentException>(() => scene.AddOverlay(definition));
        signs[0] = new(VirtualSignId.New(), 2, 2, "separate", 1);
        scene.AddOverlay(definition);
        signs.Clear();
        var stored = runtime.SnapshotScenes().Single().Overlays.Single();
        Assert.That(stored.VirtualChests, Has.Count.EqualTo(1));
        Assert.That(stored.VirtualSigns, Has.Count.EqualTo(1));
        Assert.Throws<ArgumentException>(() => new SceneVirtualSignDefinition(VirtualSignId.New(), 0, 0,
            new string('x', SceneVirtualSignDefinition.MaximumTextLength + 1), 1));
    }

    private sealed class RecordingSink : ISceneRenderSink
    {
        public List<SceneRenderRequest> Requests { get; } = [];
        public void Invalidate(SceneRenderRequest request) => Requests.Add(request);
    }
}
