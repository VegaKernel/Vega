using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

/// <summary>
/// Terraria-backed bounded entity snapshot/removal capability. All access is game-thread-only.
/// </summary>
public sealed class TerrariaWorldEntityMaintenance(IGameThreadDispatcher dispatcher) : IWorldEntityMaintenance
{
    public WorldEntityCleanupSnapshot Snapshot(WorldId worldId)
    {
        RequirePrimaryWorld(worldId);
        dispatcher.AssertGameThread();

        var items = new List<WorldItemCleanupSnapshot>();
        int itemLimit = Math.Min(global::Terraria.Main.maxItems, global::Terraria.Main.item.Length);
        for (int slot = 0; slot < itemLimit; slot++)
        {
            global::Terraria.WorldItem? item = global::Terraria.Main.item[slot];
            if (item is { active: true })
            {
                items.Add(new WorldItemCleanupSnapshot(
                    new WorldItemCleanupHandle(slot, item.timeSinceItemSpawned)));
            }
        }

        var projectiles = new List<WorldProjectileCleanupSnapshot>();
        int projectileLimit = Math.Min(
            global::Terraria.Main.maxProjectiles,
            global::Terraria.Main.projectile.Length);
        for (int slot = 0; slot < projectileLimit; slot++)
        {
            global::Terraria.Projectile? projectile = global::Terraria.Main.projectile[slot];
            if (projectile is { active: true })
            {
                projectiles.Add(new WorldProjectileCleanupSnapshot(
                    new WorldProjectileCleanupHandle(slot, projectile.key),
                    projectile.minion,
                    projectile.sentry));
            }
        }

        return new WorldEntityCleanupSnapshot(
            worldId,
            Array.AsReadOnly(items.ToArray()),
            Array.AsReadOnly(projectiles.ToArray()),
            DateTimeOffset.UtcNow);
    }

    public WorldEntityRemovalResult Remove(WorldEntityRemovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePrimaryWorld(request.WorldId);
        dispatcher.AssertGameThread();

        int removedItems = 0;
        int skippedItems = 0;
        foreach (WorldItemCleanupHandle handle in request.Items)
        {
            if (!TryGetItem(handle, out global::Terraria.WorldItem? item))
            {
                skippedItems++;
                continue;
            }

            item!.TurnToAir();
            global::Terraria.NetMessage.SendData(21, number: handle.Slot);
            removedItems++;
        }

        int removedProjectiles = 0;
        int skippedProjectiles = 0;
        foreach (WorldProjectileCleanupHandle handle in request.Projectiles)
        {
            if (!TryGetProjectile(handle, out global::Terraria.Projectile? projectile))
            {
                skippedProjectiles++;
                continue;
            }

            int key = projectile!.key;
            float positionX = projectile.position.X;
            float positionY = projectile.position.Y;
            projectile.active = false;
            projectile.timeLeft = 0;
            projectile.type = 0;
            global::Terraria.NetMessage.SendData(
                29,
                number: key,
                number2: positionX,
                number3: positionY);
            removedProjectiles++;
        }

        return new WorldEntityRemovalResult(
            removedItems,
            removedProjectiles,
            skippedItems,
            skippedProjectiles);
    }

    private static bool TryGetItem(
        WorldItemCleanupHandle handle,
        out global::Terraria.WorldItem? item)
    {
        item = null;
        if ((uint)handle.Slot >= (uint)global::Terraria.Main.item.Length ||
            handle.Slot >= global::Terraria.Main.maxItems)
        {
            return false;
        }

        item = global::Terraria.Main.item[handle.Slot];
        return item is { active: true } &&
            item.timeSinceItemSpawned >= handle.SpawnAgeTicks;
    }

    private static bool TryGetProjectile(
        WorldProjectileCleanupHandle handle,
        out global::Terraria.Projectile? projectile)
    {
        projectile = null;
        if ((uint)handle.Slot >= (uint)global::Terraria.Main.projectile.Length ||
            handle.Slot >= global::Terraria.Main.maxProjectiles)
        {
            return false;
        }

        projectile = global::Terraria.Main.projectile[handle.Slot];
        return projectile is { active: true } && projectile.key == handle.Key;
    }

    private static void RequirePrimaryWorld(WorldId worldId)
    {
        if (worldId != WorldId.Primary)
        {
            throw new NotSupportedException(
                $"TerrariaWorldEntityMaintenance currently owns only '{WorldId.Primary}'.");
        }
    }
}
