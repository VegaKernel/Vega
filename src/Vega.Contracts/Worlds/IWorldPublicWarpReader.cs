using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

public sealed record PublicWarpSnapshot(string Name, PlayerPosition Position);

public sealed record WorldWarpCatalogSnapshot(WorldId WorldId, Guid FileIdentity, IReadOnlyList<PublicWarpSnapshot> Warps);

/// <summary>Read-only public warp listing for a hosted runtime world; hidden warps are omitted.</summary>
public interface IWorldPublicWarpReader
{
    ValueTask<WorldWarpCatalogSnapshot?> ReadAsync(WorldId worldId, CancellationToken cancellationToken = default);
}
