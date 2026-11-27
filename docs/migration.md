# Offline migration to Vega

Commands here operate on Vega's own deployment data directory and exit without starting the Terraria server. Stop the TShock source server, the Vega primary and all sandbox processes first, and retain a complete deployment backup. Use the current server executable from its own directory. TShock source databases are read-only; these tools never update them. Run against a disposable copy first. Each command is a separate transaction or atomic file publication, not a single cross-database/file transaction.

## MySQL source and external destinations

Every database-backed TShock converter below accepts either an SQLite file path
or the literal `@mysql` source argument. For MySQL, provide the connection
settings in `VEGA_TSHOCK_MYSQL_CONNECTION`, using a source user with `SELECT`
access and an explicit database name. For example, load a connection string
from a local file with restricted permissions (do not commit it):

```powershell
$env:VEGA_TSHOCK_MYSQL_CONNECTION = (Get-Content -LiteralPath 'D:\Migration\tshock-mysql.connection' -Raw).Trim()
.\Vega.Server.exe --vega-import-tshock-accounts '@mysql' 'D:\Migration\groups.json'
.\Vega.Server.exe --vega-import-tshock-ssc '@mysql' classic
.\Vega.Server.exe --vega-import-tshock-bans '@mysql'
.\Vega.Server.exe --vega-import-tshock-regions '@mysql' 12345678 'D:\Migration\groups.json'
.\Vega.Server.exe --vega-import-tshock-warps '@mysql' 12345678 '00000000-0000-0000-0000-000000000001'
```

Use your actual legacy world ID and native persistent GUID. The MySQL source is
opened with a read-only repeatable-read transaction per converter; stop source
writes across the whole migration so separate commands see the same data.
Connection strings and driver diagnostics are not emitted by the migration CLI.

The **destination** is selected independently by Vega's existing
`config/database.json`: `provider` can be `sqlite`, `mysql` or `postgresql`.
For example, configure `provider: mysql` and supply the destination connection
via the configured `VEGA_DATABASE_CONNECTION_STRING` environment variable.
Create a separate Vega database and review its group mapping first. MySQL
source and target database names must differ even if hosts differ (fail closed
against source/target aliasing). SQLite source and destination files must differ.

Account, ban and region imports run transactionally in the selected relational
provider. The SQLite destination backup remains beside the existing DB; for a
configured external destination, a logical dump (`mysqldump` or `pg_dump`)
must succeed after schema initialization and before imported rows are written.
It is stored under `data/migration-backups/<unique-import-directory>/`.
Install the proper dump tool on PATH and grant sufficient backup rights before
importing; retain a full external DB backup **before schema initialization**
as well. Backups contain authentication data. SSC, warps and whitelist remain
in their normal atomic Vega file stores regardless of relational provider.

To roll back, stop all worlds and restore the appropriate full database and
file-store backups together with the matching deployment version. Never
restore an older backup over newer live progress. SQLite CLI examples below
remain unchanged.

## TShock active bans

```powershell
.\Vega.Server.exe --vega-import-tshock-bans "D:\TShock\tshock.sqlite"
```

Imports the current `PlayerBans` schema into the configured Vega database, shared by primary and sandbox worlds. Active `ip:`, `uuid:`, `name:` and `acc:` identifiers, reason, actor, UTC tick timestamps and permanent/temporary expiration are preserved; an absent reason or actor gets an explicit import default. Expired/revoked source rows are not imported. An unsupported identifier or invalid active row aborts before any ban writes. Older `Bans` schemas and plugin-specific identifier types require a separate conversion. Destination backup follows the provider rules above; all active inserts form a single transaction. Stable imported IDs make repeat imports insert-only and preserve existing/revoked Vega records. Stop all worlds before restoring the backup. No account authentication data is imported by this command.

## TShock warps

```powershell
.\Vega.Server.exe --vega-import-tshock-warps "D:\TShock\tshock.sqlite" 12345678 "00000000-0000-0000-0000-000000000001"
```

Replace the example numeric `WorldID` and GUID with the actual TShock world ID and persistent GUID of the corresponding Vega `.wld` file. Selection is explicit because the old database uses a numeric world ID and Vega uses the native file identity. Imports only that world's rows from `Warps` into a new `data/warps/<guid-N>.json`; an existing destination is not overwritten. Private warps remain hidden from public listing. Coordinates are converted from TShock tile positions to Vega pixel top-left using standard Terraria player geometry (42px height); native edge-clamping and exceptional player geometry require live qualification. Invalid, duplicate or reserved names and out-of-bounds storage values abort the entire import. An unmatched world creates no file. Rollback is removal of the newly imported warp file while offline, before any live changes, or restoration from the deployment backup.

## TShock whitelist

```powershell
.\Vega.Server.exe --vega-import-tshock-whitelist "D:\TShock\whitelist.txt" on
```

Choose `on` or `off` to match the intended TShock `EnableWhitelist` setting. Blank lines and comments are ignored; IP and CIDR entries are parsed before an atomic shared-store import. The destination must be empty and disabled. Invalid entries abort rather than silently weakening an allowlist, and source bytes are unchanged. `whitelist <ip|cidr>` and `whitelist add/del <ip|cidr>` continue to apply to primary and running sandbox admissions through `data/whitelist.json`. Keep its initialization lock marker with backups.

## TShock accounts and group mapping

Create/review the target Vega groups and permissions first. TShock permission strings and wildcard grants do not have automatic one-to-one Vega equivalents. Provide an explicit JSON object mapping every source group in use to a reviewed existing Vega group. For example, if the legacy `member` role should become the ordinary Vega `default` group:

```json
{"member":"default"}
```

```powershell
.\Vega.Server.exe --vega-import-tshock-accounts "D:\TShock\tshock.sqlite" "D:\Migration\groups.json"
```

The import is transactional and requires an empty Vega account table. Numeric IDs are preserved (SSC and region membership depend on them), names are normalized exactly as Vega login names, and last-access timestamps are copied. All groups must be explicitly mapped and exist. The supported BCrypt format from the pinned TShock schema is verified by the standalone persistence layer and opportunistically upgraded to Vega's PBKDF2-SHA256 format after a successful password verification. Hashes are not shown in output. Unsupported encodings or work factors outside 4–14 abort the import; reset those credentials in TShock before exporting. Imported BCrypt password verification accepts up to 72 UTF-8 bytes; accounts with longer legacy passwords should reset them before migration. Vega does not import UUID-based automatic authentication, and ordinary account credentials remain necessary. Backups contain password hashes and should receive the same access protection as the database.

## TShock SSC

```powershell
.\Vega.Server.exe --vega-import-tshock-ssc "D:\TShock\tshock.sqlite" classic
```

Use `journey` instead of `classic` for a Journey world; TShock's character row does not store a difficulty value. Import the accounts first. The source account ID and name must match an actual Vega account. The converter preserves the pinned 350-slot inventory layout (items, equipment, dyes, miscellaneous slots, all four banks, trash, three loadouts, prefixes, favorite flags), life/mana, spawn, appearance, quest count, permanent unlocks, death counters, voice and team. Nullable seeded appearance uses Vega defaults; zero life is normalized to one as in native Vega capture. Older or ambiguous slot layouts are rejected, not automatically padded. Native item application still enforces its own item limits. Shared-state collisions with different payloads abort before publication. Backups and rollback follow the primary SSC procedure above. Actual character restoration on a stock client remains a separate acceptance test.

## TShock regions

```powershell
.\Vega.Server.exe --vega-import-tshock-regions "D:\TShock\tshock.sqlite" 12345678 "D:\Migration\groups.json"
```

This imports only the selected native legacy WorldID into the primary Vega world, which must not already contain regions. IDs, rectangle bounds, priority (`Z`), protected flag, owner and allowed accounts/groups are carried over. Account references must resolve by original ID and name; group references must be explicitly mapped. A console-owned `Server` region without a matching account has no account owner. All writes form a single transaction in the configured Vega provider. Unrecognized references or collisions abort, and a backup of the existing database is retained. Vega's region precedence and protection semantics still require live-world verification (including equal-priority overlaps). Do not treat a character display name as account membership when account sessions are configured.

## Remaining TShock migration requirements

The current pinned TShock SQLite and MySQL schemas are supported for the listed data. Full older schema coverage, automatic group/permission translation, ancillary plugin/configuration data and real deployment migration remain separate work. Review group permissions rather than copying TShock wildcard grants into Vega. Synthetic coverage includes legacy SQLite file fingerprints and independent MySQL source/target provider contracts; the MySQL CLI contract exercises all five converter families and repeat/no-overwrite cases. Actual stock-client SSC restoration remains an acceptance gate.
