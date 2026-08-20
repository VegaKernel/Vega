using Vega.Persistence.Relational;

namespace Vega.Persistence.MySql;

public sealed class MySqlVegaDatabase : ExternalVegaDatabase
{
    private MySqlVegaDatabase(MySqlConnectionFactory connections)
        : base(
            "mysql",
            "MySQL persistence",
            connections,
            RelationalProvider.MySql,
            SchemaSql,
            GlobalBanSql)
    {
    }

    public static MySqlVegaDatabase Create(string connectionString) =>
        new(new MySqlConnectionFactory(connectionString));

    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS global_bans (
            id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
            subject_kind SMALLINT NULL CHECK (subject_kind BETWEEN 0 AND 4),
            subject_value VARCHAR(1024) NULL,
            reason TEXT NULL,
            created_by VARCHAR(255) NULL,
            created_at_utc VARCHAR(40) NULL,
            expires_at_utc VARCHAR(40) NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            revoked BOOLEAN NOT NULL DEFAULT FALSE,
            revoked_by VARCHAR(255) NULL,
            revoked_at_utc VARCHAR(40) NULL,
            CHECK (
                revoked = TRUE OR
                (subject_kind IS NOT NULL AND subject_value IS NOT NULL AND
                 reason IS NOT NULL AND created_by IS NOT NULL AND created_at_utc IS NOT NULL)
            ),
            INDEX ix_global_bans_active_expiry (revoked, expires_at_utc)
        ) ENGINE=InnoDB;
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
        ON DUPLICATE KEY UPDATE
            subject_kind = IF(VALUES(revision) > revision, VALUES(subject_kind), subject_kind),
            subject_value = IF(VALUES(revision) > revision, VALUES(subject_value), subject_value),
            reason = IF(VALUES(revision) > revision, VALUES(reason), reason),
            created_by = IF(VALUES(revision) > revision, VALUES(created_by), created_by),
            created_at_utc = IF(VALUES(revision) > revision, VALUES(created_at_utc), created_at_utc),
            expires_at_utc = IF(VALUES(revision) > revision, VALUES(expires_at_utc), expires_at_utc),
            revoked = IF(VALUES(revision) > revision, FALSE, revoked),
            revoked_by = IF(VALUES(revision) > revision, NULL, revoked_by),
            revoked_at_utc = IF(VALUES(revision) > revision, NULL, revoked_at_utc),
            revision = IF(VALUES(revision) > revision, VALUES(revision), revision);
        """,
        """
        INSERT INTO global_bans (
            id, revision, revoked, revoked_by, revoked_at_utc)
        VALUES (@id, @revision, TRUE, @revokedBy, @revokedAt)
        ON DUPLICATE KEY UPDATE
            revoked = IF(VALUES(revision) > revision, TRUE, revoked),
            revoked_by = IF(VALUES(revision) > revision, VALUES(revoked_by), revoked_by),
            revoked_at_utc = IF(VALUES(revision) > revision, VALUES(revoked_at_utc), revoked_at_utc),
            revision = IF(VALUES(revision) > revision, VALUES(revision), revision);
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
