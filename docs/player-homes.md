# Personal homes

Vega includes the TerraZ Homes extension in its navigation module. Existing vanilla `/home` (bed/spawn) is unchanged.

| Command | Behavior | Permission |
| --- | --- | --- |
| `/sethome [name]` | Save or update your current position; default name is `home` | `homes.set` |
| `/myhome [name]` | Teleport to your own saved point | `homes.tp` |
| `/myhome -l` | List your saved points | `homes.tp` |
| `/delhome [name]` | Delete your point | `homes.delete` |

The base set permission allows one point. Grant `homes.set.5` in addition to `homes.set` for five points (highest effective numeric permission wins); `homes.set.*` removes the quota. Names are case-insensitive, up to 32 characters. Updating an existing point is possible even at the quota. Commands require authentication and the exact player session. A point belongs to an account and both the logical world ID and the loaded world file GUID. A recreated island/world cannot accidentally use points from an old file. The database enforces uniqueness and serializes quota mutations even with colocated runtimes. The initial release schema works with SQLite, MySQL and PostgreSQL and cascades the removal of points when their account is deleted.

## Existing TShock Homes data

Stop all Vega worlds and import accounts preserving their original IDs first. Then run the offline server command:

```text
--vega-import-tshock-homes <TShock SQLite file|@mysql> <legacy numeric WorldID> <Vega world file GUID> [Vega logical world ID]
```

The primary world is the default logical ID. The physical file GUID is explicit and must match the target world (it is printed by `/worldinfo`; record it before stopping the world). The old Homes extension stored pixel coordinates; they are preserved directly. The import reads the selected legacy world only and rejects any non-empty destination world scope, malformed records, duplicate names or unknown account IDs. No existing points are overwritten. MySQL source selection uses the same offline migration configuration as the other TShock imports.
