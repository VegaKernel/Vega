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

`vega_schema_history` has one release baseline (version 1, `vega-initial`). All current tables are created together; pre-release upgrade chains were removed before deployment. SQLite and PostgreSQL initialize transactionally. MySQL holds a database-scoped named lock and uses idempotent DDL before recording the baseline. Pre-release database schemas are explicitly rejected without changing their schema or data; use a fresh database or export needed development data with the matching binary. TShock source imports are unchanged.

Authenticated playtime is recorded in `vega_play_sessions`, shared by primary and sandbox processes on all three providers. Each row is an exact authentication tenure in one entered native world, keyed by a server-generated authentication ID. `playtime` shows the current duration, saved world duration and saved aggregate across worlds for the authenticated player. Native callbacks only change the in-memory ledger; a single worker commits absolute millisecond checkpoints in one transaction every 30 seconds. A retry does not double-count and a final checkpoint cannot reopen a tenure. Logout, disconnect, slot replacement and native shutdown end the tenure. Account deletion cascades its history. Wall-clock changes do not affect elapsed monotonic time.

This counts authenticated residence, including AFK time, not a measure of player activity. Connection time before native world entry is excluded. Cross-world totals sum distinct per-world tenures (including any brief overlap if worlds temporarily coexist during a transfer). An abrupt process crash may lose the latest uncommitted interval; longer database outages can lose more time. The bounded per-process backlog retains existing checkpoints for retry, with an explicit warning if its capacity is exhausted; new tenures that begin during exhaustion are not recorded. A bounded final flush runs at clean shutdown. The displayed persisted total already includes any saved part of the current tenure; do not add the full live duration to it.

The first schema owns the persistent global-ban replica. Create and revoke operations use monotonic revisions; stale or duplicate delivery cannot overwrite newer state. Revocations are tombstones, including when they arrive before an older create event. Expired and revoked bans are excluded from the active snapshot.

The Vega 1.0 baseline includes accounts, inherited groups, explicit permission grants/denials, administration audit records, account-scoped temporary role leases, world-scoped regions with versioned ACL rows, and finite account/player-name mutes. Granting a temporary group atomically revokes the previous active row before inserting its replacement. Every replacement or explicit revoke is audited and revisioned; an older expiry/revoke request therefore cannot remove a newer assignment. Runtime permission checks read a preloaded one-active-lease-per-account cache and compare its absolute UTC expiration without querying SQL or keeping a timer on the player session.

This is infrastructure, not a generic DAL. `Vega.Contracts` exposes domain repositories; it never exposes SQL or `DbConnection` to Core or `ServerPlugins`.

## Backup and recovery

Back up before upgrading Vega across a released schema revision:

- SQLite: stop Vega and copy `data/vega.db` together with any `-wal`/`-shm` files, or use the SQLite online backup command while the server is running;
- PostgreSQL: use `pg_dump` with the deployment's normal credential and retention policy;
- MySQL/MariaDB: use `mysqldump` or the platform's consistent snapshot facility.

Vega refuses to open a database with an unsupported schema marker or version. Restore the matching Vega version or restore a pre-upgrade backup; never edit `vega_schema_history` to force a downgrade.

SQLite, MySQL and PostgreSQL run the same persistence and backup contract. CI also tests all six combinations of TShock SQLite/MySQL source and Vega SQLite/MySQL/PostgreSQL destination on isolated databases, with shared character/warp file-store behavior. The external contract uses disposable MySQL 8.4 and PostgreSQL 17 services. Changing the configured provider selects a different store; it does not automatically copy an existing Vega database. SSC, whitelist and warps remain in the normal shared/world-scoped file stores regardless of relational provider.
