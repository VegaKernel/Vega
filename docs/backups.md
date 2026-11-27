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
Each archive contains `world/`, `database/`, persisted gameplay files under `data/`, redacted JSON beneath `config/`,
`manifest.json` and `checksums.sha256`. Password, token, secret and connection-string
properties are replaced with `<redacted>`. Retention only deletes matching archive
files whose resolved path remains beneath the fixed `backups/` directory.

Gameplay files include shared sandbox SSC and the whitelist. For the primary deployment
and each sandbox runtime, Vega also copies existing restrictions, build protection,
chest templates, warps, NPC account progress, MOTD and rules. These files contain
player data, including whitelist addresses and character inventories. Each file is
copied from its published version. World save, database snapshot and character
checkpoints can have different timestamps. The sandbox catalog, managed instance
world files and generated world templates are included at their last on-disk save.
Back up externally managed worlds and external template sources separately. When
restoring to a new path, update absolute world paths in the restored sandbox catalog
before starting the supervisor.

## Restore drill

1. Copy the archive away from the live deployment and verify every line in
   `checksums.sha256`.
2. Stop the target Vega process. Restore the `.wld` and optional `.vworld` from
   `world/` to the configured world path.
3. Restore `vega.sqlite3` as the configured SQLite database, or use `pg_restore` /
   the MySQL client for the provider dump. Do not reuse redacted configuration as a
   secret source.
4. Restore the archived `data/` tree at the deployment root with all backend processes
   stopped. Supply fresh transfer/control credentials separately. Let any outstanding
   SSC transfer leases in the snapshot expire before accepting new transfers.
5. Restore operator-reviewed configuration and secrets. Start the same Vega version
   from `manifest.json` and check an inherited and an isolated character, the
   whitelist, restrictions, accounts, groups, regions and bans.

Keep at least one periodic restore drill on the actual deployment host. An archive is
not a proven backup until its checksums and database/world restore have succeeded.
