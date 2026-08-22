namespace Vega.Contracts.Regions;

using Vega.Contracts.Worlds;

public interface IRegionService
{
    IReadOnlyList<RegionSnapshot> SnapshotRegions();

    RegionSnapshot? FindTopRegion(int tileX, int tileY);

    IReadOnlyList<RegionSnapshot> SnapshotRegions(WorldId worldId);

    RegionSnapshot? FindTopRegion(WorldId worldId, int tileX, int tileY);

    ValueTask<RegionMutationResult> CreateAsync(
        WorldId worldId,
        string name,
        int x,
        int y,
        int width,
        int height,
        int priority,
        string? owner,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<RegionMutationResult> DeleteAsync(
        WorldId worldId,
        string name,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<RegionMutationResult> UpdateAsync(
        WorldId worldId,
        string name,
        RegionUpdate update,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<RegionMutationResult> SetAccessAsync(
        WorldId worldId,
        string name,
        RegionAccessKind kind,
        string subject,
        bool enabled,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);
}

public sealed record RegionUpdate(
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    int? Priority = null,
    string? Owner = null,
    bool ClearOwner = false,
    string? Flag = null,
    bool? FlagValue = null);

public enum RegionAccessKind
{
    Member,
    Group
}

public sealed record RegionMutationResult(
    bool Succeeded,
    string Code,
    RegionSnapshot? Before = null,
    RegionSnapshot? After = null);
