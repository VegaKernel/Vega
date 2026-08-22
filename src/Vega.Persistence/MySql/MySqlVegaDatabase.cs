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
            IdentitySchemaSql,
            RegionSchemaSql,
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

    private const string IdentitySchemaSql =
        """
        CREATE TABLE IF NOT EXISTS vega_groups (
            name VARCHAR(255) NOT NULL PRIMARY KEY,
            parent_name VARCHAR(255) NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            CONSTRAINT fk_vega_groups_parent FOREIGN KEY (parent_name)
                REFERENCES vega_groups(name) ON DELETE RESTRICT
        ) ENGINE=InnoDB;

        CREATE TABLE IF NOT EXISTS vega_group_permissions (
            group_name VARCHAR(255) NOT NULL,
            permission VARCHAR(255) NOT NULL,
            effect SMALLINT NOT NULL CHECK (effect IN (-1, 1)),
            PRIMARY KEY (group_name, permission),
            CONSTRAINT fk_vega_permissions_group FOREIGN KEY (group_name)
                REFERENCES vega_groups(name) ON DELETE CASCADE
        ) ENGINE=InnoDB;

        CREATE TABLE IF NOT EXISTS vega_accounts (
            id INT NOT NULL PRIMARY KEY,
            name VARCHAR(255) NOT NULL UNIQUE,
            password_hash VARCHAR(512) NOT NULL,
            group_name VARCHAR(255) NOT NULL,
            culture VARCHAR(64) NULL,
            last_seen_utc VARCHAR(40) NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            CONSTRAINT fk_vega_accounts_group FOREIGN KEY (group_name)
                REFERENCES vega_groups(name) ON DELETE RESTRICT
        ) ENGINE=InnoDB;

        CREATE TABLE IF NOT EXISTS vega_role_leases (
            id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
            account_id INT NOT NULL,
            group_name VARCHAR(255) NOT NULL,
            issued_by VARCHAR(255) NOT NULL,
            issued_at_utc VARCHAR(40) NOT NULL,
            expires_at_utc VARCHAR(40) NOT NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            revoked BOOLEAN NOT NULL DEFAULT FALSE,
            INDEX ix_role_leases_account_expiry (account_id, revoked, expires_at_utc),
            CONSTRAINT fk_vega_leases_account FOREIGN KEY (account_id)
                REFERENCES vega_accounts(id) ON DELETE CASCADE,
            CONSTRAINT fk_vega_leases_group FOREIGN KEY (group_name)
                REFERENCES vega_groups(name) ON DELETE RESTRICT
        ) ENGINE=InnoDB;

        CREATE TABLE IF NOT EXISTS vega_administration_audit (
            id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
            occurred_at_utc VARCHAR(40) NOT NULL,
            actor VARCHAR(255) NOT NULL,
            operation VARCHAR(255) NOT NULL,
            target VARCHAR(255) NOT NULL,
            before_revision BIGINT NOT NULL,
            after_revision BIGINT NOT NULL,
            outcome VARCHAR(64) NOT NULL
        ) ENGINE=InnoDB;

        INSERT IGNORE INTO vega_groups (name, parent_name, revision) VALUES ('guest', NULL, 1);
        INSERT IGNORE INTO vega_groups (name, parent_name, revision) VALUES ('default', 'guest', 1);
        INSERT IGNORE INTO vega_groups (name, parent_name, revision) VALUES ('administrator', 'default', 1);
        INSERT IGNORE INTO vega_group_permissions (group_name, permission, effect)
            VALUES ('administrator', '*', 1);
        """;

    private const string RegionSchemaSql =
        """
        CREATE TABLE IF NOT EXISTS vega_regions (
            id INT NOT NULL PRIMARY KEY,
            world_id VARCHAR(255) NOT NULL,
            name VARCHAR(255) NOT NULL,
            x INT NOT NULL CHECK (x >= 0),
            y INT NOT NULL CHECK (y >= 0),
            width INT NOT NULL CHECK (width > 0),
            height INT NOT NULL CHECK (height > 0),
            priority INT NOT NULL,
            owner VARCHAR(255) NULL,
            revision BIGINT NOT NULL CHECK (revision > 0),
            UNIQUE KEY ux_vega_regions_world_name (world_id, name),
            INDEX ix_vega_regions_world_priority (world_id, priority)
        ) ENGINE=InnoDB;
        CREATE TABLE IF NOT EXISTS vega_region_members (
            region_id INT NOT NULL,
            account_name VARCHAR(255) NOT NULL,
            PRIMARY KEY (region_id, account_name),
            CONSTRAINT fk_vega_region_members_region FOREIGN KEY (region_id)
                REFERENCES vega_regions(id) ON DELETE CASCADE
        ) ENGINE=InnoDB;
        CREATE TABLE IF NOT EXISTS vega_region_groups (
            region_id INT NOT NULL,
            group_name VARCHAR(255) NOT NULL,
            PRIMARY KEY (region_id, group_name),
            CONSTRAINT fk_vega_region_groups_region FOREIGN KEY (region_id)
                REFERENCES vega_regions(id) ON DELETE CASCADE
        ) ENGINE=InnoDB;
        CREATE TABLE IF NOT EXISTS vega_region_flags (
            region_id INT NOT NULL,
            flag VARCHAR(255) NOT NULL,
            value SMALLINT NOT NULL CHECK (value IN (0, 1)),
            PRIMARY KEY (region_id, flag),
            CONSTRAINT fk_vega_region_flags_region FOREIGN KEY (region_id)
                REFERENCES vega_regions(id) ON DELETE CASCADE
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
