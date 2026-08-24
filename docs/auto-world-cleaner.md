# AutoWorldCleaner

`Vega.Modules.AutoWorldCleaner` owns the optional item/projectile cleanup policy. The
module is disabled by default and is configured independently from autosave in
`config/auto-world-cleaner.json`.

```json
{
  "enabled": false,
  "intervalMinutes": 30,
  "onlyWhenPlayersOnline": true,
  "items": {
    "enabled": true,
    "minimumActiveCount": 200,
    "minimumAgeSeconds": 300
  },
  "projectiles": {
    "enabled": true,
    "minimumActiveCount": 700,
    "excludeMinions": true,
    "excludeSentries": true
  }
}
```

`intervalMinutes` is clamped to 1–1440. Item cleanup starts only when the active item
count reaches `minimumActiveCount`; only items at least `minimumAgeSeconds` old become
candidates. Projectile cleanup counts only projectiles eligible under the minion and
sentry exclusions, and runs after that eligible count reaches its threshold.

The scheduler, thresholds and exclusions belong to the module. Terraria access does
not: the module uses `IWorldEntityMaintenance` to request immutable item/projectile
snapshots and guarded removals. A cleanup run is posted to the Terraria game thread and
performs snapshot, candidate selection and removal in one game-thread action. The
Terraria adapter also validates item spawn age and projectile identity before removal,
so a stale handle cannot remove a newly reused slot.

`onlyWhenPlayersOnline` skips scheduled or manual cleanup when no players are online. Cleanup is also skipped until the runtime reaches `WorldReady`, so an aggressive interval cannot touch a partially loaded world. Runs never overlap. Module stop cancels the periodic scheduler. The current Terraria
adapter owns only `WorldId.Primary`; the contract is world-scoped so future sandbox
backends can provide their own implementation without exposing Terraria globals to the
module.

## Commands and observability

`worldclean run` (or `cleanworld run`) requests an immediate run through the same policy
as the scheduler. `worldclean status` reports the latest state. The command requires
`vega.cfg.maintenance`; disabling the module in configuration also disables manual
cleanup rather than creating a hidden policy bypass.

Operations snapshots expose whether the cleaner is enabled, its current/last state,
completion timestamps and removed item/projectile counts. Failures become dashboard
warnings. Structured logs use the `auto-world-cleaner` source.
