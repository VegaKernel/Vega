using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;

namespace Vega.Persistence.Relational;

public abstract class ExternalVegaDatabase : IVegaDatabase
{
    private readonly ExternalSchemaMigrator migrator;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private int schemaVersion;

    private protected ExternalVegaDatabase(
        string provider,
        string name,
        IRelationalConnectionFactory connections,
        RelationalProvider relationalProvider,
        string schemaSql,
        string identitySchemaSql,
        string regionSchemaSql,
        RelationalGlobalBanSql globalBanSql)
    {
        Provider = provider;
        Name = name;
        migrator = new ExternalSchemaMigrator(
            connections,
            relationalProvider,
            schemaSql,
            identitySchemaSql,
            regionSchemaSql);
        GlobalBans = new RelationalGlobalBanReplica(connections, globalBanSql, EnsureReady);
        var identities = new RelationalIdentityService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
        Accounts = identities;
        Authorization = identities;
        RoleLeases = new RelationalRoleLeaseService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
        Regions = new RelationalRegionService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
    }

    public string Provider { get; }

    public string Name { get; }

    public int SchemaVersion => Volatile.Read(ref schemaVersion);

    public bool IsReady => SchemaVersion > 0;

    public IGlobalBanReplica GlobalBans { get; }

    public IAccountService Accounts { get; }

    public IAuthorizationService Authorization { get; }

    public IRoleLeaseService RoleLeases { get; }

    public IRegionService Regions { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsReady)
        {
            return;
        }

        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            int version = await migrator.ApplyAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref schemaVersion, version);
            Authorization.SnapshotGroups();
            RoleLeases.SnapshotActive(DateTimeOffset.UtcNow);
            Regions.SnapshotRegions(WorldId.Primary);
        }
        finally
        {
            initialization.Release();
        }
    }

    private void EnsureReady()
    {
        if (!IsReady)
        {
            throw new InvalidOperationException(
                $"{Name} has not completed runtime initialization.");
        }
    }
}
