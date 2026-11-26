# Offline migration to Vega

Commands here operate on Vega's own deployment data directory and exit without starting the Terraria server. Stop the primary and all sandbox processes first, and retain a complete deployment backup. Use the current server executable from its own directory. TShock source databases are read-only; these tools never update them. Run against a disposable copy first.

## Existing Vega primary SSC

```powershell
.\Vega.Server.exe --vega-migrate-primary-ssc
```

This imports `data/primary-characters.json` into `data/sandboxes/ssc.json`. It preserves existing isolated-world and shared state, never replaces a different shared character, rejects duplicates and active transfers, and assigns imported entries new shared revisions. Byte-identical existing characters are retained. Before publishing, the existing shared store is copied to a unique `.before-primary-migration.*` file; the old primary file is then archived as `primary-characters.json.migrated`. Output gives the counts and backup paths. The copied timestamps and payloads are retained. Source and destination use the same inter-process lock as ordinary store access.

After successful migration, enable `enabled` and `authoritative` in `config/primary-character-persistence.json`. A conflict leaves both original stores unchanged. If publication fails before source archival, retry while still offline: identical imported rows are recognized; conflicting rows require explicit operator reconciliation. To roll back offline before resuming play, restore the archived primary file and previous shared backup together (if the shared store did not exist, restore from the complete deployment backup), then disable authority. Do not restore an old backup over later live progress. Existing source file/archives are intentionally never discarded.

## TShock SQLite active bans

```powershell
.\Vega.Server.exe --vega-import-tshock-bans "D:\TShock\tshock.sqlite"
```

Imports the current `PlayerBans` schema into the configured Vega SQLite database, shared by primary and sandbox worlds. Active `ip:`, `uuid:`, `name:` and `acc:` identifiers, reason, actor, UTC tick timestamps and permanent/temporary expiration are preserved; an absent reason or actor gets an explicit import default. Expired/revoked source rows are not imported. An unsupported identifier or invalid active row aborts before any ban writes. Older `Bans` schemas and plugin-specific identifier types require a separate conversion. A pre-existing destination database is backed up using the SQLite backup API; all active inserts form a single transaction. Stable imported IDs make repeat imports insert-only and preserve existing/revoked Vega records. Stop all worlds before restoring the backup. No account authentication data is imported by this command.

## TShock SQLite warps

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

This imports only the selected native legacy WorldID into the primary Vega world, which must not already contain regions. IDs, rectangle bounds, priority (`Z`), protected flag, owner and allowed accounts/groups are carried over. Account references must resolve by original ID and name; group references must be explicitly mapped. A console-owned `Server` region without a matching account has no account owner. All writes form a single SQLite transaction. Unrecognized references or collisions abort, and a backup of the existing database is retained. Vega's region precedence and protection semantics still require live-world verification (including equal-priority overlaps). Do not treat a character display name as account membership when account sessions are configured.

## Remaining TShock migration requirements

The current pinned TShock SQLite schema is supported for the listed data. Full legacy schema coverage, automatic group/permission translation, external database providers, ancillary plugin/configuration data and real deployment migration remain separate work. Review group permissions rather than copying TShock wildcard grants into Vega. The complete synthetic CLI smoke run imported accounts, SSC, regions, warps and bans, re-ran the ban import without duplicate rows, and verified the source database SHA-256 remained identical.
