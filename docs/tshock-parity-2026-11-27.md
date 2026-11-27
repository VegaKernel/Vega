# Vega / TShock functional parity — 27 November 2026

**Conclusion: not at feature parity yet.** Vega has a broad native server command implementation and a substantially more expressive world/scene/sandbox architecture, but the command subbranches and the ordinary plugin SDK are still significantly narrower in multiple important areas. Native hook availability is not equivalent to a supported plugin event.

## Evidence and scope

- Official TShock release 6.2.1, Terraria 1.4.5.8: local `v6.2.1` source at `b25b3e5bf59870e08175a33df3f8a9c7fc9958c1` and deployed release assemblies. The local TerraZ fork `81175b93` has the same 95 command registration names, aliases, initial permission expressions and console flags. This is not a claim about later upstream revisions.
- Vega `8078b5ba`, native Terraria 1.4.6.1, TerrariaRuntime `1.1.7` / `4f38354`: source and actual final published assemblies. Different game versions make signature and game-behavior parity separate from command/SDK parity.
- [All 95 command registrations and live console descriptors](tshock-command-parity-2026-11-27.md). Native disposable Vega startup plus `help`, `aliases`, and `help <original-name>` was performed; actual destructive/admin operation effects and joined-client UX were not exercised. Player-only command registrations were inspected in source. Existing [command](server-command-audit.md) and [packet](server-behavior-audit.md) ledgers remain relevant.
- Official TSAPI `HookManager` and TShock `GetDataHandlers`/`Hooks` metadata were inspected directly. Matching native method candidates, ordinary SDK subscription capabilities, and internal policies are distinguished below; a generated hook is not a proof of equivalent timing or cancellation.

## 1. Commands, aliases and actual subcommands

Of 95 original command registrations, 93 have a Vega name or operator alternative (including conditional `rocket` and alternative `setup`), and two are deliberate omissions (`su`, `sudo`). All 27 original alias names map to their intended commands. Native console discovery returned 86 Vega canonical commands, 38 aliases and 70 of the 95 TShock names; 23 original names are player-only in Vega. TShock permits console `me`, `reply`, `whisper`, which Vega currently does not. Permissions use Vega's own identifiers, not the TShock permission namespace; migration maps roles explicitly.

**Subcommand comparison** (actual handlers, not just displayed help): `= ` close feature correspondence, `~` partial/different semantics, `–` missing feature or syntax.

| Area | TShock branch | Vega equivalent | Assessment / concrete gap |
|---|---|---|---|
| `user` | `add`, `del`, `password`, `group`, `help` | `account`/`user` `create`, `delete`, `password`, `group`, plus list/show/temp | ~ Add/delete operations exist, but original `add`/`del` aliases and `user help` do not. Preserve delegation checks. |
| accounts | `login`, `register`, `logout`, `password`, `accountinfo`, `tempgroup`, `setup` | Native SSC-backed equivalents, account CRUD and temporary leases | ~ `setup` console alternative, not original in-game token; UUID/no-password login/config and tempgroup optional-duration semantics not identical. Stock client verification pending. |
| `group` | `add`, `del`, `parent`, `list`, `listperm`, `addperm`, `delperm`, `help` | `create`, `delete`, `parent`, `list`, `show`, `grant`, `deny`, `revoke` | ~ Original command syntax, multiple permissions and wildcard/atomic batch semantics missing. `show` is the read alternative to listperm. |
| `group` | `rename`, `prefix`, `suffix`, `color` | No corresponding group fields or command actions in `GroupSnapshot`/`GroupCommand` | – Need persisted group metadata, rename transaction across references, chat rendering, permissions and tests. |
| `region` | `set 1/2`, `define`, `name`, `clear` | Explicit rectangular `create`, `list`, `show` | ~ Coordinates are supported; interactive selection and selection inspection/clear absent. |
| `region` | `protect`, `allow/remove`, `allowg/removeg`, `z`, `resize`, `expand` + directions | `flag`, `member/group add/remove`, `priority`, absolute `resize` | ~ Most domain operations exist, but legacy syntax and resize/expand directional semantics differ; protect must map to actual region flags, not blindly alias. |
| `region` | `delete`, `list`, `info`, `rename`, `tp`, `help` | `delete`, `list`, `show` | ~ Rename and region-center teleport missing; no explicit legacy help branch. |
| `ban` | `add`, `del`, `list`, `details`, `identifiers`, `examples`, `help` | Persistent shared ban `add` typed subject, `list`, `show`, `unban` | ~ Core admission and revocation covered; original identifier-selection syntax, details/remove surfaces and examples/help diverge. |
| `mute` / `unmute` | Session mute/unmute | Persistent timed and subject-bound mute, list/show/unmute | ~ Broader persistence, but legacy argument/default/toggle semantics need adapter and native UX tests. |
| `itemban`, `projban`, `tileban` | `add/del` by name or ID, `allow/disallow` per group, `list/help` | Numeric `add`, `del`, `list`, general bypass permission | – Per-item/per-projectile/per-tile group exception is not equivalent to blanket bypass. Need durable per-content exceptions and named resolution. |
| `warp` | `add`, `del`, `hide`, `list`, `send`, name teleport | All six branches in `NavigationModule.WarpAsync` | = Structural match; compare hidden warp visibility, target resolution, coordinates, permissions and native join behavior before claiming complete match. |
| `tp`, `tphere`, `tpnpc`, `tppos`, `home`, `spawn`, `tpallow`, `pos` | Self/other teleport, wildcard and targeting branches | Source/destination, unique target, NPC slot/name, position, preference | ~ `*` mass teleport branches missing; explicit safe coordinate bounds and session binding differ. |
| `worldevent` | `meteor`, `fullmoon`, `bloodmoon`, `eclipse`, `invade`, `rain`, `sandstorm`, `lanternsnight/lanterns`, `meteorshower` | `rain`, `bloodmoon`, `eclipse`, `fullmoon`, `slimerain`, `sandstorm`, moon events and four named invasions | ~ Missing meteor, lantern night, meteor shower, coin rain; original `invade`/`rain` nested grammar and spaced aliases do not map directly. Existing native world event predicates must remain authoritative. |
| `grow` | 22 tree/plant types and help (incl. cactus, herb) | 20 tree/gem/mushroom types and help | ~ `cactus` and `herb` missing. Native compatible ground requirement differs. |
| `spawnboss` | `all/*`, named bosses, event enemies and pillars | ID/name + bounded count, 12 groups of shorthand aliases, native boss catalog filter | ~ `all/*` absent; named event foes/pillars outside `Boss` filter are not covered; exact native summon semantics and location rules differ. |
| `spawnmob`, `butcher`, `renamenpc` | Native spawn, selector and combat/name variants | Bounded spawn, filtered combat and town NPC rename | ~ Review maximum count, selector/name ambiguity and boss/town protections separately; don't remove intentional bounds solely for compatibility. |
| `item`, `give` | Name/ID, amount and prefix ID/name | Name/ID, positive amount, numeric prefix only | ~ Named prefix missing; quantity defaults, max stacks and recipient branches are not exact. |
| `clear` | `item/items/i`, `npc/npcs/n`, `proj/projectile/projectiles/p`, optional original radius | Long names + explicit `all` scope | ~ Short aliases and original distance/scope behavior need reconciliation. |
| `firework` | Eight types and short aliases | Same eight types and short aliases in actual handler | = Selector match, real client visual qualification not done. |
| `time` | `day`, `night`, `noon`, `midnight`, explicit time | Same named modes and `HH:mm` | = Review time parsing and native world clock synchronization. |
| `whitelist` | Single IP/CIDR add | Legacy single address accepted; list/on/off/add/del | = Feature covered with additional controls and shared admission across worlds. |
| `rest` | `listusers`, `destroytokens` | Both plus `status`; typed admin endpoints | = Command branch names covered; HTTP paths/auth contracts are intentionally different. |
| `me`, `whisper`, `reply`, `party`, `wallow` | Player chat and console subsets | All registered player-only; same public aliases | ~ Console me/whisper/reply omitted; formatting, session reply history and toggle behavior differ. |
| `overridessc`, `savessc`, `uploadssc` | SSC operations | Exact authenticated SSC binding, guarded confirmation | ~ Real stock client join/restore, reconnect and transfer still pending, despite native tests. |
| `help`, `aliases`, `displaylogs` | Paginated help/alias query, log subscription | Catalog info, all aliases, bounded recent log query | ~ Help/alias grammar, pagination and continuous log subscription not equivalent. |
| `su`, `sudo` | Legacy substitution | No direct command | Deliberate omission. Preserve explicit role/delegation model; don't reintroduce unrestricted identity or permission bypass merely for spelling parity. |

The remaining single-action commands have catalog entries in the 95-row inventory; they are not automatically certified for all native outcomes, configuration combinations, messages, wildcard targets, or original permissions. Prioritize the concrete differences above rather than mass-adding aliases without matching semantics.

## 2. Hooks: native engine versus public plugin surface

Metadata count in the actual binaries:

| Surface | Count | Interpretation |
|---|---:|---|
| TSAPI `HookManager` collections | 48 | Published native/server events |
| TShock `AccountHooks`, `GeneralHooks`, `PlayerHooks`, `RegionHooks` events | 21 | Domain/lifecycle events |
| TShock `GetDataHandlers` typed handler lists | 53 | Public gameplay-message callbacks |
| TerrariaRuntime explicit `Hooks` events | 38 | Special native boundaries (also internal Vega uses) |
| TerrariaRuntime generated `HookEvents` event definitions | 2,889 | Native method hooks; signature/timing must be inspected |
| TerrariaRuntime.Hooks `On.*` / `IL.*` | 26,390 each | Generated method and IL detour definitions, not 52,780 distinct gameplay capabilities |
| Vega ordinary player notifications | 4 | `OnJoined`, `OnLeft`, `OnEnteredWorld`, `OnClosing` |
| Vega ordinary world notifications and policies | 11 + 2 | Six generic NPC/projectile notifications, five actor-specific, two NPC veto policies |

The raw hook substrate is broad. The supported, lifecycle-owned Vega SDK event surface is **not** equally broad. `IHotModuleContext` only exposes command registration/registration tracking/cleanup, so changing plugin entry point does not automatically expose host contracts. Debug diagnostics are not an ordinary gameplay plugin API. A built-in policy does not make a plugin hook available.

### 2.1 All 48 TSAPI collections grouped by capability

**Public** means an ordinary SDK subscription exists, **partial** means a related but narrower/different-stage event or task, **internal** means a native candidate/core use but no ordinary SDK subscription, and **verify** means native mapping needs exact method/timing review. Raw generated event existence alone is not counted as a match.

| TSAPI collection(s) | TerrariaRuntime / Vega native boundary | Ordinary Vega SDK |
|---|---|---|
| `GameUpdate`, `GamePostUpdate` | Dedicated tick completed + generated `Main.Update` | Partial: `Tasks.Every`/`Post` are scheduling, not pre/post native tick events |
| `GameInitialize`, `GamePostInitialize` | Dedicated world ready/lifecycle; generated initialize methods | Partial: plugin activation, no native startup/world-ready subscription |
| `GameWorldConnect`, `GameWorldDisconnect` | Server world lifecycle | Internal; no explicit world connect/disconnect events |
| `GameHardmodeTileUpdate` | `Hooks.WorldGen.HardmodeTileUpdate` | Internal |
| `GameStatueSpawn` | Statue/mechanical spawn candidates, `Hooks.NPC.MechSpawn`, `Hooks.Item.MechSpawn` | Verify; no ordinary event |
| `ItemSetDefaultsInt`, `ItemSetDefaultsString`, `ItemNetDefaults` | Generated `Item.SetDefaults*`, `netDefaults` (older overloads need version review) | Internal; no item default callback |
| `ItemForceIntoChest` | `Hooks.Chest.QuickStack` related, exact scope needs review | Verify; virtual chest transfer is a different feature |
| `NetSendData`, `NetSendNetData`, `NetSendBytes`, `NetGetData` | `Hooks.NetMessage.SendData`, byte send hooks, `Hooks.MessageBuffer.GetData`; module-specific send mapping needs review | Internal; no ordinary generic network processing subscription |
| `NetGreetPlayer` | `Hooks.Netplay.ServerPlayerEnteringWorld` | Partial: `Players.OnEnteredWorld`, exact greeting timing distinct |
| `NetNameCollision` | `Hooks.MessageBuffer.NameCollision` | Internal |
| `NpcKilled` | `Hooks.NPC.Killed` in `NativeGameplayEventHooks` | Public: `OnNpcKilled` |
| `NpcSetDefaultsInt`, `NpcSetDefaultsString`, `NpcNetDefaults` | Generated NPC default methods (signature/version review) | Internal |
| `NpcStrike` | Native strike candidate | No general damage/strike SDK event |
| `NpcTransform` | `Hooks.NPC.Transforming` | Public: `OnNpcTransforming`, `AllowNpcTransform`; mutation semantics narrower |
| `NpcSpawn` | `Hooks.NPC.Spawn` and `.Create` | Partial: `OnNpcCreating`; not identical spawn veto/parameter mutation |
| `NpcLootDrop` | `Hooks.NPC.DropLoot` | Public: notification and `AllowNpcLoot`; no general mutable loot callback |
| `NpcTriggerPressurePlate`, `PlayerTriggerPressurePlate`, `ProjectileTriggerPressurePlate` | `Hooks.Collision.PressurePlate` | Internal |
| `DropBossBag` | `Hooks.NPC.BossBag` | Internal |
| `NpcAIUpdate`, `ProjectileAIUpdate` | Generated native AI methods | Internal; custom `OnNpcActorThink` is not generic native AI |
| `PlayerUpdatePhysics` | Generated Player update methods; exact physics boundary needs review | Verify; no public equivalent |
| `ProjectileSetDefaults` | Generated `Projectile.SetDefaults` | Internal |
| `ServerCommand` | `Hooks.Main.CommandProcess`; core command registry | Partial: register own commands, no global command pre/post observer |
| `ServerConnect` | `Hooks.Netplay.ServerConnectionActivated` and admission core | Partial: join notification, no generic pre-admission decision |
| `ServerJoin`, `ServerLeave`, `ServerSocketReset` | connection lifecycle and exact pre-reset boundary | Public related: `OnJoined`, `OnLeft`, `OnClosing`; ordering/semantics need stock-client qualification |
| `ServerChat`, `ServerBroadcast` | Native command/chat and messaging core | Internal; plugin can message, not intercept general chat/broadcast |
| `WorldSave` | Dedicated save completed + generated `WorldFile.SaveWorld` | Internal; no public pre/post save hook |
| `WorldStartHardMode` | Generated `WorldGen.StartHardmode` and core decision | Internal |
| `WorldMeteorDrop` | `Hooks.WorldGen.Meteor` | Internal |
| `WorldChristmasCheck`, `WorldHalloweenCheck` | Generated `Main.checkXMas`/`checkHalloween` | Internal |
| `WorldGrassSpread` | Generated `WorldGen.SpreadGrass` | Internal |
| `WireTriggerAnnouncementBox` | `Hooks.Wiring.AnnouncementBox` | Internal |

### 2.2 All 21 TShock domain hooks

| Original event(s) | Vega state / missing SDK capability |
|---|---|
| `AccountCreate`, `AccountDelete`, `AccountGroupUpdate` | Account/authorization service operations exist; no public account lifecycle subscription |
| `ReloadEvent` | Plugin configuration reload and own activation lifecycle exist, but no global configuration reload event |
| `PlayerPreLogin`, `PlayerPostLogin`, `PlayerLogout` | Internal `PlayerAccountSessionService.BindingClosing/Established/Ended`; public join/leave is not authentication. Need exact auth-tenure notifications and separately governed pre-login policy. |
| `PlayerCommand`, `PrePlayerCommand`, `PostPlayerCommand` | Command registration and executor exist; no global supported hooks |
| `PlayerChat` | Internal chat pipeline/renderer; no general SDK chat notification or policy |
| `PlayerPermission`, `PlayerItembanPermission`, `PlayerProjbanPermission`, `PlayerTilebanPermission`, `PlayerHasBuildPermission` | Core authorization/content/region policies exist; no ordinary plugin permission-policy API. Preserve authoritative enforcement and explicit permission ownership. |
| `RegionEntered`, `RegionLeft`, `RegionCreated`, `RegionDeleted`, `RegionRenamed` | Internal region state and service. No public region service/events; rename also missing in command model. |

### 2.3 All 53 TShock typed gameplay handler lists

Grouped by domain, distinct names are retained. These are normal gameplay callback families, not a request to expose arbitrary raw protocol mutation. In Vega the built-in policy pipeline can handle many corresponding normal actions, but that is **not SDK parity**.

| TShock handler list names | Vega public surface / gap |
|---|---|
| `PlayerInfo`, `PlayerSlot`, `GetSection`, `PlayerUpdate`, `PlayerHP`, `PlayerSpawn`, `PlayerZone`, `PlayerAnimation`, `PlayerMana`, `PlayerTeam`, `TogglePvp` | Player snapshot/inventory APIs; no generic typed gameplay subscriptions |
| `TileEdit`, `DoorUse`, `SendTileRect`, `PlaceObject`, `PlaceTileEntity`, `LiquidSet`, `PaintTile`, `PaintWall`, `GemLockToggle`, `MassWireOperation`, `SyncTilePicking` | Core policies and world tile API; no general plugin-visible validated build events |
| `ItemDrop`, `ChestItemChange`, `ChestOpen`, `PlaceChest` | Partial: managed virtual chest transactions, canonical chest capture/replace; no canonical player chest/item lifecycle notification family |
| `NewProjectile`, `ProjectileKill` | Partial: `OnProjectileSynchronized`, `OnProjectileRemoved` are accepted/native notifications, not arbitrary pre-handler substitutions |
| `NPCStrike`, `NpcTalk`, `NPCSpecial`, `NPCAddBuff`, `NPCHome`, `ReleaseNPC`, `FishOutNPC` | Partial: actor-specific interactions and NPC spawn/teleport/despawn; no general native player interaction, strike, buff/home events |
| `SignRead`, `Sign` | Partial: virtual signs read/edit request and acceptance; no canonical sign event family |
| `PlaceItemFrame`, `DisplayDollItemSync`, `DisplayDollPoseSync`, `RequestTileEntityInteraction`, `FoodPlatterTryPlacing`, `DisplayJarTryPlacing` | Partial: virtual frame/rack managed deposit and read-only display projections; not general native event family |
| `PlayerBuffUpdate`, `PlayerBuff`, `PlayerDamage`, `KillMe`, `HealOtherPlayer`, `Teleport`, `PortalTeleport`, `Emoji`, `LandGolfBallInCup`, `ReadNetModule` | No corresponding general typed plugin subscriptions; selected operation commands and native internal policies are separate |

## 3. Plugin SDK capability comparison

The official `TSPlayer` declares 69 public non-accessor methods (overloads counted). Vega's ordinary SDK intentionally uses narrower operation interfaces and immutable session/world snapshots. Counting methods alone is not useful; assess whether a plugin can perform the required operation without depending on internal implementations.

| Plugin requirement | TShock | Vega public SDK at this revision |
|---|---|---|
| Commands | `Commands.ChatCommands`, `HandleCommand`, aliases, permission, global command hooks | Register own typed command with aliases/permission, guarded lifetime; **missing** public catalog/execute and global command lifecycle hooks |
| Player lookup, chat response, kick, navigation | `TSPlayer`, then engine operations | `IPluginPlayers` online, exact lookup, message/kick/teleport; strong snapshot/session checks; broader audience/broadcast helpers and chat hooks absent |
| Player state and actions | `TSPlayer` heal/damage/kill/buff/spawn/team/PvP, native player access | **Gap:** a number of these exist in built-in command/native services but not `IPluginPlayers`; SDK inventory exposes capture/replace one slot, not a general grant/buff/vital/team operation surface |
| Player SSC/account | Account manager, auth hooks, `SaveServerCharacter`, `SendServerCharacter` | **Gap:** internal SSC coordinator and account service, no public account/auth/SSC facet; actor-specific quest transactions are not a general substitute |
| Player-owned metadata | `SetData`, `GetData`, `RemoveData`, lifecycle | Plugin-owned storage/leases/tasks; no dedicated exact-session metadata facet. Plugin can maintain its own scoped state. |
| Groups/permissions | Group manager, `HasPermission`, permission hooks | **Gap:** no ordinary authorization read/mutation/permission-event facet. Command principal permission checks exist. No group presentation/rename fields. |
| Regions/warps/bans/mutes | Public managers and events | **Gap:** internal Vega services and REST/commands but absent ordinary SDK domain facets. REST admin API is separate, not plugin SDK. |
| Network/gameplay callbacks | Native send, receive and typed data handler hooks | **Gap:** ordinary typed validated gameplay notifications/policies absent except listed NPC/projectile/managed scene cases. Debug trace is not equivalent. Keep integrity/authorization policies authoritative. |
| NPCs and combat | Engine `Main.npc`, native hooks, TSPlayer helpers | `World.Npcs` snapshot/spawn/teleport/despawn; advanced actor shops/dialog/quests/wallet/patrol and lifetime ownership. **Gap:** generic damage/buff/AI/vitals/inventory operations and events. |
| Projectiles/world items | Engine entities, native APIs, send/remove helpers | **Gap:** projectile sync/removal notifications exist, but no public generic projectile spawn/update/remove or dropped-item service facet. Scene visibility assignment does not create or manage gameplay entities. |
| Tiles/containers/environment | Mutable native tile/entity APIs, managers | `World.Tiles` detached capture/revision replace, `Chests` capture/replace, `Environment` capture/patch. Strong public domain alternative; canonical sign/entity/dropped-item CRUD and native lifecycle events still incomplete. |
| Native boundary access | TSAPI/OTAPI/raw Terraria objects | TerrariaRuntime custom and generated hooks in host, but ordinary SDK deliberately avoids native mutable object references. Keep supported, bounded operation APIs separate from advanced native extension points. |
| Scenes and isolation | Primarily third-party plugins | Vega `World.Scenes`, `FakeProvider`, object projections/managed GUI, visibility layers, schematic capture/place, independent sandbox lifecycle; substantial additional built-in capability. |
| Plugin lifecycle/concurrency | Plugin initialize/dispose, hooks, timers; ownership generally manual | Guarded callbacks, scoped cleanup, tasks, storage/config, hot reload, retirement/fault isolation. Strong Vega design; stable compatibility/API baseline still needed. |
| Persistence | Public DB managers and plugin-controlled DAL | Plugin-owned JSON/config, internal host persistence; no general host account/group/region queries through SDK, no dynamic external NPC economy registration. |

## 4. Prioritized implementation plan (not completed features)

| Priority | Work | Acceptance |
|---|---|---|
| P0 | Command semantics: group/region/restrictions, account/ban and navigation compatibility | Add missing domain capabilities first, then compatible aliases/arguments; each branch has a test for permissions, state mutation and ordinary outcomes. Keep intentional operator/authority safeguards. |
| P0 | Public player/session/auth, command/chat lifecycle, region/permission read and event facets | Explicit world/session ownership, plugin lifetime removal, documented event timing, safe read-only notifications first; test actual native adapters and reload. |
| P0 | Typed ordinary gameplay hooks for player movement/spawn/damage, NPC strike, tile/object edits, item/container interactions | Immutable validated event records, appropriate bounded decisions attached to real authoritative stages; ordinary gameplay tests, no raw protocol bypass. |
| P1 | Public player vital/item/team operations, account/group/region/warp/ban service facets, projectile & dropped-item CRUD | Use existing domain services; no direct host DB/global mutable arrays; exact session/world validation and bounded mutation. |
| P1 | World lifecycle/tick/save, native AI/default/pressure-plate/environment events | Review native signatures against pinned Terraria 1.4.6.1, expose only semantically meaningful boundaries; benchmark zero-subscriber path and guarded dispatch. |
| P1 | Remaining original command branches: worldevent/grow/boss selectors and console chat, help/logging UX | Test each selector and permission separately, compare stock client behavior; keep explicit bounds and confirmation where needed. |
| P1 | Stable SDK compatibility baseline and plugin migration examples | Independent plugin builds, version/generation preflight, reload tests, representative real plugins; document unmapped APIs. |

Do not treat the 93/95 names or the generated native hook count as a TShock replacement claim. Command grammar, supported plugin operations/event stages, and real-client behavioral qualification are independent release gates.

## Implementation follow-up (after the pinned audit)

The audit above remains the baseline at `8078b5ba`; the following changes close
specific identified gaps without changing the native ownership model:

- Account `add`/`del`/`help`, ban `details`/`del`/`help`/`identifiers`/`examples`
  reuse the existing durable account/ban services. Ban IDs remain Vega GUIDs.
- Group `add`, `del`, `listperm`, `help`, `addperm`/`delperm` (single permission)
  use the existing delegation and revision model. Persisted `prefix`, `suffix`,
  `color` use schema v2 and the effective group including temporary roles.
  Group rename and atomic multi-permission operations remain open.
- Region aliases, `rename`, direction-based expansion/resize, pagination,
  `tp` and exact-session two-point selection/`define` are implemented. Selection
  uses player positions or explicit coordinates, not the legacy hit selection;
  `info -d` graphical showing of boundaries remains open.
- Content restriction commands resolve unique native symbolic names (including
  whitespace/case normalization), `help`, and `allow`/`disallow` per content/group.
  These are durable exact group grants/denies, not a second exception database;
  a manager must also hold group-management and the exact delegated permission.
  They follow normal inheritance/denial and blanket bypass semantics. Removing
  a content restriction does not silently mutate group permissions; removing or
  denying an allowance is a distinct operation. Name localization/ambiguous
  partial name matching is not claimed.
- `item`/`give` accept unique native symbolic prefix names; existing native
  stack/type/prefix validation still applies. `clear` supports short selectors.
- `worldevent` maps original invasion/rain grammar, spaced moons, meteor,
  lanterns, meteor shower and coin rain through native world operations.
  `meteor` is one-shot, lanterns represent manual control, and native refusal is
  reported as a failure. `grow cactus` requires existing sand and clear space,
  `grow herb` an existing immature herb; no forced terrain replacement.
  `clearangler [player-name]` supports the native completion list.
- `context.Activity` adds exact authentication lifecycle, accepted public chat
  and command-completion metadata; no credential or argument exposure. Regions
  expose immutable current-world reads; no invented region events.
- `context.Players.Actions` exposes the existing typed native heal/buff/kill/
  slap/respawn/give operations, with exact session/world ownership and tracked
  lifetime cancellation. These do not expose mutable native objects or packets.
- `WorldEvents.OnTickCompleted` and `OnSaveCompleted` expose native completed
  tick/save boundaries; asynchronous save completions marshal to the owning
  game thread on a best-effort basis. They are observations, not veto points;
  no-subscriber paths avoid publishing records. World-ready/stopping timing
  still needs explicit qualification.

See the corresponding command, SDK and persistence tests. Real graphical
client qualification, group rename, wildcard/bulk authority operations,
canonical typed gameplay events and stable SDK compatibility remain separate.

Integrated Windows Release verification: 893 tests passed (two external
database contracts skipped locally), including a discovered retirement
cancellation race fixed and repeated ten times in isolation. Native deployment
smoke passed with `-TestParityCommands`, `-TestSandboxIsolation` and
`-TestProducerBatching`: group style, region rename/expand, content-specific
allowance, lantern night, meteor shower and coin rain commands were executed
on a disposable native world. The 255-peer/600-native-update regression with
producer batching delivered 8,273,395 callbacks and verified every stream
hash in 9.885 s (60.70 updates/s, busy p95 20.10 ms, 213/600 overruns).
This is consistent with the earlier same-binary candidate range, not a new
60-TPS stability guarantee or a benchmark of subscribed plugin callbacks.
Raw logs: `artifacts/parity-implementation-2026-11-27`.
