# TShock and TerraZ migration audit

## Purpose

Vega is not a TShock compatibility layer, but TShock and the TerraZ production plugins are authoritative evidence for behavior, security incidents, operator workflows and edge cases. Removing their projects from the shipping solution is valid only after each relevant capability has an explicit disposition and validation.

This ledger prevents physical cleanup from being mistaken for behavioral completion. It is a migration gate, not a request to copy old class shapes, globals, raw packet readers or plugin APIs into Vega.

Status meanings:

- **Native**: a working Vega implementation and focused tests exist.
- **Partial**: a contract or a narrow slice exists, but the production capability is incomplete.
- **Missing**: no equivalent implementation exists yet.
- **Reference**: source is retained for behavior and regression analysis; it is not a runtime dependency.
- **Drop candidate**: behavior may be intentionally omitted, but the reason and replacement/operator impact must be recorded before the row is resolved.

## Reproducible source baseline

The pre-cleanup Vega source is commit `72bff77812ea7e0e41e490a4b9af98d9e078acb0`, the parent of cleanup commit `f24b80368084beeeac0bf42a7c61ea5e7792b191`. Its TSAPI gitlink is `024e480eb3f361a26dda1e4745f55509f30452e7`. The historical `.gitmodules` URL is gone; the identical object is available from `TerraZ-Team/TSAPI` and was verified by checking out that exact gitlink.

The audit worktrees are deliberately outside the Vega solution. A local checkout may be recreated with:

```powershell
git worktree add --detach ..\Vega.LegacyAudit 72bff77812ea7e0e41e490a4b9af98d9e078acb0
git -C ..\Vega.LegacyAudit config submodule.TSAPI.url https://github.com/TerraZ-Team/TSAPI.git
git -C ..\Vega.LegacyAudit submodule update --init external/TSAPI
```

Current TerraZ repositories were freshly read on 2026-08-20. These revisions are the audit baseline; later changes require a deliberate refresh and ledger review.

| Repository | Revision | Branch |
| --- | --- | --- |
| `AutoWorldCleaner` | `017ddedb84cc9b3b902d01fe6f1f7e8538f1650d` | `main` |
| `ChatAboveHeads` | `28603dc98deabda7b245bb04600fc6edf36746d5` | `master` |
| `ChatGuard` | `6542246eac5e26700cb79d842add23a0e07537c5` | `master` |
| `ChestRegen` | `aa2976d8f745d2dcae3f9c0dcb83b8f17ff0d01e` | `main` |
| `Clans` | `2d901e106f64fab1583e3e07d48f98db5e2870e7` | `main` |
| `ClientPlatformDetector` | `4b912f5614445ea1b1fdf453d30977b3630d0033` | `main` |
| `ControlGroupOption` | `829f59068cc25888acfeadaba7bd6aa132fbd92f` | `master` |
| `ExtendedBans` | `9dac5dc8716575335b8c0d23a870be11779bd669` | `main` |
| `FakeProvider` | `2a86a4b8a60bb710fafda3f06724ce13265f7a0d` | `master` |
| `Homes` | `5723474c9d279d47c4fa3cfa9b7e4fec87ca4338` | `main` |
| `HouseRegions` | `f06b5b2413a650e60a832ec489080d1efb706f10` | `master` |
| `InfChests` | `a0393c66df89575baf54f9ef198ac62a47d9790e` | `main` |
| `InvSee` | `1499d34e74334182e7775d3d6c04d9682a1e0123` | `main` |
| `LeashedEntityGuard` | `dc946af6d3a4c9f60a1c506355d5832b42efa431` | `main` |
| `LobbyServer` | `b1800fe3e2980884a34eff025f6e06e5c35e3f0f` | `master` |
| `Mentions` | `7f8e9732ab6e07ca3ffeec4ced6fbdcba8bcebfd` | `master` |
| `MiniLobby` | `d53268f5b85e93255c279463ddb3afe74548ffc6` | `main` |
| `NoCoinDupe` | `14b4da35fd6dc1f7ed81445cac2489fe9d034c37` | `main` |
| `PacketGuard` | `d366713fe902570e7995d5fc1a575bd785005a9a` | `main` |
| `PluginLoader` | `e2ad55782710e1223e760223b7d31a4336caf0b8` | `master` |
| `Region-Vision` | `ef1ef73ad7dc82efefecc8b06866b8a403730fd5` | `master` |
| `SealedDressers` | `aae93ba7b37de3ab883772cd41806d391c0c6447` | `main` |
| `SSCHealthFix` | `23f053e9f724d9a570c5c22f5675206fa94e00f5` | `main` |
| `StateGuard` | `2c3963b3ed14ceb6b6580053d8cf31c443093604` | `main` |
| `TempGroup` | `cbac50701365b9e82f3c5ac482ba527fd8e70a97` | `master` |
| `TimeSync` | `8ccf8c3aabdeda39962b31d227f3871d97c4cb94` | `main` |
| `TpaRequest` | `567acaac34839e4d02269cfcc38c706c5b269103` | `master` |
| `TPSChecker` | `49c2824ca499119a62aae1097e329338d70c1573` | `main` |
| `TSAPI` | `f24c6bfd288ac3192ca6c067943ac5afd6b491dc` | `general-devel` |
| `TSExt_UICommand` | `be7372e844b97c7c4b9e11246c34f15e5b2f33a1` | `main` |
| `TShock` | `9912073f7868b94bcdc7ab658193673647e210db` | `general-devel` |
| `TUI` | `b087671e2737b2d87f0e75b4570cb13121329f7f` | `master` |
| `UuidGuardExtension` | `31b90dc7bd81a1e66741fc22b371a831d5d49054` | `main` |
| `Wipe` | `daf1148430b4d4a7955940866d3b1309b93cb484` | `master` |
| `WorldEdit` | `886a8f6b81fd4b22afee406aeb5c3fdd7e702b16` | `master` |
| `WorldScene` | `d9e15c85cd601e546d5f62afb3f035ce455c5188` | `master` |

## TShock capability ledger

The restored source contains substantially more than a bootstrap bridge. Fresh TerraZ TShock registers 95 built-in command definitions and 42 REST routes. Vega currently owns four built-in command handlers (`serverinfo`, `checkupdates`, `kick` and `playing`/aliases); Terraria console passthrough is not a replacement for player-facing authorization, persistence or help semantics.

| Capability | Legacy evidence | Vega state | Required disposition and validation |
| --- | --- | --- | --- |
| Bootstrap, lifecycle and native console | `TShock.cs`, TSAPI hooks, launcher and CLI parser | **Native** for runtime/readiness and console ownership | Retain native host; audit every still-relevant CLI/config precedence rule in Phase 19. |
| Commands, aliases, help and selectors | `Commands.cs`, `PaginationTools.cs`, 95 registrations | **Partial**: pipeline plus four handlers | Classify every command as built-in, `ServerPlugin`, Terraria passthrough or explicit drop; add authorization and end-to-end tests. |
| Accounts and login | `UserManager`, login/register/logout/password/account commands | **Partial**: contracts only | Persistent identity store, bootstrap-admin flow, authentication and audit in Phase 19. |
| Groups and permissions | `Group`, `GroupManager`, `Permissions`, temporary groups | **Partial**: session authorization and cluster DTOs only | Persistent inheritance/grants/denials, delegated administration and role leases in Phases 15/19. |
| Bans, mutes and whitelist | `BanManager`, `Whitelist`, command and connection checks | **Partial**: ban contract and cluster replication only | Typed identity/CIDR matching, persistence, admission enforcement and moderation commands in Phases 13/19. |
| Regions and build authorization | `RegionManager`, `RegionHandler`, region commands and Bouncer checks | **Partial**: read contract only | World-scoped persistence, priorities/ACLs, game-thread checks and commands in Phase 19. |
| Warps and teleport administration | `WarpsManager`, warp/home/spawn/tp command families | **Missing** except low-level player operation | Decide base commands versus modules; preserve range/section-load and mounted-player edge cases. |
| SSC and character persistence | `CharacterManager`, `PlayerData`, `NetItem`, research and remembered positions | **Missing** | Model login phases and authoritative character state before sandbox transfer; reproduce loadout, CC, slot and disconnect regressions. |
| Database/schema providers | `DbBuilder`, SQL query builders and all domain managers | **Missing** | Default embedded store, migrations, transactional upgrades and explicit external-provider boundary in Phase 19. Do not copy the generic legacy DAL blindly. |
| Server configuration | `TShockConfig`, `ServerSideConfig`, config docs and CLI overrides | **Missing** | Typed `config/` layout, validation, provenance, safe reload and bilingual operator docs in Phase 19. |
| Localization | `I18n`, English resource, PO catalogs and language docs | **Partial**: culture contracts only | Per-recipient catalogs, fallback, `/lang` and module namespaces without process-global culture mutation. |
| Packet/state validation | `GetDataHandlers`, `Bouncer`, specialized handlers | **Partial**: typed boundary, six player-slot ownership rules and two universal authority checks | Extract incident-backed invariants into Phase 13 one at a time; correction remains in `Vega.Terraria`. |
| Item/projectile/tile restrictions | `ItemBans`, `ItemManager`, `ProjectileManager`, `TileManager` | **Missing** | Decide universal security policy versus optional rules, persist accepted restrictions and cover permission exceptions. |
| Save, backup and world lifecycle | `SaveManager`, `BackupManager`, world hooks | **Partial**: Terraria save passthrough and `.vworld` cache | Add controlled backup/reset orchestration, failure recovery and registered persistence participants. |
| REST administration | `Rest/*`, secure tokens and 42 routes | **Missing** | Decide a Vega management API independently of legacy route compatibility; preserve authorization/redaction requirements. |
| GeoIP and connection identity | `GeoIPCountry` and connection handling | **Partial**: MMDB provider exists | Complete forwarded endpoint/provenance, sensitive-field authorization and platform metadata in Phase 13. |
| Plugin/module lifecycle | TSAPI plugin host, `ModuleManager`, plugin installer | **Native**: explicit first-party host plus opt-in unload-safe plugin host | Keep built-ins restart-only; collectible plugins must use tracked cleanup and pass unload verification. |
| Logs, metrics and operational state | text/SQL logs and server-info/status surfaces | **Partial**: structured bounded logs and sampled process/network state | Add tick percentiles, security counters, module state and persistence diagnostics without unbounded hot-path work. |
| Chat and moderation pipeline | chat hooks, mute/whisper/broadcast/MOTD/rules | **Missing** except cluster chat DTOs | Typed ordered transformation/moderation/rendering, commands and per-recipient localization; optional community policy stays modular. |

## TerraZ TShock delta over upstream

The fresh `TerraZ-Team/TShock` branch is not merely an upstream mirror. At pinned revision `9912073f`, from its current upstream merge base `1afaeb51`, it carries 33 commits affecting 18 paths (535 insertions and 387 deletions). The production-relevant delta must be mined into regression tests before its source ceases to be an audit input:

- correct tile-kill and rope-placement boolean/range conditions;
- bounds checks for projectile, NPC, item-frame and post-1.4.5 packet indices/types;
- synchronized, deterministic recent-projectile tracking without unsafe LINQ over mutable lists;
- removal of a background `Task.Run` from projectile cleanup and use of UTC timestamps;
- throttled and staggered stack-hack scans instead of scanning every player every second;
- cheaper squared-distance and cached command-prefix/log-caller hot paths;
- relaxed item pickup validation using vanilla range and both packet/server positions to avoid false rejects;
- SSC login allowed when crowd control was imposed by the SSC authentication gate itself;
- first-login SSC appearance preservation and explicit loadout mapping;
- client chat-tag filtering and newer spectate/item packet registration.

These are behavior/test inputs. Their indentation, static globals, `TSPlayer` fields and TShock hook shapes are not migration targets.

## TerraZ repository dispositions

### Core/runtime invariants

| Sources | Target | Current state |
| --- | --- | --- |
| `ExtendedBans` | Typed moderation identities, normalized IP/CIDR matching and authoritative admission | **Partial**, Phase 13/19 |
| `PacketGuard`, `StateGuard`, `LeashedEntityGuard` | Typed deterministic packet/state rules plus separate telemetry | **Partial**, Phase 13 |
| `NoCoinDupe` | Server-authoritative NPC coin-pickup policy | **Native**, Phase 9/13 |
| `ClientPlatformDetector`, `SSCHealthFix`, `UuidGuardExtension` | Connection metadata, explicit login phases and structured external security signals | **Missing**, Phase 13 |
| `TPSChecker` | Bounded tick-rate telemetry, percentiles, operations warning and `tps` command | **Native**, Phase 14 |
| `TimeSync` | Optional per-world monotonic clock with game-thread correction and explicit-change re-anchoring | **Native**, Phase 14 |
| `PluginLoader` | Optional administrative unload/replace with tracked cleanup, collectible isolation and restart fallback | **Native**, Phase 14 |
| `FakeProvider` | Observer-scoped tile layers, entity visibility and symmetric inbound validation | **Missing**, Phase 16 |

### Administration primitives with module consumers

| Sources | Core capability | User-facing owner |
| --- | --- | --- |
| `TempGroup`, `ControlGroupOption` | Expiring role leases, delegated grants and audit | Administration `ServerPlugin`/UI |
| `InvSee`, `TSExt_UICommand` | Inventory snapshots/mutation and sensitive-field redaction | Administration `ServerPlugin`/UI |
| `Wipe` | Validated world transition/reset and persistence participants | Destructive admin `ServerPlugin`/UI |
| `AutoWorldCleaner` | World scheduler and safe entity snapshot/removal | Maintenance `ServerPlugin` |
| `InfChests`, `ChestRegen` | Stable chest identity, versioned snapshots, transactional mutation and ACL hooks | Chest-policy `ServerPlugins` |
| `Mentions` | Ordered typed chat transformation pipeline | Community `ServerPlugin` |
| `Region-Vision` | Private overlays with matching inbound interaction filtering | Region visualization `ServerPlugin` |

### Optional behavior that must not inflate Core

`Homes`, `HouseRegions`, `TpaRequest`, `Clans`, `SealedDressers`, `ChatAboveHeads` and `ChatGuard` remain `ServerPlugin` candidates. Their required world, region, teleport, identity, chat and scheduler primitives may enter Contracts/Core only alongside a real port. Their commands, schemas, timers and server-specific policy do not become base-server behavior automatically.

### Verified FakeProvider consumers

The fresh source confirms FakeProvider is shared infrastructure rather than a single plugin detail:

- `WorldEdit` uses `TZ.FakeProvider.Abstractions` for personal clipboard and mask previews, ordering, movement/resizing and deterministic session cleanup;
- `Region-Vision` uses personal tile overlays and must reject interaction with phantom border tiles;
- `TUI` supplies personal visual trees through fake tile providers;
- `MiniLobby` composes personal areas with player visibility/isolation layers;
- `WorldScene` and `LobbyServer` carry scene/lobby compositions that must be reviewed before the API is frozen.

WorldEdit remains the first complete Phase 16 consumer because it exercises the abstraction without requiring full multi-world process orchestration.

## Completed migration slices

### NoCoinDupe — replace with a server-authoritative Core invariant

- **Evidence:** TerraZ `NoCoinDupe` revision `14b4da35fd6dc1f7ed81445cac2489fe9d034c37`, `NoCoinDupe.cs`; confirmed TShock incident [`Pryaxis/TShock#2004`](https://github.com/Pryaxis/TShock/issues/2004).
- **Disposition:** `Replace`. Vega rejects every client-originated Multiplicity `SyncExtraValue (92)` packet. Server-originated synchronization remains available.
- **Owner:** `Vega.Core/Networking/NpcCoinPickupAuthorityPolicy`; OTAPI selection remains in `Vega.Terraria` and explicit composition remains in `Vega.Runtime`.
- **Validation:** focused policy tests cover inbound rejection, outbound pass-through, unrelated packets and bounded parsing of a serialized 14-byte Multiplicity payload.
- **Configuration/migration impact:** none. The legacy plugin DLL must not be deployed; the invariant is enabled for every Vega server because accepting a client write to NPC-held coin value is not a gameplay customization point.
- **Boundary check:** no TShock/TSAPI type or runtime dependency is present. `TZ.Multiplicity` remains the only wire-protocol model.

### StateGuard — player-slot ownership slice

- **Evidence:** TerraZ `StateGuard` revision `2c3963b3ed14ceb6b6580053d8cf31c443093604`, `Handlers/SpectateHandler.cs`.
- **Disposition:** `Port to Core` for the packet identity rule only. `PlayerPacketOwnershipPolicy` now rejects a `SpectatePlayer (150)` payload whose typed `PlayerId` differs from the connection slot.
- **Validation:** the existing table-driven ownership suite covers matching and forged spectate identities through `SpectatePlayerView`.
- **Remaining behavior:** disabling spectate, ghost suppression, forced character difficulty, border mutation policy, bypass authorization, corrective synchronization and warning throttling remain unresolved. They require configuration and Terraria correction capabilities rather than more branches in this packet policy.
- **Boundary check:** the rule uses `TZ.Multiplicity`; no StateGuard/TShock types or permission model entered Vega Core.

### TerraZ TShock Bouncer — unsigned PlayerDeathV2 damage

- **Evidence:** TerraZ TShock revision `9912073f7868b94bcdc7ab658193673647e210db`; fix commit `55d57b89f44d2a282e4f6206e0dabcb2a62f1cb7`, `TShockAPI/Bouncer.cs` (`OnKillMe`). The former signed-`short` comparison against `42000` was unreachable for overflowed wire values and the source records their potential to cause server loops.
- **Disposition:** `Port to Core`. `PlayerDeathDamageAuthorityPolicy` interprets `PlayerDeathV2.Damage` as the protocol's unsigned 16-bit bit pattern and rejects values above `42000` before Terraria handles the packet. `PlayerPacketOwnershipPolicy` also rejects a death payload claiming another connection's slot.
- **Owner:** typed validation is in `Vega.Core/Networking`; packet selection and final OTAPI cancellation remain in `Vega.Terraria`; explicit policy composition remains in `Vega.Runtime`.
- **Validation:** serialized Multiplicity payload tests cover the inclusive `42000` boundary, `42001`, `65535` encoded as signed `-1`, outbound synchronization, unrelated packets, matching/forged player slots and the actual inbound protocol adapter.
- **Configuration/migration impact:** none. This is a universal protocol-safety invariant rather than configurable gameplay policy. Structured rejection reasons replace TShock-specific kick text.
- **Boundary check:** no Bouncer, `TSPlayer`, raw reader or TShock event type crossed into Vega. `TZ.Multiplicity` remains the wire model.

### TPSChecker — replace plugin-local globals with Operations telemetry

- **Evidence:** TerraZ `TPSChecker` revision `49c2824ca499119a62aae1097e329338d70c1573`, especially `TickRateMonitor.cs` and `Handlers/TShockCommandsHandler.cs`.
- **Disposition:** `Replace`. `Vega.Terraria` samples the authoritative game-thread tick, resets the observation lifetime at `NetworkReady`, publishes current/rolling-minute/min/max rates with UTC timestamps, and computes bounded p50/p95/p99 tick durations. `Vega.Operations` propagates the immutable data and emits a critical dashboard warning below 15 TPS.
- **User-facing replacement:** `tps` with alias `tpsstats` reports rates, extremes and percentiles through the normal command/localization/authorization boundary. The TUI server and performance windows consume the same snapshot.
- **Intentional drops:** mutable process-global `/tpsalerts` is replaced by always-visible operations warnings; `/tpsreset` is dropped because erasing lifetime extrema destroys diagnostic evidence. A process restart begins a new observation lifetime.
- **Validation/performance:** deterministic-time tests cover rolling retention, extrema, reset semantics and percentile calculation. The tick hot path uses fixed arrays and a bounded queue; percentile sorting reuses one scratch buffer and runs once per sampling interval.
- **Boundary check:** no TShock hooks, players, commands or static plugin state were copied. Terraria owns sampling; Contracts owns the immutable model; Core owns presentation; Terminal UI only reads snapshots.

### PluginLoader — constrained administrative unload and replacement

- **Evidence:** TerraZ `PluginLoader` revision `e2ad55782710e1223e760223b7d31a4336caf0b8`, `PluginsManager.cs` and its create/change/delete/rename watcher paths.
- **Disposition:** `Replace`. Vega keeps explicit first-party composition and adds a separate opt-in host only for assemblies directly exporting one `IHotReloadableVegaModule`. Each image is loaded from memory into its own collectible `AssemblyLoadContext`; replacement is staged before the active revision is stopped.
- **User-facing replacement:** `plugin list`, `plugin reload <name-or-dll>` and `plugin unload <name-or-dll>` use the normal command/permission pipeline. `config/modules.json` disables the entire hot path by default. Operations/TUI expose the mode, dynamic module state, changed files and restart requirement.
- **Unload invariant:** the host first disposes every command, hook or cleanup registered through `IHotModuleContext` in reverse order, preventing new callbacks; it then asks the module to quiesce its own work in `StopAsync`, invokes `DisposeAsync`, requests context unload and verifies collection through a weak reference. Incomplete cleanup is reported as degraded and restart-required, never as successful unload.
- **Intentional boundary:** explicitly composed `IVegaModule` instances such as bundled PVP and CustomItems stay in the default context and require restart. Arbitrary TSAPI/TShock plugins, hidden static hooks and untracked registrations are not declared safe.
- **Validation:** a real separately built plugin integration fixture proves initial load, dynamic command ownership, same-file hot replacement, lease removal, administrative unload and collectible-context release. Disabled-mode and dynamic command-registry tests cover the safety switch and cleanup boundary.

### TimeSync — monotonic world clock without background Terraria access

- **Evidence:** TerraZ `TimeSync` revision `8ccf8c3aabdeda39962b31d227f3871d97c4cb94`, `src/TimeSync.cs`. The legacy loop reads `Main.time`/`Main.dayTime` from `Task.Run` every ten seconds and mutates through `TSPlayer.Server.SetTime`.
- **Disposition:** `Replace`. `MonotonicWorldClock` keeps independent anchors per `WorldId`, advances from `TimeProvider` monotonic timestamps and compares positions on the complete day/night cycle so a boundary crossing is not mistaken for a manual change.
- **Terraria boundary:** `TerrariaWorldClockSynchronizer` observes and corrects only from `Main.OnTickForThirdPartySoftwareOnly`; a correction updates Terraria time on that same thread and broadcasts `WorldData`. No background worker reads or writes Terraria globals.
- **Configuration/operations:** `config/world-clock.json` is disabled by default and validates correction/re-anchor thresholds. Operations/TUI show predicted phase/time, drift, correction count and explicit-change re-anchor count.
- **Validation:** deterministic-time tests cover low-TPS correction, day/night rollover, large explicit changes, independent world anchors and disabled mode.
- **Boundary check:** Core depends only on `WorldId`, immutable clock values and `TimeProvider`; no TShock/TSAPI type or process-global plugin timer entered Vega.

## Closure rules for Phases 9 and 10

For every TShock subsystem and TerraZ repository, the final ledger must record:

1. the pinned source revision and concrete files/incident being used as evidence;
2. one disposition: `Keep native`, `Port to Core`, `Port to ServerPlugin`, `Replace`, or `Drop`;
3. the target project and owning phase;
4. executable validation for every accepted invariant or an explicit operator/product rationale for a drop;
5. configuration, migration and documentation impact;
6. confirmation that no copied TShock/TSAPI type or hidden runtime dependency crossed into Contracts/Core.

Phase 9 closes only after all accepted module behavior has been ported and validated. Phase 10 closes only after this ledger contains no unresolved **Partial**, **Missing** or unclassified entry. The external audit checkouts can then be archived; they never need to return to the production solution.
