namespace Vega.Contracts.Worlds;

public readonly record struct WorldItemCleanupHandle(int Slot, int SpawnAgeTicks);

public readonly record struct WorldProjectileCleanupHandle(int Slot, int Key);

public sealed record WorldItemCleanupSnapshot(WorldItemCleanupHandle Handle);

public sealed record WorldProjectileCleanupSnapshot(
    WorldProjectileCleanupHandle Handle,
    bool IsMinion,
    bool IsSentry);

public sealed record WorldEntityCleanupSnapshot(
    WorldId WorldId,
    IReadOnlyList<WorldItemCleanupSnapshot> Items,
    IReadOnlyList<WorldProjectileCleanupSnapshot> Projectiles,
    DateTimeOffset SampledAt);

public sealed record WorldEntityRemovalRequest(
    WorldId WorldId,
    IReadOnlyList<WorldItemCleanupHandle> Items,
    IReadOnlyList<WorldProjectileCleanupHandle> Projectiles);

public readonly record struct WorldEntityRemovalResult(
    int RemovedItems,
    int RemovedProjectiles,
    int SkippedItems,
    int SkippedProjectiles);

/// <summary>
/// Game-thread-only access to bounded immutable world-entity snapshots and guarded removal.
/// Policy such as schedules, thresholds and exclusions belongs to modules, not this capability.
/// </summary>
public interface IWorldEntityMaintenance
{
    WorldEntityCleanupSnapshot Snapshot(WorldId worldId);

    WorldEntityRemovalResult Remove(WorldEntityRemovalRequest request);
}
