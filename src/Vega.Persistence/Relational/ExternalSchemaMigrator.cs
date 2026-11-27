using System.Data.Common;
using System.Globalization;

namespace Vega.Persistence.Relational;

internal enum RelationalProvider
{
    PostgreSql,
    MySql
}

internal sealed class ExternalSchemaInitializer(
    IRelationalConnectionFactory connections,
    RelationalProvider provider,
    string globalBanSchemaSql,
    string identitySchemaSql,
    string regionSchemaSql,
    string moderationSchemaSql)
{
    public async ValueTask<int> InitializeAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        bool mysql = provider == RelationalProvider.MySql;
        IEnumerable<string> sections = new[] { globalBanSchemaSql, identitySchemaSql, regionSchemaSql, moderationSchemaSql }
            .Concat(RelationalSchemaBaseline.AdditionalTables(mysql, sqlite: false));
        if (mysql)
        {
            // MySQL commits DDL implicitly. All table definitions and seeds are idempotent,
            // and the named lock covers the entire baseline until its one history row is written.
            await AcquireMySqlLockAsync(connection, cancellationToken).ConfigureAwait(false);
            try { return await RelationalSchemaBaseline.InitializeLockedAsync(connection, null, sections, cancellationToken).ConfigureAwait(false); }
            finally { await ReleaseMySqlLockAsync(connection).ConfigureAwait(false); }
        }

        // PostgreSQL supports transactional DDL and a transaction-scoped initialization lock.
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RelationalSchemaBaseline.ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(621980319401);", cancellationToken).ConfigureAwait(false);
            int version = await RelationalSchemaBaseline.InitializeLockedAsync(connection, transaction, sections, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return version;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask AcquireMySqlLockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(CONCAT('vega-schema-', DATABASE()), 30);";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (Convert.ToInt32(result, CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException(
                "Could not acquire the MySQL Vega schema initialization lock.");
        }

    }

    private static async ValueTask ReleaseMySqlLockAsync(DbConnection connection)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(CONCAT('vega-schema-', DATABASE()));";
        await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
