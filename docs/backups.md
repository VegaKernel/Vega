# Deployment backups

Vega creates deploy-local recovery archives without stopping the server. Scheduling is
configured by exactly three settings in `config/backups.json`:

```json
{
  "enabled": false,
  "interval": "06:00:00",
  "retainedBackups": 7
}
```

`interval` accepts an invariant `TimeSpan` from five minutes through 30 days.
`retainedBackups` is bounded to 1–365. `enabled` controls the schedule; an authorized
operator may still run `backup` manually. `backup status` reports the same bounded
state shown by Operations/TUI. Concurrent manual/scheduled runs are rejected.

## Consistency and contents

The backup first joins the normal world-save barrier. Terraria snapshots mutable world
state on the game thread, completes the worker save and, when enabled, Vega waits for
the matching `.vworld` rebuild. Only then does the backup capture the database:

- SQLite uses its online backup API and never copies a live database/WAL pair;
- PostgreSQL runs `pg_dump` in custom format with `PGPASSWORD` passed only through the
  child environment;
- MySQL/MariaDB runs `mysqldump --single-transaction` with `MYSQL_PWD` passed only
  through the child environment.

The external provider's dump tool must be on `PATH`. A missing tool or non-zero exit
fails the run without publishing an archive.

Staging lives under `data/backup-staging/`, outside the visible archive set. Vega
validates the ZIP before atomically moving it to `backups/Vega-backup-<UTC>.zip`.
Each archive contains `world/`, `database/`, redacted JSON beneath `config/`,
`manifest.json` and `checksums.sha256`. Password, token, secret and connection-string
properties are replaced with `<redacted>`. Retention only deletes matching archive
files whose resolved path remains beneath the fixed `backups/` directory.

## Restore drill

1. Copy the archive away from the live deployment and verify every line in
   `checksums.sha256`.
2. Stop the target Vega process. Restore the `.wld` and optional `.vworld` from
   `world/` to the configured world path.
3. Restore `vega.sqlite3` as the configured SQLite database, or use `pg_restore` /
   the MySQL client for the provider dump. Do not reuse redacted configuration as a
   secret source.
4. Restore operator-reviewed configuration, supply secrets independently, start the
   same Vega version from `manifest.json`, and run account/group/region/ban checks.

Keep at least one periodic restore drill on the actual deployment host. An archive is
not a proven backup until its checksums and database/world restore have succeeded.
