using Vega.Contracts.Worlds;

namespace Vega.Modules.Navigation;

public sealed class WorldPublicWarpReader(IWorldEnvironmentService environment, WarpStore store) : IWorldPublicWarpReader
{
    public async ValueTask<WorldWarpCatalogSnapshot?> ReadAsync(WorldId worldId, CancellationToken cancellationToken = default)
    {
        WorldEnvironmentSnapshot? world = await environment.CaptureAsync(worldId, cancellationToken).ConfigureAwait(false);
        if (world?.WorldId != worldId || world.FileIdentity is not { } identity || identity == Guid.Empty) return null;
        IReadOnlyList<WarpPoint> stored = await store.ReadAsync(identity, cancellationToken).ConfigureAwait(false);
        // Never return a stale persistent world's catalog after a reset/replacement during I/O.
        WorldEnvironmentSnapshot? current = await environment.CaptureAsync(worldId, cancellationToken).ConfigureAwait(false);
        if (current?.WorldId != worldId || current.FileIdentity != identity) return null;
        return new(worldId, identity, Array.AsReadOnly(stored.Where(point => !point.Hidden)
            .OrderBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
            .Select(point => new PublicWarpSnapshot(point.Name, point.Position)).ToArray()));
    }
}
