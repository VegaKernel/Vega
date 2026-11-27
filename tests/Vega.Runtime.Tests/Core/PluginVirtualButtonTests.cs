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
public sealed class PluginVirtualButtonTests
{
    [Test]
    public async Task VirtualSwitchInputIsProviderOwnedSessionBoundAndNeverInvokesCanonicalInteraction()
    {
        var world = new WorldId("button-world");
        var scenes = new SceneRuntime();
        var observer = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(world, observer);
        var player = new PlayerSnapshot(observer.PlayerId, observer.PlayerName, false, 100, 100, 20, 20, new(1600, 1600), false, 0)
            { WorldId = world, SessionId = observer.SessionId };
        var directory = new Directory(player);
        VegaPluginRuntime.Configure(new(world, null, null, null, null, scenes, null));
        try
        {
            await using var owner = new HotModuleRegistrationScope(new Registry(), new Logger(), "ButtonOwner", Path.GetTempPath(), playerDirectory: directory);
            await using var otherOwner = new HotModuleRegistrationScope(new Registry(), new Logger(), "OtherButtons", Path.GetTempPath(), playerDirectory: directory);
            var fake = ((IVegaPluginContext)owner).World.FakeProvider;
            var other = ((IVegaPluginContext)otherOwner).World.FakeProvider;
            var switches = SceneTileArea.Fill(2, 2, new SceneTile(136, Flags: SceneTileFlags.Active));
            using var provider = fake.CreatePersonal("menu", new(100, 100, 2, 2), switches, [SceneSessionObserver.From(observer)], layer: 10);
            using var otherProvider = other.CreateGlobal("other", new(104, 100, 2, 2), switches, layer: 20);
            Assert.That(fake.SupportsVirtualButtons, Is.True);
            provider.SetButtonTargets([new("open", 0, 0), new("back", 1, 0)]);
            otherProvider.SetButtonTargets([new("foreign", 0, 0)]);
            Assert.Throws<ArgumentException>(() => provider.SetButtonTargets([new("duplicate", 0, 0), new("duplicate", 1, 0)]));
            Assert.Throws<ArgumentException>(() => provider.SetButtonTargets([new("outside", 2, 0)]));
            var policy = new SceneInteractionAuthorityPolicy(scenes, world, directory);
            Assert.That(Press(policy, observer.PlayerId, 99, 99), Is.EqualTo(PacketProcessingDecision.Allow), "Native real-world switches remain unchanged.");
            Assert.That(Press(policy, observer.PlayerId, 100, 101), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
            Assert.That(provider.TryTakeButtonPress(out _), Is.False, "Unregistered active tiles must not produce a plugin action.");

            Assert.That(Press(policy, observer.PlayerId, 100, 100), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
            Assert.That(otherProvider.TryTakeButtonPress(out _), Is.False, "A separate provider must not consume the owner action.");
            Assert.That(provider.TryTakeButtonPress(out var press), Is.True);
            Assert.That(press!.Observer, Is.EqualTo(SceneSessionObserver.From(observer)));
            var ownedScene = press.SceneId;
            var ownedOverlay = provider.Id;
            Assert.That(press.Target.Key, Is.EqualTo("open"));
            Assert.That(press.Position, Is.EqualTo(new WorldTilePosition(100, 100)));
            Assert.That(provider.TryTakeButtonPress(out _), Is.False);

            Press(policy, observer.PlayerId, 100, 100);
            Press(policy, observer.PlayerId, 101, 100);
            Assert.That(provider.TryTakeButtonPress(out press), Is.True);
            Assert.That(press!.Target.Key, Is.EqualTo("back"), "At most the latest intent per session is queued.");

            // Pending clicks become stale after the provider moves, a registration changes, or another provider covers it.
            Press(policy, observer.PlayerId, 100, 100);
            provider.Move(1, 0);
            Assert.That(provider.TryTakeButtonPress(out _), Is.False);
            Assert.That(Press(policy, observer.PlayerId, 101, 100), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
            Assert.That(provider.TryTakeButtonPress(out press), Is.True);
            Assert.That(press!.Target, Is.EqualTo(new SceneButtonTarget("open", 0, 0)));
            Press(policy, observer.PlayerId, 101, 100);
            provider.SetButtonTargets([new("new-menu", 0, 0)]);
            Assert.That(provider.TryTakeButtonPress(out _), Is.False);
            Press(policy, observer.PlayerId, 101, 100);
            otherProvider.Move(-3, 0);
            Assert.That(provider.TryTakeButtonPress(out _), Is.False);
            Press(policy, observer.PlayerId, 101, 100);
            Assert.That(otherProvider.TryTakeButtonPress(out press), Is.True);
            Assert.That(press!.Target.Key, Is.EqualTo("foreign"));
            otherProvider.Dispose();

            Press(policy, observer.PlayerId, 101, 100);
            directory.Player = player with { Position = new(3200, 3200) };
            Assert.That(provider.TryTakeButtonPress(out _), Is.False, "A click cannot be delivered to an observer who has moved out of reach.");
            directory.Player = player with { WorldId = new("foreign") };
            Press(policy, observer.PlayerId, 101, 100);
            Assert.That(provider.TryTakeButtonPress(out _), Is.False);
            directory.Player = player;
            Press(policy, observer.PlayerId, 101, 100);
            scenes.CloseSession(world, new(observer.SessionId, observer.PlayerId, observer.PlayerName, DateTimeOffset.UtcNow));
            var next = observer with { SessionId = PlayerSessionId.New() };
            scenes.OpenSession(world, next);
            directory.Player = player with { SessionId = next.SessionId };
            Assert.That(provider.TryTakeButtonPress(out _), Is.False, "Slot reuse must discard the previous session input.");
            Assert.That(Press(policy, observer.PlayerId, 101, 100), Is.EqualTo(PacketProcessingDecision.Allow), "Private overlay membership is not inherited by a reused slot.");
            provider.SetObservers([SceneSessionObserver.From(next)]);
            Press(policy, observer.PlayerId, 101, 100);
            await owner.DisposeAsync();
            Assert.That(((ISceneButtonRuntime)scenes).TryTakeButtonPress(ownedScene, ownedOverlay, out _), Is.False);
            Assert.That(Press(policy, observer.PlayerId, 101, 100), Is.EqualTo(PacketProcessingDecision.Allow));
        }
        finally { VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty); }
    }

    private static PacketProcessingDecision Press(IPacketProcessor<PacketView> policy, PlayerId player, short x, short y)
    {
        using var stream = new MemoryStream();
        new HitSwitch { X = x, Y = y }.ToStream(stream, false);
        var view = PacketView.FromPayload(PacketTypes.HitSwitch, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(view, PacketDirection.Inbound, new ConnectionId(player.Slot), player, DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private sealed class Directory(PlayerSnapshot player) : IPlayerDirectory
    {
        public PlayerSnapshot Player { get; set; } = player;
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [Player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? found) { found = id == Player.Id ? Player : null; return found is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? found) { found = name == Player.Name ? Player : null; return found is not null; }
    }
    private sealed class Registry : ICommandRegistry { public IDisposable Register(ICommandHandler handler) => throw new NotSupportedException(); }
    private sealed class Logger : IOperationsLogger { public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { } }
}
