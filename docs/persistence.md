# Persistence

Vega ships one domain-oriented persistence layer with three relational providers: SQLite, PostgreSQL and MySQL/MariaDB. SQLite is the deploy-ready default. The runtime reads `config/database.json`, initializes the selected provider after cheap runtime/module registration and before world loading, and fails startup if the required store cannot connect or initialize its baseline schema.

## Configuration

Default configuration:

```json
{
  "provider": "sqlite",
  "sqlitePath": "data/vega.db",
  "busyTimeoutMilliseconds": 5000,
  "connectionStringEnvironmentVariable": "VEGA_DATABASE_CONNECTION_STRING",
  "connectionString": null
}
```

Supported provider values are `sqlite`, `postgresql` (`postgres`) and `mysql` (`mariadb`). A relative SQLite path is resolved beneath the extracted server directory.

For PostgreSQL:

```powershell
$env:VEGA_DATABASE_CONNECTION_STRING = 'Host=db.example;Database=vega;Username=vega;Password=...;SSL Mode=VerifyFull'
```

For MySQL/MariaDB:

```powershell
$env:VEGA_DATABASE_CONNECTION_STRING = 'Server=db.example;Database=vega;User ID=vega;Password=...;SslMode=VerifyFull'
```

Then change only `provider` in `config/database.json`. The environment variable is preferred over `connectionString`, so a deploy archive does not need to contain a password. If an external provider is selected without a connection string, Vega stops with an actionable configuration error and does not fall back to SQLite.

## Schema and behavior

`vega_schema_history` currently contains one `vega-1.0-baseline` row. No public Vega database schema exists yet, so the former development-only sequence has been collapsed instead of being treated as a compatibility contract. SQLite and PostgreSQL create the complete baseline transactionally. MySQL DDL implicitly commits, so Vega holds a database-scoped named lock and uses idempotent baseline DDL before recording that row. Numbered forward migrations begin only after the first public schema ships.

The first schema owns the persistent global-ban replica. Create and revoke operations use monotonic revisions; stale or duplicate delivery cannot overwrite newer state. Revocations are tombstones, including when they arrive before an older create event. Expired and revoked bans are excluded from the active snapshot.

The Vega 1.0 baseline includes accounts, inherited groups, explicit permission grants/denials, administration audit records, account-scoped temporary role leases, world-scoped regions with versioned ACL rows, and finite account/player-name mutes. Granting a temporary group atomically revokes the previous active row before inserting its replacement. Every replacement or explicit revoke is audited and revisioned; an older expiry/revoke request therefore cannot remove a newer assignment. Runtime permission checks read a preloaded one-active-lease-per-account cache and compare its absolute UTC expiration without querying SQL or keeping a timer on the player session.

This is infrastructure, not a generic DAL. `Vega.Contracts` exposes domain repositories; it never exposes SQL or `DbConnection` to Core or `ServerPlugins`.

## Backup and recovery

Back up before upgrading Vega across a released schema revision:

- SQLite: stop Vega and copy `data/vega.db` together with any `-wal`/`-shm` files, or use the SQLite online backup command while the server is running;
- PostgreSQL: use `pg_dump` with the deployment's normal credential and retention policy;
- MySQL/MariaDB: use `mysqldump` or the platform's consistent snapshot facility.

Vega refuses to open a database whose schema is newer than the running binary. Restore the matching Vega version or restore a pre-upgrade backup; never edit `vega_schema_history` to force a downgrade.

Real PostgreSQL/MySQL provider contract tests require disposable database services. The normal local suite always validates SQLite persistence and provider selection; CI/deployment validation should additionally exercise the chosen external engine.
