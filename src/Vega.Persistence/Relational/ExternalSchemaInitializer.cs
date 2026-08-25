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
    private const int CurrentVersion = 2;

    public async ValueTask<int> InitializeAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = await connections
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        if (provider == RelationalProvider.MySql)
        {
            return await ApplyMySqlAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await ExecuteAsync(
                    connection,
                    transaction,
                    "SELECT pg_advisory_xact_lock(621980319401);",
                    cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, HistoryTableSql, cancellationToken)
                .ConfigureAwait(false);
            int version = await ReadVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            EnsureSupported(version);

            if (version == 0)
            {
                await ExecuteAsync(connection, transaction, globalBanSchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, identitySchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, regionSchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, moderationSchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await InsertHistoryAsync(
                        connection,
                        transaction,
                        1,
                        "vega-1.0-baseline",
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                await ExecuteAsync(connection, transaction, PostgreSqlMuteSubjectsV2Sql, cancellationToken)
                    .ConfigureAwait(false);
                await InsertHistoryAsync(
                        connection,
                        transaction,
                        2,
                        "mute-stable-identities",
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 2;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return version;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<int> ApplyMySqlAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        bool lockHeld = await AcquireMySqlLockAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, HistoryTableSql, cancellationToken)
                .ConfigureAwait(false);
            int version = await ReadVersionAsync(connection, null, cancellationToken)
                .ConfigureAwait(false);
            EnsureSupported(version);
            if (version == 0)
            {
                // MySQL implicitly commits DDL. Baseline DDL is idempotent so a crash
                // before the history row is safely retried under the same lock.
                await ExecuteAsync(connection, null, globalBanSchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, null, identitySchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, null, regionSchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, null, moderationSchemaSql, cancellationToken)
                    .ConfigureAwait(false);
                await InsertHistoryAsync(
                        connection,
                        null,
                        1,
                        "vega-1.0-baseline",
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                await ExecuteAsync(connection, null, MySqlMuteSubjectsV2Sql, cancellationToken)
                    .ConfigureAwait(false);
                await InsertHistoryAsync(
                        connection,
                        null,
                        2,
                        "mute-stable-identities",
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 2;
            }

            return version;
        }
        finally
        {
            if (lockHeld)
            {
                await ReleaseMySqlLockAsync(connection).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureSupported(int version)
    {
        if (version > CurrentVersion)
        {
            throw new InvalidDataException(
                $"Database schema {version} is newer than supported schema {CurrentVersion}.");
        }
    }

    private static string HistoryTableSql =>
        """
        CREATE TABLE IF NOT EXISTS vega_schema_history (
            version INTEGER NOT NULL PRIMARY KEY,
            name VARCHAR(200) NOT NULL,
            applied_at_utc VARCHAR(40) NOT NULL
        );
        """;

    private static string PostgreSqlMuteSubjectsV2Sql =>
        """
        CREATE TABLE IF NOT EXISTS vega_mute_subjects (
            mute_id VARCHAR(36) NOT NULL REFERENCES vega_mutes(id) ON DELETE CASCADE,
            kind SMALLINT NOT NULL CHECK (kind BETWEEN 0 AND 3),
            value VARCHAR(512) NOT NULL,
            PRIMARY KEY (mute_id, kind, value)
        );
        CREATE INDEX IF NOT EXISTS ix_vega_mute_subjects_lookup
            ON vega_mute_subjects (kind, value);
        INSERT INTO vega_mute_subjects (mute_id, kind, value)
            SELECT id, subject_kind, subject_value FROM vega_mutes
            ON CONFLICT DO NOTHING;
        """;

    private static string MySqlMuteSubjectsV2Sql =>
        """
        CREATE TABLE IF NOT EXISTS vega_mute_subjects (
            mute_id VARCHAR(36) NOT NULL,
            kind SMALLINT NOT NULL,
            value VARCHAR(512) NOT NULL,
            PRIMARY KEY (mute_id, kind, value),
            INDEX ix_vega_mute_subjects_lookup (kind, value),
            CONSTRAINT fk_vega_mute_subjects_mute FOREIGN KEY (mute_id)
                REFERENCES vega_mutes(id) ON DELETE CASCADE,
            CHECK (kind BETWEEN 0 AND 3)
        );
        INSERT IGNORE INTO vega_mute_subjects (mute_id, kind, value)
            SELECT id, subject_kind, subject_value FROM vega_mutes;
        """;

    private static async ValueTask<int> ReadVersionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM vega_schema_history;";
        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async ValueTask InsertHistoryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int version,
        string name,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO vega_schema_history (version, name, applied_at_utc)
            VALUES (@version, @name, @appliedAt);
            """;
        command.AddParameter("@version", version);
        command.AddParameter("@name", name);
        command.AddParameter(
            "@appliedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> AcquireMySqlLockAsync(
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

        return true;
    }

    private static async ValueTask ReleaseMySqlLockAsync(DbConnection connection)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(CONCAT('vega-schema-', DATABASE()));";
        await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
