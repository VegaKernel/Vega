using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Vega.Persistence.Sqlite;

internal static class SqliteSchemaMigrator
{
    private const int CurrentVersion = 1;

    private static readonly Migration[] Migrations =
    [
        new(
            1,
            "global-bans",
            """
            CREATE TABLE global_bans (
                id TEXT NOT NULL PRIMARY KEY,
                subject_kind INTEGER NULL CHECK (subject_kind BETWEEN 0 AND 4),
                subject_value TEXT NULL,
                reason TEXT NULL,
                created_by TEXT NULL,
                created_at_utc TEXT NULL,
                expires_at_utc TEXT NULL,
                revision INTEGER NOT NULL CHECK (revision > 0),
                revoked INTEGER NOT NULL DEFAULT 0 CHECK (revoked IN (0, 1)),
                revoked_by TEXT NULL,
                revoked_at_utc TEXT NULL,
                CHECK (
                    revoked = 1 OR
                    (subject_kind IS NOT NULL AND subject_value IS NOT NULL AND
                     reason IS NOT NULL AND created_by IS NOT NULL AND created_at_utc IS NOT NULL)
                )
            );

            CREATE INDEX ix_global_bans_active_expiry
                ON global_bans (revoked, expires_at_utc);
            """)
    ];

    public static async ValueTask<int> ApplyAsync(
        SqliteConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await connections
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnableWriteAheadLoggingAsync(connection, cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureHistoryTableAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            int version = await ReadVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (version > CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Database schema {version} is newer than supported schema {CurrentVersion}.");
            }

            foreach (Migration migration in Migrations)
            {
                if (migration.Version <= version)
                {
                    continue;
                }

                await ExecuteMigrationAsync(
                        connection,
                        transaction,
                        migration,
                        cancellationToken)
                    .ConfigureAwait(false);
                version = migration.Version;
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

    private static async ValueTask EnableWriteAheadLoggingAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureHistoryTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS vega_schema_history (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM vega_schema_history;";
        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async ValueTask ExecuteMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Migration migration,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = migration.Sql;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText =
            """
            INSERT INTO vega_schema_history (version, name, applied_at_utc)
            VALUES ($version, $name, $appliedAt);
            """;
        history.Parameters.AddWithValue("$version", migration.Version);
        history.Parameters.AddWithValue("$name", migration.Name);
        history.Parameters.AddWithValue(
            "$appliedAt",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Migration(int Version, string Name, string Sql);
}
