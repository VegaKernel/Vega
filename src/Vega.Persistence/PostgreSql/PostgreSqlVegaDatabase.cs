using Vega.Persistence.Relational;

namespace Vega.Persistence.PostgreSql;

public sealed class PostgreSqlVegaDatabase : ExternalVegaDatabase
{
    private PostgreSqlVegaDatabase(PostgreSqlConnectionFactory connections)
        : base(
            "postgresql",
            "PostgreSQL persistence",
            connections,
            RelationalProvider.PostgreSql,
            SchemaSql,
            GlobalBanSql)
    {
    }

    public static PostgreSqlVegaDatabase Create(string connectionString) =>
        new(new PostgreSqlConnectionFactory(connectionString));

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
