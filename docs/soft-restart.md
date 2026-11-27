# In-process soft restart

Vega can recreate its bundled application modules without unloading Terraria or replacing the server process. The feature is an administrative recovery tool, is disabled by default, and does not make native binaries or arbitrary plugins safely replaceable.

## Boundary

| Lifetime | Components | Behavior |
| --- | --- | --- |
| `PreservedInfrastructure` | Terraria/TerrariaRuntime, loaded world, listener and connected players, game-thread dispatcher, persistence, accounts/sessions, bans, regions, localization, lifecycle, Operations/TUI, world image and backups | Remain alive and keep authoritative state. |
| `RestartableModule` | PVP, CustomItems, AutoWorldCleaner, Mentions, Chests, Wipe and Scenes | Recreated from explicit factories. Packet, chat, chest, command and status calls pass through the active generation router. |
| `ProcessRestartOnly` | native/runtime assemblies, listener/world/storage selection, TerrariaRuntime hooks not owned by a tracked module, failed or uncollected `ServerPlugins` contexts | Require a normal process restart; Vega refuses to report a successful soft restart. |

Module-owned temporary state resets. A module that genuinely needs continuity may implement `IModuleRestartStateParticipant`; the host accepts at most one MiB of explicitly keyed, version-matched state. Authentication, moderation, regions and world state do not use that handoff because they already belong to preserved services.

## Configuration and commands

Edit `config/soft-restart.json`:

```json
{
  "enabled": false,
  "drainTimeoutSeconds": 5,
  "moduleTimeoutSeconds": 10,
  "totalTimeoutSeconds": 60
}
```

After enabling it before process startup, an actor with `vega.server.soft-restart` may run:

```text
softrestart confirm
softrestart Pvp
restartmodules Chests Scenes
modules restart confirm
```

A named target is validated by full or short module name. The bundled modules currently form one restart ownership unit, so Vega recreates its validated dependency closure; the result and audit retain the requested targets. Restart-all always requires the literal `confirm` argument. Concurrent attempts return `Busy`.

Vega first constructs the replacement, closes the callback gate, drains in-flight work, stops the old graph in reverse dependency order, restores compatible handoff state, starts the replacement in topological order and atomically publishes it. If startup returns with an error, Vega attempts rollback once. If a synchronous module is still inside `Start` after cancellation or timeout, Vega keeps the gate closed, reports `Degraded` and defers cleanup until `Start` returns. This outcome and a failed rollback require a process restart.

Progress and the final result are structured `soft-restart` log events, visible in plain output and the TUI log window. `plugin reload`/`plugin unload` remain the only hot path for collectible external plugins. A dirty or incompletely collected `ServerPlugins` context makes soft restart return `ProcessRestartRequired`.

Native `exit`, `stop` and `shutdown` retain Terraria's save-and-exit behavior. Listener port/password, world selection, storage provider, native assembly updates and `.vworld` mode changes require a process restart; replacing the loaded canonical world requires the documented world-reset flow.

Run the lifecycle benchmark with:

```powershell
dotnet run --project benchmarks/Vega.SoftRestart.Benchmarks -c Release
```
