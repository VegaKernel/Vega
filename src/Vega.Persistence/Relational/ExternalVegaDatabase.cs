using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;

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
        RelationalGlobalBanSql globalBanSql)
    {
        Provider = provider;
        Name = name;
        migrator = new ExternalSchemaMigrator(
            connections,
            relationalProvider,
            schemaSql,
            identitySchemaSql);
        GlobalBans = new RelationalGlobalBanReplica(connections, globalBanSql, EnsureReady);
        var identities = new RelationalIdentityService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
        Accounts = identities;
        Authorization = identities;
    }

    public string Provider { get; }

    public string Name { get; }

    public int SchemaVersion => Volatile.Read(ref schemaVersion);

    public bool IsReady => SchemaVersion > 0;

    public IGlobalBanReplica GlobalBans { get; }

    public IAccountService Accounts { get; }

    public IAuthorizationService Authorization { get; }

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
