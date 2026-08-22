using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Runtime;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Persistence.Relational;

namespace Vega.Persistence.Sqlite;

public sealed class SqliteVegaDatabase : IVegaDatabase
{
    private readonly SqliteConnectionFactory connections;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly string databaseDirectory;
    private int schemaVersion;

    private SqliteVegaDatabase(
        SqliteVegaDatabaseOptions options,
        SqliteConnectionFactory connections)
    {
        this.connections = connections;
        DatabasePath = options.DatabasePath;
        databaseDirectory = Path.GetDirectoryName(options.DatabasePath) ??
            throw new InvalidOperationException(
                $"Cannot determine the database directory for '{options.DatabasePath}'.");
        GlobalBans = new SqliteGlobalBanReplica(connections, EnsureReady);
        var identities = new RelationalIdentityService(
            connections.Open,
            async cancellationToken => await connections.OpenAsync(cancellationToken).ConfigureAwait(false),
            EnsureReady);
        Accounts = identities;
        Authorization = identities;
    }

    public string Name => "SQLite persistence";

    public string Provider => "sqlite";

    public string DatabasePath { get; }

    public int SchemaVersion => Volatile.Read(ref schemaVersion);

    public bool IsReady => SchemaVersion > 0;

    public IGlobalBanReplica GlobalBans { get; }

    public IAccountService Accounts { get; }

    public IAuthorizationService Authorization { get; }

    public static SqliteVegaDatabase Create(SqliteVegaDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connections = new SqliteConnectionFactory(options);
        return new SqliteVegaDatabase(options, connections);
    }

    public static async ValueTask<SqliteVegaDatabase> OpenAsync(
        SqliteVegaDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        SqliteVegaDatabase database = Create(options);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return database;
    }

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

            Directory.CreateDirectory(databaseDirectory);
            int version = await SqliteSchemaMigrator
                .ApplyAsync(connections, cancellationToken)
                .ConfigureAwait(false);
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
                "SQLite persistence has not completed runtime initialization.");
        }
    }
}
