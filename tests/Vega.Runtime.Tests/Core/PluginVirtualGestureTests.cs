using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Modules;
using Vega.Core.Networking;
using Vega.Core.Scenes;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture, NonParallelizable]
public sealed class PluginVirtualGestureTests
{
    [Test]
    public async Task ReusableWidgetPanelRendersLabelsDispatchesExactInputAndOwnsItsLifetime()
    {
        var world = new WorldId("widgets");
        var scenes = new SceneRuntime();
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(world, opened);
        var player = new PlayerSnapshot(opened.PlayerId, opened.PlayerName, false, 100, 100, 20, 20, new(1600, 1600), false, 0)
            { WorldId = world, SessionId = opened.SessionId };
        var directory = new Directory(player);
        var clicks = new List<SceneButtonPress>();
        var drags = new List<TileUiDragEvent>();
        VegaPluginRuntime.Configure(new(world, null, null, null, null, scenes, null));
        try
        {
            await using var scope = new HotModuleRegistrationScope(new Registry(), new Logger(), "WidgetOwner", Path.GetTempPath(), playerDirectory: directory);
            IVegaPluginContext plugin = scope;
            var background = new SceneTile(0, WallType: 4);
            var layout = new TileUiLayout(8, 6, background).Container(new(1, 1, 6, 4), container => container
                .Label(new(0, 0, 4, 2), "A1", color: 2, background: background)
                .Button("confirm", 4, 1, new(136, Flags: SceneTileFlags.Active), clicks.Add)
                .Drag("slider", new(0, 2, 4, 2), drags.Add));
            Assert.Throws<ArgumentOutOfRangeException>(() => layout.Container(new(0, 0, 2, 2), c => c.Paint(new(1, 0, 2, 1), SceneTile.Air)));
            Assert.Throws<ArgumentException>(() => layout.Label(new(0, 0, 2, 2), "too long"));
            var snapshot = layout.Snapshot();
            Assert.That(snapshot[1, 1], Is.EqualTo(new SceneTile(337, WallType: 4, FrameX: 360, TileColor: 2, Flags: SceneTileFlags.Active)));
            Assert.That(snapshot[2, 2].FrameX, Is.EqualTo(378));
            Assert.That(snapshot[2, 2].FrameY, Is.EqualTo(18));
            Assert.That(snapshot[3, 1].FrameX, Is.EqualTo(36));

            var panel = layout.CreatePersonalPanel(plugin, "widgets", 100, 100, [SceneSessionObserver.From(opened)]);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert.That(scenes.TryRequestButtonPress(player, 105, 102), Is.True);
            Assert.That(scenes.ObserveGesturePosition(player, 4, 10, new(101, 103), now), Is.True);
            Assert.That(panel.Dispatch(1), Is.EqualTo(1));
            Assert.That(clicks.Single().Observer, Is.EqualTo(SceneSessionObserver.From(opened)));
            // Even when one event per tick is allowed and more button input is pending, drag gets its turn.
            Assert.That(scenes.TryRequestButtonPress(player, 105, 102), Is.True);
            Assert.That(panel.Dispatch(1), Is.EqualTo(1));
            Assert.That(drags.Single().Phase, Is.EqualTo(SceneGesturePhase.Begin));
            Assert.That(drags.Single().LocalOrigin, Is.EqualTo(new WorldTilePosition(1, 3)));
            layout.Label(new(1, 1, 4, 2), "B2", 2, background: background);
            panel.Redraw();
            Assert.That(panel.Provider.Definition.Tiles[1, 1].FrameX, Is.EqualTo(396));
            Assert.That(scenes.ObserveGesturePosition(player, 4, 10, new(103, 104), now), Is.True);
            Assert.That(scenes.TryCompleteGesture(player, new(101, 103), new(103, 104), true, now), Is.True);
            panel.Dispatch();
            Assert.That(drags.Select(d => d.Phase), Is.EqualTo(new[] { SceneGesturePhase.Begin, SceneGesturePhase.Move, SceneGesturePhase.End }));
            Assert.That(drags.Last().DeltaX, Is.EqualTo(2));
            Assert.That(drags.Last().DeltaY, Is.EqualTo(1));
            Assert.That(drags.All(d => d.Observer.Session == opened.SessionId), Is.True);

            Assert.That(scenes.TryRequestButtonPress(player, 105, 102), Is.True);
            scenes.CloseSession(world, new(opened.SessionId, opened.PlayerId, opened.PlayerName, now));
            var next = opened with { SessionId = PlayerSessionId.New() };
            scenes.OpenSession(world, next);
            directory.Player = player with { SessionId = next.SessionId };
            Assert.That(panel.Dispatch(), Is.Zero, "Reused player slots cannot deliver old panel input.");
            Assert.That(scenes.TryRequestButtonPress(directory.Player, 105, 102), Is.False, "Personal panels must not become visible to a replacement connection.");
            await scope.DisposeAsync();
            Assert.That(panel.Dispatch(), Is.Zero, "Plugin scope owns the panel and stops callbacks at retirement.");
        }
        finally { VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty); }
    }

    [Test]
    public async Task GestureCapturesExactSessionAndProviderWithCoalescedMoveAndTerminalOutcome()
    {
        var world = new WorldId("gesture-world");
        var scenes = new SceneRuntime();
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(world, opened);
        var player = new PlayerSnapshot(opened.PlayerId, opened.PlayerName, false, 100, 100, 20, 20, new(1600, 1600), false, 0)
            { WorldId = world, SessionId = opened.SessionId };
        var directory = new Directory(player);
        var policy = new SceneInteractionAuthorityPolicy(scenes, world, directory);
        VegaPluginRuntime.Configure(new(world, null, null, null, null, scenes, null));
        try
        {
            await using var scope = new HotModuleRegistrationScope(new Registry(), new Logger(), "GestureOwner", Path.GetTempPath(), playerDirectory: directory);
            var fake = ((IVegaPluginContext)scope).World.FakeProvider;
            var layout = new TileUiLayout(4, 4).Button("action", 1, 3, new(136, Flags: SceneTileFlags.Active))
                .Gesture("drag", new(0, 0, 4, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => layout.Paint(new(3, 3, 2, 1), SceneTile.Air));
            using var provider = layout.CreatePersonal(fake, "panel", 100, 100, [SceneSessionObserver.From(opened)]);
            Assert.That(fake.SupportsVirtualGestures, Is.True);
            Assert.That(scenes.TryRequestButtonPress(player, 101, 103), Is.True);
            Assert.That(provider.TryTakeButtonPress(out var click), Is.True);
            Assert.That(click!.Target.Key, Is.EqualTo("action"));
            Assert.Throws<ArgumentException>(() => provider.SetGestureTargets([new("a", new(0, 0, 3, 3)), new("b", new(2, 2, 2, 2))]));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert.That(scenes.ObserveGesturePosition(player, 4, 10, new(100, 100), now), Is.True);
            Assert.That(scenes.ObserveGesturePosition(player, 4, 10, new(101, 100), now), Is.True);
            Assert.That(scenes.ObserveGesturePosition(player, 4, 10, new(102, 101), now), Is.True);
            // Content redraws keep stable control geometry and capture intact.
            layout.Paint(new(0, 0, 2, 2), new(1, Flags: SceneTileFlags.Active)).Redraw(provider);
            Assert.That(provider.Definition.Tiles[0, 0].TileType, Is.EqualTo(1));
            Assert.That(Release(policy, player.Id, new(100, 100), new(102, 101)), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
            Assert.That(provider.TryTakeGesture(out var begin), Is.True);
            Assert.That(begin!.Phase, Is.EqualTo(SceneGesturePhase.Begin));
            Assert.That(begin.Position, Is.EqualTo(new WorldTilePosition(100, 100)));
            Assert.That(provider.TryTakeGesture(out var move), Is.True);
            Assert.That(move!.Phase, Is.EqualTo(SceneGesturePhase.Move));
            Assert.That(move.Position, Is.EqualTo(new WorldTilePosition(102, 101)));
            Assert.That(provider.TryTakeGesture(out var end), Is.True);
            Assert.That(end!.Phase, Is.EqualTo(SceneGesturePhase.End));
            Assert.That(end.GestureId, Is.EqualTo(begin.GestureId));
            Assert.That(end.Observer, Is.EqualTo(SceneSessionObserver.From(opened)));
            Assert.That(provider.TryTakeGesture(out _), Is.False);

            Assert.That(scenes.ObserveGesturePosition(player, 4, 11, new(100, 100), now), Is.True);
            Assert.That(provider.TryTakeGesture(out _), Is.True);
            directory.HoldingTool = false;
            scenes.TickGestures(directory, directory, now);
            Assert.That(provider.TryTakeGesture(out var cancel), Is.True);
            Assert.That(cancel!.Phase, Is.EqualTo(SceneGesturePhase.Cancel));
            Assert.That(Release(policy, player.Id, new(100, 100), new(104, 100)), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));

            directory.HoldingTool = true;
            Assert.That(scenes.ObserveGesturePosition(player, 4, 12, new(100, 100), now), Is.True);
            provider.Move(1, 0);
            Assert.That(provider.TryTakeGesture(out _), Is.False, "Moving a panel retires its original coordinates.");
            Assert.That(scenes.ObserveGesturePosition(player, 4, 13, new(101, 100), now), Is.True);
            scenes.CloseSession(world, new(opened.SessionId, opened.PlayerId, opened.PlayerName, now));
            var next = opened with { SessionId = PlayerSessionId.New() };
            scenes.OpenSession(world, next);
            directory.Player = player with { SessionId = next.SessionId };
            Assert.That(provider.TryTakeGesture(out _), Is.False);

            // Ordinary wiring not intersecting a visible virtual panel remains unchanged.
            Assert.That(Release(policy, player.Id, new(95, 95), new(96, 96)), Is.EqualTo(PacketProcessingDecision.Allow));
            provider.SetObservers([SceneSessionObserver.From(next)]);
            provider.SetGestureTargets([]);
            Assert.That(Release(policy, player.Id, new(100, 100), new(103, 100)), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        }
        finally { VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty); }
    }

    private static PacketProcessingDecision Release(IPacketProcessor<PacketView> policy, PlayerId player, WorldTilePosition start, WorldTilePosition end)
    {
        using var stream = new MemoryStream();
        new MassWireOperation { StartX = (short)start.X, StartY = (short)start.Y, EndX = (short)end.X, EndY = (short)end.Y }.ToStream(stream, false);
        var view = PacketView.FromPayload(PacketTypes.MassWireOperation, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(view, PacketDirection.Inbound, new ConnectionId(player.Slot), player, DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private sealed class Directory(PlayerSnapshot player) : IPlayerDirectory, ISceneGestureToolState
    {
        public PlayerSnapshot Player { get; set; } = player;
        public bool HoldingTool { get; set; } = true;
        public bool IsHoldingGestureTool(PlayerSnapshot player) => HoldingTool && player.SessionId == Player.SessionId;
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [Player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? found) { found = id == Player.Id ? Player : null; return found is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? found) { found = name == Player.Name ? Player : null; return found is not null; }
    }

    private sealed class Registry : ICommandRegistry { public IDisposable Register(ICommandHandler handler) => throw new NotSupportedException(); }
    private sealed class Logger : IOperationsLogger { public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { } }
}
