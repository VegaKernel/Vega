using NUnit.Framework;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class TerrariaWorldEntityMaintenanceTests
{
    [Test]
    public void SnapshotIsImmutableProjectionAndRemovalRejectsStaleHandles()
    {
        global::Terraria.WorldItem[] originalItems = global::Terraria.Main.item;
        global::Terraria.Projectile[] originalProjectiles = global::Terraria.Main.projectile;
        int originalNetMode = global::Terraria.Main.netMode;
        try
        {
            var item = new global::Terraria.WorldItem
            {
                timeSinceItemSpawned = 600
            };
            item.inner.SetDefaults(1);
            var projectile = new global::Terraria.Projectile
            {
                active = true,
                minion = true,
                sentry = false
            };
            global::Terraria.Main.item = [new global::Terraria.WorldItem(), item];
            global::Terraria.Main.projectile = [projectile];
            global::Terraria.Main.netMode = 0;
            var dispatcher = new GameThreadDispatcher();
            var maintenance = new TerrariaWorldEntityMaintenance(dispatcher);

            WorldEntityCleanupSnapshot snapshot = maintenance.Snapshot(WorldId.Primary);
            WorldProjectileCleanupHandle projectileHandle = snapshot.Projectiles.Single().Handle;
            WorldEntityRemovalResult stale = maintenance.Remove(
                new WorldEntityRemovalRequest(
                    WorldId.Primary,
                    [new WorldItemCleanupHandle(1, 601)],
                    [projectileHandle with { Key = projectileHandle.Key + 1 }]));
            WorldEntityRemovalResult current = maintenance.Remove(
                new WorldEntityRemovalRequest(
                    WorldId.Primary,
                    [snapshot.Items.Single().Handle],
                    [projectileHandle]));

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Items.Count, Is.EqualTo(1));
                Assert.That(snapshot.Items[0].Handle.SpawnAgeTicks, Is.EqualTo(600));
                Assert.That(snapshot.Projectiles.Count, Is.EqualTo(1));
                Assert.That(snapshot.Projectiles[0].IsMinion, Is.True);
                Assert.That(snapshot.Projectiles[0].IsSentry, Is.False);
                Assert.That(stale, Is.EqualTo(new WorldEntityRemovalResult(0, 0, 1, 1)));
                Assert.That(current.RemovedItems, Is.EqualTo(1));
                Assert.That(current.RemovedProjectiles, Is.EqualTo(1));
                Assert.That(dispatcher.AssertCalls, Is.EqualTo(3));
            });
        }
        finally
        {
            global::Terraria.Main.item = originalItems;
            global::Terraria.Main.projectile = originalProjectiles;
            global::Terraria.Main.netMode = originalNetMode;
        }
    }

    [Test]
    public void NonPrimaryWorldIsRejectedBeforeTerrariaAccess()
    {
        var maintenance = new TerrariaWorldEntityMaintenance(new GameThreadDispatcher());

        Assert.Throws<NotSupportedException>(() => maintenance.Snapshot(new WorldId("sandbox")));
    }

    private sealed class GameThreadDispatcher : IGameThreadDispatcher
    {
        public int AssertCalls { get; private set; }
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() => AssertCalls++;
        public bool TryPost(string name, Action action)
        {
            action();
            return true;
        }
        public bool TrySchedule(string name, Func<bool> step)
        {
            step();
            return true;
        }
    }
}
