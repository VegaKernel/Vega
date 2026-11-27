using NUnit.Framework;
using TerraRuntime.Schematics;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Core.Modules;
using Vega.Core.Scenes;
using Vega.PluginSdk;
using Vega.Modules.Scenes;
using Vega.Terraria.Hooking;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginSdkWorldContextTests
{
    [Test]
    [NonParallelizable]
    public async Task RegionFacadeExposesOnlyDetachedSnapshotsFromHostedWorld()
    {
        WorldId world = new("regions");
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice" };
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "builders" };
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["build"] = false };
        var legitimate = new RegionSnapshot(1, "home", 0, 0, 10, 10, "Alice")
        { WorldId = world, Members = members, Groups = groups, Flags = flags };
        var foreign = legitimate with { Id = 2, WorldId = new("other") };
        var service = new RegionReadFixture([legitimate, foreign], legitimate);
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, null, null, Regions: service));
        await using var scope = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.Regions", Path.GetTempPath());
        IVegaPluginContext plugin = scope;
        var regions = plugin.World.Regions;
        Assert.That(regions.IsAvailable, Is.True);
        var snapshot = regions.Snapshot();
        var top = regions.FindTop(1, 1)!;
        Assert.That(service.LastWorld, Is.EqualTo(world));
        Assert.That(snapshot.Select(region => region.Id), Is.EqualTo(new[] { legitimate.Id }));
        members.Clear(); groups.Clear(); flags["build"] = true;
        Assert.That(snapshot[0].Members.Contains("alice"), Is.True);
        Assert.That(snapshot[0].Groups.Contains("BUILDERS"), Is.True);
        Assert.That(top.Flags["BUILD"], Is.False);
        service.Top = foreign;
        Assert.That(regions.FindTop(1, 1), Is.Null);
    }

    private sealed class RegionReadFixture(IReadOnlyList<RegionSnapshot> snapshots, RegionSnapshot top) : IRegionService
    {
        public WorldId? LastWorld { get; private set; }
        public RegionSnapshot Top { get; set; } = top;
        public IReadOnlyList<RegionSnapshot> SnapshotRegions() => throw new NotSupportedException();
        public RegionSnapshot? FindTopRegion(int x, int y) => throw new NotSupportedException();
        public IReadOnlyList<RegionSnapshot> SnapshotRegions(WorldId worldId) { LastWorld = worldId; return snapshots; }
        public RegionSnapshot? FindTopRegion(WorldId worldId, int x, int y) { LastWorld = worldId; return Top; }
        public ValueTask<RegionMutationResult> CreateAsync(WorldId worldId, string name, int x, int y, int width, int height, int priority, string? owner, string actor, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<RegionMutationResult> DeleteAsync(WorldId worldId, string name, long revision, string actor, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<RegionMutationResult> UpdateAsync(WorldId worldId, string name, RegionUpdate update, long revision, string actor, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<RegionMutationResult> SetAccessAsync(WorldId worldId, string name, RegionAccessKind kind, string subject, bool enabled, long revision, string actor, CancellationToken token = default) => throw new NotSupportedException();
    }

    [Test]
    [NonParallelizable]
    public async Task CustomNpcActorOwnsNativeGenerationMotionShopAndPluginLifetime()
    {
        var world = new WorldId("actors");
        var oldNpc = global::Terraria.Main.npc[0];
        var oldRight = global::Terraria.Main.rightWorld;
        var oldBottom = global::Terraria.Main.bottomWorld;
        var oldMode = global::Terraria.Main.netMode;
        var dispatcher = new EventGameThread();
        bool substitute = false;
        using var actors = new Vega.Terraria.Worlds.TerrariaNpcActorService(dispatcher, world, null, new WorldGameplayEventHub(), new NullLogger())
        {
            SpawnNative = (x, y, type) =>
            {
                global::Terraria.Main.npc[0] = new global::Terraria.NPC { whoAmI = 0, active = true, type = substitute ? 22 : type,
                    generation = 1, townNPC = true, life = 100, width = 18, height = 40 };
                return 0;
            }
        };
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, null, null, NpcActors: actors));
        var scope = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.Actor", Path.GetTempPath());
        try
        {
            global::Terraria.Main.rightWorld = global::Terraria.Main.bottomWorld = 10000;
            global::Terraria.Main.netMode = 0;
            global::Terraria.Main.npc[0] = new global::Terraria.NPC();
            var shop = new NpcShopCatalog([new("torch", 8, 5, 100, CoinPrice: 100)]);
            var api = ((IVegaPluginContext)scope).World.Npcs;
            substitute = true;
            Assert.That((await api.CreateActorAsync(new NpcActorDefinition(17, "Rejected"), new(100, 200))).Status, Is.EqualTo(NpcOperationStatus.Failed));
            Assert.That(global::Terraria.Main.npc[0].active, Is.False);
            substitute = false;
            var created = await api.CreateActorAsync(new NpcActorDefinition(17, "Trader", Shop: shop), new(100, 200));
            Assert.That(created.Status, Is.EqualTo(NpcOperationStatus.Applied));
            var actor = created.Actor!;
            var native = global::Terraria.Main.npc[0];
            Assert.That(native.GivenName, Is.EqualTo("Trader"));
            Assert.That(native.friendly && native.immortal && native.dontTakeDamage, Is.True);
            native.velocity = new(5, 2);
            Assert.That(global::HookEvents.Terraria.NPC.InvokeAI(native, () => { }).ContinueExecution, Is.False);
            Assert.That(native.velocity.X, Is.Zero);
            Assert.That(native.velocity.Y, Is.EqualTo(2));
            Assert.That(await actor.SetIntentAsync(new NpcActorIntent(NpcActorIntentKind.MoveTo, new(500, 200))), Is.True);
            Assert.That(global::HookEvents.Terraria.NPC.InvokeAI(native, () => { }).ContinueExecution, Is.False);
            Assert.That(native.velocity.X, Is.GreaterThan(0));
            Assert.That(await actor.RenameAsync("New Trader"), Is.True);
            Assert.That(await actor.TeleportAsync(new(150, 200)), Is.True);
            Assert.That(await actor.SetShopAsync(new NpcShopCatalog([new("stone", 2, 1, 50)])), Is.True);
            var snapshot = await actor.CaptureAsync();
            Assert.That(snapshot!.Name, Is.EqualTo("New Trader"));
            Assert.That(snapshot.ShopRevision, Is.EqualTo(2));
            Assert.That(snapshot.Npc.Position, Is.EqualTo(new PlayerPosition(150, 200)));

            // Even byte-generation and type reuse must not let the old lease mutate the new actor.
            var replacement = new global::Terraria.NPC { whoAmI = 0, active = true, type = 17, generation = 1 };
            global::Terraria.Main.npc[0] = replacement;
            Assert.That(await actor.RenameAsync("wrong"), Is.False);
            actor.Dispose();
            Assert.That(replacement.active, Is.True);
            replacement.active = false;
            var next = await api.CreateActorAsync(new NpcActorDefinition(17, "Another"), new(200, 200));
            Assert.That(next.Actor, Is.Not.Null);
            await scope.DisposeAsync();
            Assert.That(global::Terraria.Main.npc[0].active, Is.False);
        }
        finally
        {
            await scope.DisposeAsync();
            global::Terraria.Main.npc[0] = oldNpc;
            global::Terraria.Main.rightWorld = oldRight;
            global::Terraria.Main.bottomWorld = oldBottom;
            global::Terraria.Main.netMode = oldMode;
        }
    }
    [Test]
    [NonParallelizable]
    public async Task NativeStrikeCompletionReportsReturnedDamageAndPostStrikeSnapshotWithoutChangingVanilla()
    {
        var world = new WorldId("strike-events");
        var events = new WorldGameplayEventHub();
        var dispatcher = new EventGameThread();
        using var hooks = new NativeGameplayEventHooks(world, events, dispatcher, new NullLogger());
        hooks.Attach();
        await using var faulty = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(),
            "Plugin.FaultyStrike", Path.GetTempPath(), faultThreshold: 1, worldEvents: events);
        await using var healthy = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(),
            "Plugin.HealthyStrike", Path.GetTempPath(), worldEvents: events);
        int oldMode = global::Terraria.Main.netMode, oldPlayer = global::Terraria.Main.myPlayer;
        bool oldDedicated = global::Terraria.Main.dedServ;
        try
        {
            global::Terraria.Main.netMode = 2; global::Terraria.Main.myPlayer = 255; global::Terraria.Main.dedServ = true;
            var npc = new global::Terraria.NPC { whoAmI = 7, generation = 4, type = 1, active = true, life = 100, lifeMax = 100,
                defense = 0, realLife = -1, takenDamageMultiplier = 1, knockBackResist = 0, position = new(100, 200) };
            Assert.That(npc.StrikeNPC(10, 0, 0, false, true, 1), Is.EqualTo(10));
            Assert.That(npc.life, Is.EqualTo(90));
            faulty.WorldEvents.OnNpcStrikeCompleted(_ => throw new InvalidOperationException("fixture"));
            var observed = new List<NpcStrikeCompletedEvent>();
            healthy.WorldEvents.OnNpcStrikeCompleted(observed.Add);
            int dealt = npc.StrikeNPC(10, 0, 0, true, true, 1);
            Assert.That(dealt, Is.EqualTo(20));
            Assert.That(npc.life, Is.EqualTo(70));
            Assert.That(observed.Single(), Is.EqualTo(new NpcStrikeCompletedEvent(
                new NpcEventSnapshot(world, 7, 4, 1, new(100, 200), 70, false, false), 10, dealt, true)));
            Assert.That(faulty.Faults.Quarantined, Is.True);
            npc.position = new(300, 400);
            npc.active = false;
            Assert.That(npc.StrikeNPC(10, 0, 0, false, true, 1), Is.Zero);
            Assert.That(observed, Has.Count.EqualTo(1));
            Assert.That(observed[0].Npc.Position, Is.EqualTo(new PlayerPosition(100, 200)));
            await healthy.DisposeAsync();
            await faulty.DisposeAsync();
            Assert.That(events.HasSubscribers<NpcStrikeCompletedEvent>(), Is.False);
            npc.active = true;
            Assert.That(npc.StrikeNPC(10, 0, 0, false, true, 1), Is.EqualTo(10));
            Assert.That(observed, Has.Count.EqualTo(1));
        }
        finally { global::Terraria.Main.netMode = oldMode; global::Terraria.Main.myPlayer = oldPlayer; global::Terraria.Main.dedServ = oldDedicated; }
    }

    [Test]
    [NonParallelizable]
    public async Task NativeNpcNotificationsAreWorldScopedDetachedGuardedAndUnsubscribed()
    {
        var world = new WorldId("event-world");
        var events = new WorldGameplayEventHub();
        var dispatcher = new EventGameThread();
        using var hooks = new NativeGameplayEventHooks(world, events, dispatcher, new NullLogger());
        hooks.Attach();
        var faulty = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.FaultyEvents",
            Path.GetTempPath(), faultThreshold: 2, worldEvents: events);
        var healthy = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.HealthyEvents",
            Path.GetTempPath(), worldEvents: events);
        try
        {
            int failures = 0;
            var observed = new List<NpcEventSnapshot>();
            faulty.WorldEvents.OnNpcKilled(_ => { failures++; throw new InvalidOperationException("plugin failure"); });
            healthy.WorldEvents.OnNpcKilled(value => observed.Add(value.Npc));
            var npc = new global::Terraria.NPC { whoAmI = 7, type = 22, generation = 4, life = 0, position = new(100, 200) };
            global::TerrariaRuntime.Hooks.NPC.InvokeKilled(npc);
            npc.position = new(300, 400);
            global::TerrariaRuntime.Hooks.NPC.InvokeKilled(npc);
            global::TerrariaRuntime.Hooks.NPC.InvokeKilled(npc);
            Assert.That(failures, Is.EqualTo(2));
            Assert.That(faulty.Faults.Quarantined, Is.True);
            Assert.That(observed.Count, Is.EqualTo(3));
            Assert.That(observed[0], Is.EqualTo(new NpcEventSnapshot(world, 7, 4, 22, new(100, 200), 0, false, false)));
            var projectiles = new List<ProjectileEventSnapshot>();
            healthy.WorldEvents.OnProjectileSynchronized(e => projectiles.Add(e.Projectile));
            healthy.WorldEvents.OnProjectileRemoved(e => projectiles.Add(e.Projectile));
            bool previousDedicated = global::Terraria.Main.dedServ;
            try
            {
                global::Terraria.Main.dedServ = true;
                var projectile = new global::Terraria.Projectile { whoAmI = 2, key = (global::Terraria.DataStructures.ProjectileKey)123, type = 7, owner = 3, position = new(14, 16) };
                var buffer = new global::Terraria.MessageBuffer { whoAmI = 3 };
                global::TerrariaRuntime.Hooks.MessageBuffer.NotifyServerProjectileSync(buffer, projectile);
                projectile.position = new(20, 30);
                global::TerrariaRuntime.Hooks.MessageBuffer.NotifyServerProjectileRemoval(buffer, projectile);
                Assert.That(projectiles, Is.EqualTo(new[] { new ProjectileEventSnapshot(world, 2, 123, 7, 3, new(14, 16)),
                    new ProjectileEventSnapshot(world, 2, 123, 7, 3, new(20, 30)) }));
            }
            finally { global::Terraria.Main.dedServ = previousDedicated; }
            dispatcher.OnThread = false;
            global::TerrariaRuntime.Hooks.NPC.InvokeKilled(npc);
            Assert.That(observed.Count, Is.EqualTo(3));
            await faulty.DisposeAsync();
            await healthy.DisposeAsync();
            Assert.That(events.HasSubscribers<NpcKilledEvent>(), Is.False);
            Assert.That(events.HasSubscribers<ProjectileSynchronizedEvent>(), Is.False);
            Assert.That(events.HasSubscribers<ProjectileRemovedEvent>(), Is.False);
        }
        finally { await faulty.DisposeAsync(); await healthy.DisposeAsync(); }
    }

    private sealed class EventGameThread : IGameThreadDispatcher
    {
        public bool OnThread { get; set; } = true;
        public bool RejectPosts { get; set; }
        public int Posts { get; private set; }
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => OnThread;
        public void AssertGameThread() { }
        private Action? pending;
        public bool TryPost(string name, Action action) { Posts++; if (RejectPosts) return false; pending = action; return true; }
        public void RunPending() { OnThread = true; var action = pending; pending = null; action!(); OnThread = false; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
    }

    [Test]
    [NonParallelizable]
    public async Task NativeTickAndSaveCompletionAreWorldLocalGuardedAndRetired()
    {
        var world = new WorldId("lifecycle-events");
        var events = new WorldGameplayEventHub();
        var dispatcher = new EventGameThread();
        using var hooks = new NativeGameplayEventHooks(world, events, dispatcher, new NullLogger());
        hooks.Attach();
        await using var faulty = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(),
            "Plugin.FaultyLifecycle", Path.GetTempPath(), faultThreshold: 2, worldEvents: events);
        await using var healthy = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(),
            "Plugin.HealthyLifecycle", Path.GetTempPath(), worldEvents: events);
        bool previousDedicated = global::Terraria.Main.dedServ;
        try
        {
            global::Terraria.Main.dedServ = true;
            dispatcher.OnThread = false;
            global::TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveCompleted(true, null);
            Assert.That(dispatcher.Posts, Is.Zero, "No subscribers means no dispatch.");

            var ticks = new List<WorldTickCompletedEvent>();
            var saves = new List<WorldSaveCompletedEvent>();
            int faults = 0;
            faulty.WorldEvents.OnTickCompleted(_ => { faults++; throw new InvalidOperationException("plugin fault"); });
            healthy.WorldEvents.OnTickCompleted(e => { Assert.That(dispatcher.IsGameThread, Is.True); ticks.Add(e); });
            healthy.WorldEvents.OnSaveCompleted(e => { Assert.That(dispatcher.IsGameThread, Is.True); saves.Add(e); });

            var timer = System.Diagnostics.Stopwatch.StartNew();
            global::TerrariaRuntime.Hooks.Main.NotifyDedicatedTickCompleted(timer);
            Assert.That(ticks, Is.Empty);
            dispatcher.OnThread = true;
            for (int i = 0; i < 3; i++) global::TerrariaRuntime.Hooks.Main.NotifyDedicatedTickCompleted(timer);
            Assert.That(faults, Is.EqualTo(2));
            Assert.That(faulty.Faults.Quarantined, Is.True);
            Assert.That(ticks.Count, Is.EqualTo(3));
            Assert.That(ticks.All(tick => tick.WorldId == world && tick.Duration >= TimeSpan.Zero), Is.True);
            global::TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveCompleted(true, null);
            Assert.That(saves, Is.EqualTo(new[] { new WorldSaveCompletedEvent(world, true, null) }));

            dispatcher.OnThread = false;
            global::TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveFailure(new IOException("not exposed to plugins"));
            Assert.That(saves.Count, Is.EqualTo(1));
            Assert.That(dispatcher.Posts, Is.EqualTo(1));
            dispatcher.RunPending();
            Assert.That(saves[1], Is.EqualTo(new WorldSaveCompletedEvent(world, false, nameof(IOException))));

            dispatcher.RejectPosts = true;
            global::TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveCompleted(false, null);
            Assert.That(saves.Count, Is.EqualTo(2));
            dispatcher.RejectPosts = false;
            global::TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveCompleted(true, null);
            // In-flight queued notification must not cross adapter retirement/reattachment.
            hooks.Dispose();
            hooks.Attach();
            dispatcher.RunPending();
            Assert.That(saves.Count, Is.EqualTo(2));

            global::TerrariaRuntime.Hooks.Main.NotifyDedicatedWorldSaveCompleted(true, null);
            await healthy.DisposeAsync();
            await faulty.DisposeAsync();
            Assert.That(events.HasSubscribers<WorldTickCompletedEvent>(), Is.False);
            Assert.That(events.HasSubscribers<WorldSaveCompletedEvent>(), Is.False);
            dispatcher.RunPending();
            Assert.That(saves.Count, Is.EqualTo(2), "Pending save cannot invoke retired plugins.");
        }
        finally { global::Terraria.Main.dedServ = previousDedicated; }
    }

    [Test]
    [NonParallelizable]
    public async Task NpcTransformPoliciesFailOpenOnPluginFaultAndRespectExplicitVeto()
    {
        var events = new WorldGameplayEventHub();
        using var hooks = new NativeGameplayEventHooks(WorldId.Primary, events, new EventGameThread(), new NullLogger());
        hooks.Attach();
        var suspect = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.FaultyPolicy", Path.GetTempPath(), worldEvents: events);
        var policy = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.TransformPolicy", Path.GetTempPath(), worldEvents: events);
        try
        {
            suspect.WorldEvents.AllowNpcTransform(_ => throw new InvalidOperationException("policy fault"));
            policy.WorldEvents.AllowNpcTransform(value => value.RequestedType != 23);
            var npc = new global::Terraria.NPC { whoAmI = 1, type = 22, generation = 1 };
            bool Invoke(int requested)
            {
                float a = 0, b = 0, c = 0, d = 0;
                bool reposition = false;
                return global::TerrariaRuntime.Hooks.NPC.InvokeTransforming(npc, ref requested, ref a, ref b, ref c, ref d, ref reposition);
            }
            Assert.That(Invoke(23), Is.False);
            Assert.That(Invoke(24), Is.True);
            Assert.That(suspect.Faults.Count, Is.EqualTo(2));
            NpcLootCreatingEvent? loot = null;
            policy.WorldEvents.AllowNpcLoot(value => { loot = value; return false; });
            int cancelledSlot = global::TerrariaRuntime.Hooks.NPC.InvokeDropLoot(new global::Terraria.DataStructures.EntitySource_DebugCommand(),
                10, 20, 4, 4, 8, 3, false, 0, default, null, default, npc);
            Assert.That(cancelledSlot, Is.EqualTo(global::Terraria.Main.maxItems));
            Assert.That(loot!.Value.ItemType, Is.EqualTo(8));
            Assert.That(loot.Value.Stack, Is.EqualTo(3));
            await policy.DisposeAsync();
            Assert.That(Invoke(23), Is.True);
            Assert.That(events.HasPolicies<NpcLootCreatingEvent>(), Is.False);
            await suspect.DisposeAsync();
            Assert.That(events.HasPolicies<NpcTransformingEvent>(), Is.False);
        }
        finally { await policy.DisposeAsync(); await suspect.DisposeAsync(); }
    }

    [Test]
    public async Task PluginSchematicCaptureHasBoundedAdmissionAndReturnsDetachedCodecData()
    {
        var world = new WorldId("schematic-world");
        var dispatcher = new EventGameThread { OnThread = false };
        var source = new TestSchematicSource(dispatcher);
        var service = new SchematicSnapshotService(world, source, dispatcher);
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, null, null, Schematics: service));
        await using var scope = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.Schematic", Path.GetTempPath());
        var api = ((IVegaPluginContext)scope).World.Schematics;
        var pending = api.CaptureAsync(new SceneBounds(10, 20, 2, 2));
        Assert.That(pending.IsCompleted, Is.False);
        Assert.That((await api.CaptureAsync(new SceneBounds(10, 20, 2, 2))).Status, Is.EqualTo(WorldSchematicCaptureStatus.Busy));
        Assert.That(source.Captures, Is.Zero);
        dispatcher.RunPending();
        var result = await pending;
        Assert.That(result.Status, Is.EqualTo(WorldSchematicCaptureStatus.Captured));
        Assert.That(SchematicBinary.Deserialize(result.Data.Span).Npcs.Single().NpcType, Is.EqualTo(22));
        Assert.That(source.Captures, Is.EqualTo(1));
        Assert.That((await service.CaptureAsync(WorldId.Primary, new SceneBounds(10, 20, 2, 2))).Status, Is.EqualTo(WorldSchematicCaptureStatus.Invalid));
    }

    private sealed class TestSchematicSource(IGameThreadDispatcher dispatcher) : ISchematicCaptureSource
    {
        public int Captures { get; private set; }
        public SchematicDocument Capture(in SchematicBounds bounds)
        {
            Assert.That(dispatcher.IsGameThread, Is.True);
            Captures++;
            return new SchematicDocument { ContentVersion = 279, Width = bounds.Width, Height = bounds.Height,
                Tiles = new SchematicTile[bounds.Width * bounds.Height], Npcs = [new(22, 16, 16)] };
        }
    }

    [Test]
    public async Task SchematicPlacementDispatchesSupportedRecordsButRefusesUnrepresentableTiles()
    {
        var world = new WorldId("arena");
        var dispatcher = new EventGameThread();
        int placed = 0;
        var service = new SchematicSnapshotService(world, new TestSchematicSource(dispatcher), dispatcher,
            (document, tiles, bounds) =>
            {
                placed++;
                Assert.That(bounds, Is.EqualTo(new SceneBounds(50, 60, 2, 2)));
                Assert.That(document.Npcs.Single().NpcType, Is.EqualTo(22));
                Assert.That(document.WorldItems.Single().Item, Is.EqualTo(new SchematicItemStack(8, 1)));
                Assert.That(document.Chests.Single().Capacity, Is.EqualTo(80));
                Assert.That(document.Chests.Single().Items[79], Is.EqualTo(new SchematicItemStack(8, 2)));
                return new(WorldSchematicPlacementStatus.Applied, document.Npcs.Length);
            });
        byte[] Encode(bool unsupported) => SchematicBinary.Serialize(new SchematicDocument
        {
            ContentVersion = 279, Width = 2, Height = 2,
            Tiles = [default(SchematicTile) with { Shape = unsupported ? (byte)6 : (byte)0 }, default, default, default],
            Npcs = [new(22, 16, 16)], WorldItems = [new(new(8, 1), 0, 0)],
            Chests = [new(0, 0, "Expanded", Enumerable.Repeat(default(SchematicItemStack), 79).Append(new(8, 2)).ToArray()) { Capacity = 80 }]
        });
        Assert.That((await service.PlaceAsync(WorldId.Primary, Encode(false), 50, 60)).Status, Is.EqualTo(WorldSchematicPlacementStatus.Unavailable));
        Assert.That((await service.PlaceAsync(world, Encode(true), 50, 60)).Status, Is.EqualTo(WorldSchematicPlacementStatus.Invalid));
        Assert.That(placed, Is.Zero);
        Assert.That((await service.PlaceAsync(world, Encode(false), 50, 60)).Status, Is.EqualTo(WorldSchematicPlacementStatus.Applied));
        Assert.That(placed, Is.EqualTo(1));
    }

    [SetUp]
    public void ResetRuntime() => VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);

    [TearDown]
    public void ClearRuntime() => VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);

    [Test]
    public async Task PluginVirtualChestOwnsRevisionedContentsAndProviderLifetime()
    {
        var world = new WorldId("plugin-chests");
        var scenes = new SceneRuntime();
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, scenes, null));
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(world, opened);
        var player = new PlayerSnapshot(opened.PlayerId, opened.PlayerName, false, 100, 100, 20, 20, new(160, 160), false, 0)
        { WorldId = world, SessionId = opened.SessionId };
        var scope = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.Chests", Path.GetTempPath());
        try
        {
            var fake = ((IVegaPluginContext)scope).World.FakeProvider;
            Assert.That(fake.SupportsVirtualChests, Is.True);
            Assert.That(fake.SupportsVirtualSigns, Is.True);
            var provider = fake.CreatePersonal("display", new(10, 10, 4, 4),
                SceneTileArea.Fill(4, 4, new SceneTile(21, Flags: SceneTileFlags.Active)), [SceneSessionObserver.From(opened)]);
            using var chest = provider.CreateVirtualChest(0, 0, "Rewards", [new(0, 1, 5, 0)]);
            using var sign = provider.CreateVirtualSign(2, 0, "Welcome");
            Assert.That(sign.TrySetText(1, "Updated text"), Is.True);
            Assert.That(sign.TrySetText(1, "stale"), Is.False);
            Assert.That(scenes.TryRequestVirtualSignRead(player, 12, 10), Is.True);
            Assert.That(sign.TryTakeReadRequest(out var read), Is.True);
            Assert.That(read!.Value.Sign.Definition.Text, Is.EqualTo("Updated text"));
            Assert.That(chest.Definition.Revision, Is.EqualTo(1));
            Assert.That(chest.TryReplace(1, "Rewards", [new(0, 1, 10, 0)]), Is.True);
            Assert.That(chest.TryReplace(1, "stale", []), Is.False);
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(10));
            Assert.Throws<ArgumentException>(() => provider.CreateVirtualChest(1, 1, "overlap", []));
            Assert.That(scenes.TryRequestVirtualChestOpen(player, 10, 10), Is.True);
            Assert.That(chest.TryTakeOpenRequest(out var request), Is.True);
            Assert.That(request!.Value.Chest.Definition.Id, Is.EqualTo(chest.Id));
            Assert.That(request.Value.Chest.Definition.Revision, Is.EqualTo(2));
            for (int i = 0; i < 100; i++) { chest.TryTakeOpenRequest(out _); sign.TryTakeReadRequest(out _); }
            long beforePolls = GC.GetAllocatedBytesForCurrentThread();
            bool unexpected = false;
            for (int i = 0; i < 10_000; i++)
                unexpected |= chest.TryTakeOpenRequest(out _) | sign.TryTakeReadRequest(out _);
            long pollAllocations = GC.GetAllocatedBytesForCurrentThread() - beforePolls;
            Assert.That(unexpected, Is.False);
            Assert.That(pollAllocations, Is.LessThanOrEqualTo(4096), "Empty SDK polling must not allocate a closure or enumerator per call.");
            provider.Move(5, 0);
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(opened), 15, 10, out _), Is.True);
            Assert.That(scenes.TryResolveVirtualSign(world, SceneSessionObserver.From(opened), 17, 10, out _), Is.True);
            using var second = provider.CreateVirtualChest(2, 2, "Other", []);
            chest.Dispose();
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(opened), 15, 10, out _), Is.False);
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(opened), 17, 12, out _), Is.True);
            Assert.That(scenes.TryResolveVirtualSign(world, SceneSessionObserver.From(opened), 17, 10, out _), Is.True);
            sign.Dispose();
            Assert.That(scenes.TryResolveVirtualSign(world, SceneSessionObserver.From(opened), 17, 10, out _), Is.False);
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(opened), 17, 12, out _), Is.True);
            Assert.Throws<ObjectDisposedException>(() => _ = chest.Definition);
            await scope.DisposeAsync();
            Assert.That(scenes.HasActiveOverlays, Is.False);
            Assert.Throws<ObjectDisposedException>(() => _ = second.Definition);
        }
        finally { await scope.DisposeAsync(); }
    }

    [Test]
    public async Task FakeProviderUsesScopedLeasesAndExactSessionMembershipAcrossReloads()
    {
        var world = new WorldId("sandbox-fake");
        var scenes = new SceneRuntime();
        var visibility = new SceneVisibilityRuntime();
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, scenes, null, visibility));
        var first = new PlayerSessionOpened(PlayerSessionId.New(), new PlayerId(1), "first", DateTimeOffset.UtcNow);
        var second = new PlayerSessionOpened(PlayerSessionId.New(), new PlayerId(2), "second", DateTimeOffset.UtcNow);
        scenes.OpenSession(world, first); scenes.OpenSession(world, second);
        visibility.OpenSession(world, first); visibility.OpenSession(world, second);
        var a = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.A", Path.GetTempPath());
        var b = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.B", Path.GetTempPath());
        try
        {
            IPluginFakeProvider ownerA = ((IVegaPluginContext)a).World.FakeProvider;
            IPluginFakeProvider ownerB = ((IVegaPluginContext)b).World.FakeProvider;
            var bounds = new SceneBounds(10, 10, 2, 2);
            var stone = SceneTileArea.Fill(2, 2, new SceneTile(1, Flags: SceneTileFlags.Active));
            var air = SceneTileArea.Fill(2, 2, SceneTile.Air);
            var global = ownerA.CreateGlobal("same-name", bounds, stone);
            var competing = ownerB.CreateGlobal("same-name", bounds, air);
            Assert.That(scenes.TryResolveTile(world, first.PlayerId, 10, 10, out var tile, out _), Is.True);
            Assert.That(tile, Is.EqualTo(SceneTile.Air));
            global.SetTop();
            Assert.That(scenes.TryResolveTile(world, first.PlayerId, 10, 10, out tile, out _), Is.True);
            Assert.That(tile.TileType, Is.EqualTo(1));
            var personal = ownerA.CreatePersonal("private", new SceneBounds(20, 20, 2, 2), stone,
                [SceneSessionObserver.From(first), SceneSessionObserver.From(second)], fillOutsideWithAir: true, layer: 10);
            Assert.That(scenes.HasPersonalAirMask(world, first.PlayerId), Is.True);
            personal.SetFillOutsideWithAir(false);
            Assert.That(scenes.HasPersonalAirMask(world, first.PlayerId), Is.False);
            personal.SetFillOutsideWithAir(true);
            personal.Move(1, 0);
            personal.Draw();
            Assert.That(scenes.TryResolveTile(world, first.PlayerId, 21, 20, out _, out _), Is.True);
            var layerA = ownerA.CreateVisibilityLayer("arena", [SceneSessionObserver.From(first)]);
            var layerB = ownerB.CreateVisibilityLayer("arena", [SceneSessionObserver.From(second)]);
            layerA.AssignNpc(7);
            layerB.AssignNpc(7);
            Assert.That(layerA.RemoveNpc(7), Is.False);
            Assert.That(visibility.CanObserve(world, first.PlayerId, new(SceneEntityKind.Npc, 7)), Is.False);
            Assert.That(layerA.AssignPlayer(SceneSessionObserver.From(first)), Is.True);

            var oldClosed = new PlayerSessionClosed(first.SessionId, first.PlayerId, first.PlayerName, DateTimeOffset.UtcNow);
            scenes.CloseSession(world, oldClosed); visibility.CloseSession(world, oldClosed);
            var replacement = first with { SessionId = PlayerSessionId.New() };
            scenes.OpenSession(world, replacement); visibility.OpenSession(world, replacement);
            Assert.That(scenes.HasPersonalAirMask(world, replacement.PlayerId), Is.False);
            Assert.That(scenes.HasPersonalAirMask(world, second.PlayerId), Is.True);
            personal.Move(1, 0); // old requested session cannot be re-added by a later update
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.False);
            Assert.That(scenes.TryResolveTile(world, second.PlayerId, 22, 20, out _, out _), Is.True);
            Assert.That(layerA.AssignPlayer(SceneSessionObserver.From(first)), Is.False);
            Assert.That(layerA.AssignPlayer(SceneSessionObserver.From(replacement)), Is.True);
            Assert.That(layerA.RemovePlayer(SceneSessionObserver.From(first)), Is.False);
            personal.SetObservers([SceneSessionObserver.From(replacement)]);
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.True);
            // A delayed duplicate notification for the old generation must not clear the new one.
            scenes.CloseSession(world, oldClosed); visibility.CloseSession(world, oldClosed);
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.True);
            personal.SetEnabled(false);
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.False);
            Assert.That(scenes.HasPersonalAirMask(world, replacement.PlayerId), Is.False);
            personal.SetEnabled(true);
            for (int i = 0; i < 100; i++) ownerA.CreateGlobal("temporary", bounds, air).Dispose();
            Assert.That(scenes.SnapshotScenes(), Has.Count.EqualTo(3));

            await a.DisposeAsync();
            Assert.That(scenes.HasPersonalAirMask(world, replacement.PlayerId), Is.False);
            Assert.That(scenes.SnapshotScenes().Single().Owner, Is.EqualTo("Plugin.B"));
            Assert.That(visibility.SnapshotLayers().Single().Owner, Is.EqualTo("Plugin.B"));
            Assert.That(visibility.CanObserve(world, first.PlayerId, new(SceneEntityKind.Npc, 7)), Is.False);
            Assert.Throws<ObjectDisposedException>(() => personal.Move(1, 0));
            Assert.Throws<ObjectDisposedException>(() => ownerA.CreateGlobal("late", bounds, air));
            Assert.Throws<ObjectDisposedException>(() => ((IVegaPluginContext)a).World.Scenes.Create("late-legacy"));
            Assert.That(scenes.SnapshotScenes(), Has.Count.EqualTo(1));
            competing.Dispose(); competing.Dispose();
            Assert.That(scenes.HasActiveOverlays, Is.False);
        }
        finally { await a.DisposeAsync(); await b.DisposeAsync(); }
        Assert.That(visibility.SnapshotLayers(), Is.Empty);
    }

    [Test]
    public async Task WorldAndInventoryFacadesUseTypedRuntimeCapabilitiesAndTrackSceneLifetime()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "vega-plugin-sdk-world-tests", Guid.NewGuid().ToString("N"));
        var environment = new RecordingEnvironment();
        var tiles = new RecordingTiles();
        var chests = new RecordingChests();
        var npcs = new RecordingNpcs();
        var scenes = new RecordingScenes();
        var inventory = new RecordingInventory();
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(
            new WorldId("sandbox-a"),
            environment,
            tiles,
            chests,
            npcs,
            scenes,
            inventory));

        var scope = new HotModuleRegistrationScope(
            new NullCommandRegistry(),
            new NullLogger(),
            "Vega.Test.World",
            dataDirectory);
        IVegaPluginContext plugin = scope;

        WorldEnvironmentSnapshot? environmentSnapshot = await plugin.World.Environment.CaptureAsync();
        WorldTileAreaSnapshot? tileSnapshot = await plugin.World.Tiles.CaptureAsync(new SceneBounds(10, 20, 2, 2));
        ChestSnapshot? chestSnapshot = await plugin.World.Chests.CaptureAsync(30, 40);
        IReadOnlyList<NpcSnapshot> npcSnapshot = await plugin.World.Npcs.SnapshotAsync();
        PlayerInventorySnapshot? inventorySnapshot = await plugin.Players.Inventory.CaptureAsync(new PlayerId(3));
        ISceneLease lease = plugin.World.Scenes.Create("test-scene");

        Assert.Multiple(() =>
        {
            Assert.That(plugin.World.Current, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(environmentSnapshot?.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(tileSnapshot?.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(chestSnapshot?.Identity.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(npcSnapshot.Single().Handle.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(inventorySnapshot?.PlayerId, Is.EqualTo(new PlayerId(3)));
            Assert.That(scenes.LastOwner, Is.EqualTo("Vega.Test.World"));
            Assert.That(scenes.LastWorld, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(((RecordingSceneLease)lease).Disposed, Is.False);
        });

        WorldEnvironmentMutationResult environmentMutation = await plugin.World.Environment.PatchAsync(
            new WorldEnvironmentPatch(IsDaytime: false, TimeTicks: 1200));
        WorldTileMutationResult tileMutation = await plugin.World.Tiles.ReplaceAsync(
            tileSnapshot!,
            tileSnapshot!.Tiles);
        ChestMutationResult chestMutation = await plugin.World.Chests.ReplaceAsync(
            chestSnapshot!,
            chestSnapshot!.Items,
            "renamed");
        NpcOperationResult npcMutation = await plugin.World.Npcs.TeleportAsync(
            npcSnapshot.Single().Handle,
            new PlayerPosition(100, 200));
        InventoryMutationResult inventoryMutation = await plugin.Players.Inventory.ReplaceSlotAsync(
            inventorySnapshot!,
            new InventorySlotReplacement(0, 1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(environmentMutation.IsSuccess, Is.True);
            Assert.That(tileMutation.IsSuccess, Is.True);
            Assert.That(chestMutation.IsSuccess, Is.True);
            Assert.That(npcMutation.IsSuccess, Is.True);
            Assert.That(inventoryMutation.IsSuccess, Is.True);
            Assert.That(environment.LastActor, Is.EqualTo("Vega.Test.World"));
            Assert.That(tiles.LastActor, Is.EqualTo("Vega.Test.World"));
            Assert.That(chests.LastActor, Is.EqualTo("Vega.Test.World"));
            Assert.That(npcs.LastActor, Is.EqualTo("Vega.Test.World"));
        });

        await scope.DisposeAsync();
        Assert.That(((RecordingSceneLease)lease).Disposed, Is.True);
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }

    private sealed class RecordingEnvironment : IWorldEnvironmentService
    {
        public string? LastActor { get; private set; }

        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(new WorldEnvironmentSnapshot(
                worldId, 8400, 2400, 100, 200, true, 1000, 0, false, false,
                false, 0, 0, false, DateTimeOffset.UtcNow));

        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(
            WorldEnvironmentMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new WorldEnvironmentMutationResult(
                WorldEnvironmentMutationStatus.Applied,
                new WorldEnvironmentSnapshot(
                    request.WorldId, 8400, 2400, 100, 200,
                    request.Patch.IsDaytime ?? true,
                    request.Patch.TimeTicks ?? 1000,
                    0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow)));
        }
    }

    private sealed class RecordingTiles : IWorldTileService
    {
        public string? LastActor { get; private set; }

        public ValueTask<WorldTileAreaSnapshot?> CaptureAsync(
            WorldId worldId,
            SceneBounds bounds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldTileAreaSnapshot?>(new WorldTileAreaSnapshot(
                worldId,
                bounds,
                7,
                new SceneTileArea(bounds.Width, bounds.Height,
                    Enumerable.Repeat(SceneTile.Air, bounds.Width * bounds.Height).ToArray()),
                DateTimeOffset.UtcNow));

        public ValueTask<WorldTileMutationResult> ReplaceAsync(
            WorldTileMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new WorldTileMutationResult(WorldTileMutationStatus.Applied));
        }
    }

    private sealed class RecordingChests : IChestService
    {
        public string? LastActor { get; private set; }

        public ValueTask<ChestSnapshot?> CaptureAsync(
            ChestIdentity identity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ChestSnapshot?>(new ChestSnapshot(
                identity,
                "test",
                4,
                [new ChestItemSnapshot(0, 1, 1, 0)],
                DateTimeOffset.UtcNow));

        public ValueTask<ChestMutationResult> ReplaceAsync(
            ChestMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new ChestMutationResult(ChestMutationStatus.Applied));
        }
    }

    private sealed class RecordingNpcs : IWorldNpcService
    {
        public string? LastActor { get; private set; }

        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>([
                Npc(worldId)
            ]);

        public ValueTask<NpcSnapshot?> CaptureAsync(
            NpcHandle handle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NpcSnapshot?>(Npc(handle.WorldId));

        public ValueTask<NpcOperationResult> SpawnAsync(
            NpcSpawnRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied, Npc(request.WorldId)));
        }

        public ValueTask<NpcOperationResult> TeleportAsync(
            NpcTeleportRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied, Npc(request.Expected.WorldId)));
        }

        public ValueTask<NpcOperationResult> DespawnAsync(
            NpcDespawnRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied, Npc(request.Expected.WorldId)));
        }

        private static NpcSnapshot Npc(WorldId worldId) => new(
            new NpcHandle(worldId, 5, 1, 9),
            1,
            "Blue Slime",
            new PlayerPosition(10, 20),
            new WorldVelocity(0, 0),
            25,
            25,
            false,
            false,
            false,
            0,
            DateTimeOffset.UtcNow);
    }

    private sealed class RecordingScenes : ISceneRuntime
    {
        public bool HasActiveOverlays => false;
        public string? LastOwner { get; private set; }
        public WorldId LastWorld { get; private set; }

        public ISceneLease CreateScene(WorldId worldId, string name, string owner)
        {
            LastWorld = worldId;
            LastOwner = owner;
            return new RecordingSceneLease();
        }

        public IReadOnlyList<SceneSnapshot> SnapshotScenes() => [];
        public void ReportFailure(SceneId sceneId, string? failure) { }

        public bool TryResolveTile(
            WorldId worldId,
            PlayerId observer,
            int tileX,
            int tileY,
            out SceneTile tile,
            out OverlayId overlayId)
        {
            tile = default;
            overlayId = default;
            return false;
        }
    }

    private sealed class RecordingSceneLease : ISceneLease
    {
        public SceneId Id { get; } = SceneId.New();
        public bool Disposed { get; private set; }
        public OverlayId AddOverlay(SceneOverlayDefinition definition) => OverlayId.New();
        public bool UpdateOverlay(OverlayId overlayId, SceneOverlayDefinition definition) => true;
        public bool RemoveOverlay(OverlayId overlayId) => true;
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingInventory : IPlayerInventoryService
    {
        public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
            PlayerId playerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerInventorySnapshot?>(new PlayerInventorySnapshot(
                playerId,
                "player",
                3,
                [new InventoryItemSnapshot(0, 1, 1, 0, false, "Stone")],
                DateTimeOffset.UtcNow));

        public ValueTask<InventoryMutationResult> ReplaceSlotAsync(
            PlayerId playerId,
            long expectedRevision,
            InventorySlotReplacement replacement,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new InventoryMutationResult(InventoryMutationStatus.Applied));
    }

    private sealed class NullCommandRegistry : ICommandRegistry
    {
        public IDisposable Register(ICommandHandler handler) => new NullRegistration();
    }

    private sealed class NullRegistration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NullLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null) { }
    }
}
