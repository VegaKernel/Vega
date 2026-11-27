using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Regions;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Persistence.Sqlite;

namespace Vega.Persistence;

/// <summary>Offline-only transactional import. Each family is committed independently;
/// stop all destination writers before importing. Accounts and primary regions require
/// empty destination sets, while bans preserve existing and revoked identifiers.</summary>
public interface IVegaOfflineImport
{
    ValueTask<int> ImportAccountsAsync(IReadOnlyList<LegacyAccountImport> imports, CancellationToken token = default);
    ValueTask<int> ImportBansAsync(IReadOnlyList<GlobalBanCreated> bans, CancellationToken token = default);
    ValueTask<int> ImportPrimaryRegionsAsync(IReadOnlyList<RegionSnapshot> regions, CancellationToken token = default);
    ValueTask<int> ImportHomesAsync(WorldId world, Guid worldFile, IReadOnlyList<PlayerHome> homes, CancellationToken token = default);
}
