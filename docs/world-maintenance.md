# World autosave

`config/world-maintenance.json` controls Vega's dedicated-server autosave schedule. Vega
replaces Terraria's fixed timer so only one autosave schedule is active. The default
remains compatible: autosave is enabled every 10 minutes.

```json
{
  "autoSave": {
    "enabled": true,
    "intervalMinutes": 10
  }
}
```

The interval is clamped to 1–1440 minutes. Mutable Terraria world state is prepared on
the game thread before filesystem work moves to a worker. When world images are enabled,
every successful manual or automatic save also refreshes the `.vworld` cache through the
coalesced atomic image pipeline.

Authorized operators use `save` for the same coalesced save barrier; a concurrent
autosave/manual save is joined rather than duplicated. `shutdown [reason]` (aliases
`stop` and `off`) requests Terraria's normal server-loop exit with
`SaveOnServerExit=true`; it never kills the Vega process directly. Both commands require
`vega.cfg.maintenance`.

Item/projectile cleanup is deliberately not part of world maintenance configuration.
Its policy and scheduler belong to the optional `Vega.Modules.AutoWorldCleaner` module;
see [the AutoWorldCleaner guide](auto-world-cleaner.md) and
`config/auto-world-cleaner.json`.

Autosave errors use the structured `world-maintenance` log source. World-image
publication uses `world-image`.
