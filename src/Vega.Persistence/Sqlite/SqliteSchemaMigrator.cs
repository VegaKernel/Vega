using Microsoft.Data.Sqlite;
using Vega.Persistence.Relational;

namespace Vega.Persistence.Sqlite;

internal static class SqliteSchemaInitializer
{
    private static readonly string[] BaselineSections =
    [
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
            """,
        """
            CREATE TABLE vega_groups (
                name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                parent_name TEXT NULL COLLATE NOCASE REFERENCES vega_groups(name) ON DELETE RESTRICT,
                revision INTEGER NOT NULL CHECK (revision > 0)
            );

            CREATE TABLE vega_group_permissions (
                group_name TEXT NOT NULL COLLATE NOCASE REFERENCES vega_groups(name) ON DELETE CASCADE,
                permission TEXT NOT NULL COLLATE NOCASE,
                effect INTEGER NOT NULL CHECK (effect IN (-1, 1)),
                PRIMARY KEY (group_name, permission)
            );

            CREATE TABLE vega_accounts (
                id INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                password_hash TEXT NOT NULL,
                group_name TEXT NOT NULL COLLATE NOCASE REFERENCES vega_groups(name) ON DELETE RESTRICT,
                culture TEXT NULL,
                last_seen_utc TEXT NULL,
                revision INTEGER NOT NULL CHECK (revision > 0)
            );

            CREATE TABLE vega_role_leases (
                id TEXT NOT NULL PRIMARY KEY,
                account_id INTEGER NOT NULL REFERENCES vega_accounts(id) ON DELETE CASCADE,
                group_name TEXT NOT NULL COLLATE NOCASE REFERENCES vega_groups(name) ON DELETE RESTRICT,
                issued_by TEXT NOT NULL,
                issued_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision > 0),
                revoked INTEGER NOT NULL DEFAULT 0 CHECK (revoked IN (0, 1))
            );

            CREATE INDEX ix_role_leases_account_expiry
                ON vega_role_leases (account_id, revoked, expires_at_utc);

            CREATE TABLE vega_administration_audit (
                id TEXT NOT NULL PRIMARY KEY,
                occurred_at_utc TEXT NOT NULL,
                actor TEXT NOT NULL,
                operation TEXT NOT NULL,
                target TEXT NOT NULL,
                before_revision INTEGER NOT NULL,
                after_revision INTEGER NOT NULL,
                outcome TEXT NOT NULL
            );

            INSERT INTO vega_groups (name, parent_name, revision) VALUES ('guest', NULL, 1);
            INSERT INTO vega_groups (name, parent_name, revision) VALUES ('default', 'guest', 1);
            INSERT INTO vega_groups (name, parent_name, revision) VALUES ('administrator', 'default', 1);
            INSERT INTO vega_group_permissions (group_name, permission, effect)
                VALUES ('administrator', '*', 1);
            """,
        """
            CREATE TABLE vega_regions (
                id INTEGER NOT NULL PRIMARY KEY,
                world_id TEXT NOT NULL COLLATE NOCASE,
                name TEXT NOT NULL COLLATE NOCASE,
                x INTEGER NOT NULL CHECK (x >= 0),
                y INTEGER NOT NULL CHECK (y >= 0),
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                priority INTEGER NOT NULL,
                owner TEXT NULL COLLATE NOCASE,
                revision INTEGER NOT NULL CHECK (revision > 0),
                UNIQUE (world_id, name)
            );

            CREATE INDEX ix_vega_regions_world_priority
                ON vega_regions (world_id, priority DESC);

            CREATE TABLE vega_region_members (
                region_id INTEGER NOT NULL REFERENCES vega_regions(id) ON DELETE CASCADE,
                account_name TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (region_id, account_name)
            );

            CREATE TABLE vega_region_groups (
                region_id INTEGER NOT NULL REFERENCES vega_regions(id) ON DELETE CASCADE,
                group_name TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (region_id, group_name)
            );

            CREATE TABLE vega_region_flags (
                region_id INTEGER NOT NULL REFERENCES vega_regions(id) ON DELETE CASCADE,
                flag TEXT NOT NULL COLLATE NOCASE,
                value INTEGER NOT NULL CHECK (value IN (0, 1)),
                PRIMARY KEY (region_id, flag)
            );
            """,
        """
            CREATE TABLE vega_mutes (
                id TEXT NOT NULL PRIMARY KEY,
                subject_kind INTEGER NOT NULL CHECK (subject_kind BETWEEN 0 AND 1),
                subject_value TEXT NOT NULL COLLATE NOCASE,
                reason TEXT NOT NULL,
                issued_by TEXT NOT NULL,
                issued_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision > 0),
                revoked INTEGER NOT NULL DEFAULT 0 CHECK (revoked IN (0, 1)),
                revoked_by TEXT NULL,
                revoked_at_utc TEXT NULL
            );

            CREATE INDEX ix_vega_mutes_active_subject
                ON vega_mutes (revoked, subject_kind, subject_value, expires_at_utc);
            """
    ];

    public static async ValueTask<int> InitializeAsync(SqliteConnectionFactory connections, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnableWriteAheadLoggingAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int version = await RelationalSchemaBaseline.InitializeLockedAsync(connection, transaction,
                BaselineSections.Concat(RelationalSchemaBaseline.AdditionalTables(mysql: false, sqlite: true)), cancellationToken).ConfigureAwait(false);
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

}
