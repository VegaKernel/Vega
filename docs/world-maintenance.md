# World autosave and cleanup

`config/world-maintenance.json` controls the server autosave schedule and optional
world-entity cleanup. Vega replaces Terraria's fixed dedicated-server timer so there is
only one autosave schedule. The default remains compatible: autosave is enabled every
10 minutes. When world images are enabled, every successful manual or automatic save
also refreshes the `.vworld` cache.

```json
{
  "autoSave": {
    "enabled": true,
    "intervalMinutes": 10
  },
  "cleanup": {
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
}
```

Intervals are clamped to 1–1440 minutes. Item cleanup runs only when the total active
item count reaches its threshold, and then removes only items at least the configured
age. Projectile cleanup runs only at its threshold. Minions and sentries are excluded
by default. Cleanup is disabled by default because removal policy is server-specific.

The recurring decision and all entity-array mutations run on Terraria's game thread.
Filesystem writing for autosave and `.vworld` compilation runs off-thread using stable
save snapshots and atomic image replacement. Errors are recorded under the structured
`world-maintenance` or `world-image` log categories.
