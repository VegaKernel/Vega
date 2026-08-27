using Vega.Persistence.Relational;
using Npgsql;

namespace Vega.Persistence.PostgreSql;

public sealed class PostgreSqlVegaDatabase : ExternalVegaDatabase
{
    private readonly string connectionString;

    private PostgreSqlVegaDatabase(string connectionString)
        : base(
            "postgresql",
            "PostgreSQL persistence",
            new PostgreSqlConnectionFactory(connectionString),
            RelationalProvider.PostgreSql,
            SchemaSql,
            IdentitySchemaSql,
            RegionSchemaSql,
            ModerationSchemaSql,
            GlobalBanSql)
    {
        this.connectionString = connectionString;
    }

    public static PostgreSqlVegaDatabase Create(string connectionString) =>
        new(connectionString);

    public override string BackupFileName => "vega-postgresql.dump";

    public override async ValueTask WriteBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        EnsureBackupReady();
        var value = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(value.Database)) throw new InvalidDataException("PostgreSQL database name is required for backup.");
        await RunDumpAsync(
            "pg_dump",
            [
                $"--host={value.Host}",
                $"--port={value.Port}",
                $"--username={value.Username}",
                $"--dbname={value.Database}",
                "--format=custom",
                "--no-password",
                $"--file={Path.GetFullPath(destinationPath)}"
            ],
            new Dictionary<string, string> { ["PGPASSWORD"] = value.Password ?? string.Empty },
            destinationPath,
            cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql =
        """
        CREATE TABLE global_bans (
            id VARCHAR(36) NOT NULL PRIMARY KEY,
            subject_kind SMALLINT NULL CHECK (subject_kind BETWEEN 0 AND 4),
            subject_value TEXT NULL,
            reason TEXT NULL,
            created_by TEXT NULL,
            created_at_utc VARCHAR(40) NULL,
            expires_at_utc VARCHAR(40) NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            revoked BOOLEAN NOT NULL DEFAULT FALSE,
            revoked_by TEXT NULL,
            revoked_at_utc VARCHAR(40) NULL,
            CHECK (
                revoked OR
                (subject_kind IS NOT NULL AND subject_value IS NOT NULL AND
                 reason IS NOT NULL AND created_by IS NOT NULL AND created_at_utc IS NOT NULL)
            )
        );

        CREATE INDEX ix_global_bans_active_expiry
            ON global_bans (revoked, expires_at_utc);
        """;

    private const string IdentitySchemaSql =
        """
        CREATE TABLE vega_groups (
            name VARCHAR(255) NOT NULL PRIMARY KEY,
            parent_name VARCHAR(255) NULL REFERENCES vega_groups(name) ON DELETE RESTRICT,
            revision BIGINT NOT NULL CHECK (revision > 0)
        );

        CREATE TABLE vega_group_permissions (
            group_name VARCHAR(255) NOT NULL REFERENCES vega_groups(name) ON DELETE CASCADE,
            permission VARCHAR(255) NOT NULL,
            effect SMALLINT NOT NULL CHECK (effect IN (-1, 1)),
            PRIMARY KEY (group_name, permission)
        );

        CREATE TABLE vega_accounts (
            id INTEGER NOT NULL PRIMARY KEY,
            name VARCHAR(255) NOT NULL UNIQUE,
            password_hash VARCHAR(512) NOT NULL,
            group_name VARCHAR(255) NOT NULL REFERENCES vega_groups(name) ON DELETE RESTRICT,
            culture VARCHAR(64) NULL,
            last_seen_utc VARCHAR(40) NULL,
            revision BIGINT NOT NULL CHECK (revision > 0)
        );

        CREATE TABLE vega_account_identity_evidence (
            account_id INTEGER NOT NULL REFERENCES vega_accounts(id) ON DELETE CASCADE,
            kind SMALLINT NOT NULL CHECK (kind BETWEEN 0 AND 1),
            value VARCHAR(512) NOT NULL,
            first_seen_utc VARCHAR(40) NOT NULL,
            last_seen_utc VARCHAR(40) NOT NULL,
            observation_count BIGINT NOT NULL CHECK (observation_count > 0),
            PRIMARY KEY (account_id, kind, value)
        );

        CREATE INDEX ix_account_identity_evidence_lookup
            ON vega_account_identity_evidence (kind, value, last_seen_utc);

        CREATE TABLE vega_role_leases (
            id VARCHAR(36) NOT NULL PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES vega_accounts(id) ON DELETE CASCADE,
            group_name VARCHAR(255) NOT NULL REFERENCES vega_groups(name) ON DELETE RESTRICT,
            issued_by VARCHAR(255) NOT NULL,
            issued_at_utc VARCHAR(40) NOT NULL,
            expires_at_utc VARCHAR(40) NOT NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            revoked BOOLEAN NOT NULL DEFAULT FALSE
        );

        CREATE INDEX ix_role_leases_account_expiry
            ON vega_role_leases (account_id, revoked, expires_at_utc);

        CREATE TABLE vega_administration_audit (
            id VARCHAR(36) NOT NULL PRIMARY KEY,
            occurred_at_utc VARCHAR(40) NOT NULL,
            actor VARCHAR(255) NOT NULL,
            operation VARCHAR(255) NOT NULL,
            target VARCHAR(255) NOT NULL,
            before_revision BIGINT NOT NULL,
            after_revision BIGINT NOT NULL,
            outcome VARCHAR(64) NOT NULL
        );

        INSERT INTO vega_groups (name, parent_name, revision) VALUES ('guest', NULL, 1);
        INSERT INTO vega_groups (name, parent_name, revision) VALUES ('default', 'guest', 1);
        INSERT INTO vega_groups (name, parent_name, revision) VALUES ('administrator', 'default', 1);
        INSERT INTO vega_group_permissions (group_name, permission, effect)
            VALUES ('administrator', '*', 1);
        """;

    private const string RegionSchemaSql =
        """
        CREATE TABLE vega_regions (
            id INTEGER NOT NULL PRIMARY KEY,
            world_id VARCHAR(255) NOT NULL,
            name VARCHAR(255) NOT NULL,
            x INTEGER NOT NULL CHECK (x >= 0),
            y INTEGER NOT NULL CHECK (y >= 0),
            width INTEGER NOT NULL CHECK (width > 0),
            height INTEGER NOT NULL CHECK (height > 0),
            priority INTEGER NOT NULL,
            owner VARCHAR(255) NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            UNIQUE (world_id, name)
        );
        CREATE INDEX ix_vega_regions_world_priority
            ON vega_regions (world_id, priority DESC);
        CREATE TABLE vega_region_members (
            region_id INTEGER NOT NULL REFERENCES vega_regions(id) ON DELETE CASCADE,
            account_name VARCHAR(255) NOT NULL,
            PRIMARY KEY (region_id, account_name)
        );
        CREATE TABLE vega_region_groups (
            region_id INTEGER NOT NULL REFERENCES vega_regions(id) ON DELETE CASCADE,
            group_name VARCHAR(255) NOT NULL,
            PRIMARY KEY (region_id, group_name)
        );
        CREATE TABLE vega_region_flags (
            region_id INTEGER NOT NULL REFERENCES vega_regions(id) ON DELETE CASCADE,
            flag VARCHAR(255) NOT NULL,
            value SMALLINT NOT NULL CHECK (value IN (0, 1)),
            PRIMARY KEY (region_id, flag)
        );
        """;

    private const string ModerationSchemaSql =
        """
        CREATE TABLE vega_mutes (
            id VARCHAR(36) NOT NULL PRIMARY KEY,
            subject_kind SMALLINT NOT NULL CHECK (subject_kind BETWEEN 0 AND 1),
            subject_value VARCHAR(255) NOT NULL,
            reason TEXT NOT NULL,
            issued_by VARCHAR(255) NOT NULL,
            issued_at_utc VARCHAR(40) NOT NULL,
            expires_at_utc VARCHAR(40) NOT NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            revoked BOOLEAN NOT NULL DEFAULT FALSE,
            revoked_by VARCHAR(255) NULL,
            revoked_at_utc VARCHAR(40) NULL
        );
        CREATE INDEX ix_vega_mutes_active_subject
            ON vega_mutes (revoked, subject_kind, subject_value, expires_at_utc);

        CREATE TABLE vega_mute_subjects (
            mute_id VARCHAR(36) NOT NULL REFERENCES vega_mutes(id) ON DELETE CASCADE,
            kind SMALLINT NOT NULL CHECK (kind BETWEEN 0 AND 3),
            value VARCHAR(512) NOT NULL,
            PRIMARY KEY (mute_id, kind, value)
        );
        CREATE INDEX ix_vega_mute_subjects_lookup
            ON vega_mute_subjects (kind, value);
        """;

    private static RelationalGlobalBanSql GlobalBanSql { get; } = new(
        """
        INSERT INTO global_bans (
            id, subject_kind, subject_value, reason, created_by,
            created_at_utc, expires_at_utc, revision, revoked,
            revoked_by, revoked_at_utc)
        VALUES (
            @id, @subjectKind, @subjectValue, @reason, @createdBy,
            @createdAt, @expiresAt, @revision, FALSE, NULL, NULL)
        ON CONFLICT (id) DO UPDATE SET
            subject_kind = EXCLUDED.subject_kind,
            subject_value = EXCLUDED.subject_value,
            reason = EXCLUDED.reason,
            created_by = EXCLUDED.created_by,
            created_at_utc = EXCLUDED.created_at_utc,
            expires_at_utc = EXCLUDED.expires_at_utc,
            revision = EXCLUDED.revision,
            revoked = FALSE,
            revoked_by = NULL,
            revoked_at_utc = NULL
        WHERE EXCLUDED.revision > global_bans.revision;
        """,
        """
        INSERT INTO global_bans (
            id, revision, revoked, revoked_by, revoked_at_utc)
        VALUES (@id, @revision, TRUE, @revokedBy, @revokedAt)
        ON CONFLICT (id) DO UPDATE SET
            revision = EXCLUDED.revision,
            revoked = TRUE,
            revoked_by = EXCLUDED.revoked_by,
            revoked_at_utc = EXCLUDED.revoked_at_utc
        WHERE EXCLUDED.revision > global_bans.revision;
        """,
        """
        SELECT id, subject_kind, subject_value, reason, created_by,
               created_at_utc, expires_at_utc, revision
        FROM global_bans
        WHERE revoked = FALSE
          AND subject_kind IS NOT NULL
          AND (expires_at_utc IS NULL OR expires_at_utc > @now)
        ORDER BY created_at_utc, id;
        """);
}
