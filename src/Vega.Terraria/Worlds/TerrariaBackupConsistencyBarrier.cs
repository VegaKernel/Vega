using Vega.Contracts.Backups;
using Vega.Contracts.Runtime;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaBackupConsistencyBarrier(
    IServerControl server,
    TerrariaWorldImageService worldImages) : IBackupConsistencyBarrier
{
    public string? CurrentWorldPath => string.IsNullOrWhiteSpace(global::Terraria.Main.worldPathName)
        ? null
        : global::Terraria.Main.worldPathName;

    public async ValueTask SaveAndFlushAsync(CancellationToken cancellationToken = default)
    {
        await server.SaveAsync(cancellationToken).ConfigureAwait(false);
        await worldImages.FlushPendingBuildsAsync(cancellationToken).ConfigureAwait(false);
    }
}
