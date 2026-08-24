# Vega server behavior audit

This ledger is the durable Phase 25 source-of-truth for behavior compatibility work. It records executable server behavior, not API or source compatibility with TShock.

## Pinned evidence

- TerraZ reference: `TerraZ-Team/TShock` `general-devel@81175b934497f9ef0ee5b5200334e4260b0cc06c`.
- TerraZ packet registry: `TShockAPI/GetDataHandlers.cs`, 71 registrations.
- TerraZ command registry: 95 built-in `Command` registrations; command-level rows remain an open Phase 25 workstream.
- Terraria server binary: Terraria 1.4.5.8 Windows dedicated server, `26,028,032` bytes, SHA-256 `D87E3FAF08637F6BE8882C63E7F11FB7E792B0230006309618473ECE0F863E1E`.
- Protocol baseline: `326`.
- Readable comparison source: `JonataOliveiraa/Terraria1.4.5` `main@0459d7b61e35f15354a40fb9d1422d84e9bb4f2c`; any 1.4.5.7/1.4.5.8 conflict is resolved against the pinned 1.4.5.8 binary/protocol behavior.

## Classification rules

Every audited behavior is classified before Vega policy is chosen:

- `Vanilla semantic`: required by the Terraria protocol/world state machine.
- `TShock hardening`: security or operator behavior supplied by TShock and evaluated independently.
- `TerraZ delta`: behavior added or changed by the pinned TerraZ fork.
- `Optional policy`: gameplay/community behavior that must not silently become a server invariant.

Vega decisions are `Keep`, `Replace`, `Module`, `Native passthrough`, `Drop`, or `Unresolved` while evidence is incomplete. `Unresolved` is intentionally visible and may not be collapsed into a broad capability claim.

## Packet registration ledger

All rows below are pinned to `81175b934497f9ef0ee5b5200334e4260b0cc06c:TShockAPI/GetDataHandlers.cs`. The initial ledger preserves every registration as an explicit unresolved row. Semantic review must replace the placeholder invariant/classification and resolve the decision with executable evidence.

| ID | Source revision | Packet registration | Handler | Classification | Branch / invariant | Vega decision | Owner | Status | Executable evidence |
|---|---|---|---|---|---|---|---|---|---|
| packet-001 | `81175b9` | `PlayerInfo` | `HandlePlayerInfo` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-002 | `81175b9` | `PlayerSlot` | `HandlePlayerSlot` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-003 | `81175b9` | `ContinueConnecting2` | `HandleConnecting` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-004 | `81175b9` | `TileGetSection` | `HandleGetSection` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-005 | `81175b9` | `PlayerSpawn` | `HandleSpawn` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-006 | `81175b9` | `PlayerUpdate` | `HandlePlayerUpdate` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-007 | `81175b9` | `PlayerHp` | `HandlePlayerHp` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-008 | `81175b9` | `Tile` | `HandleTile` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-009 | `81175b9` | `DoorUse` | `HandleDoorUse` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-010 | `81175b9` | `TileSendSquare` | `HandleSendTileRect` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-011 | `81175b9` | `ItemDrop` | `HandleItemDrop` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-012 | `81175b9` | `NpcItemStrike` | `HandleNpcItemStrike` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-013 | `81175b9` | `ProjectileNew` | `HandleProjectileNew` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-014 | `81175b9` | `NpcStrike` | `HandleNpcStrike` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-015 | `81175b9` | `ProjectileDestroy` | `HandleProjectileKill` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-016 | `81175b9` | `TogglePvp` | `HandleTogglePvp` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-017 | `81175b9` | `ChestGetContents` | `HandleChestOpen` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-018 | `81175b9` | `ChestItem` | `HandleChestItem` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-019 | `81175b9` | `ChestOpen` | `HandleChestActive` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-020 | `81175b9` | `PlaceChest` | `HandlePlaceChest` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-021 | `81175b9` | `Zones` | `HandlePlayerZone` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-022 | `81175b9` | `PasswordSend` | `HandlePassword` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-023 | `81175b9` | `NpcTalk` | `HandleNpcTalk` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-024 | `81175b9` | `PlayerAnimation` | `HandlePlayerAnimation` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-025 | `81175b9` | `PlayerMana` | `HandlePlayerMana` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-026 | `81175b9` | `PlayerTeam` | `HandlePlayerTeam` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-027 | `81175b9` | `SignRead` | `HandleSignRead` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-028 | `81175b9` | `SignNew` | `HandleSign` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-029 | `81175b9` | `LiquidSet` | `HandleLiquidSet` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-030 | `81175b9` | `PlayerBuff` | `HandlePlayerBuffList` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-031 | `81175b9` | `NpcSpecial` | `HandleSpecial` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-032 | `81175b9` | `NpcAddBuff` | `HandleNPCAddBuff` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-033 | `81175b9` | `PlayerAddBuff` | `HandlePlayerAddBuff` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-034 | `81175b9` | `UpdateNPCHome` | `HandleUpdateNPCHome` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-035 | `81175b9` | `SpawnBossorInvasion` | `HandleSpawnBoss` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-036 | `81175b9` | `PaintTile` | `HandlePaintTile` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-037 | `81175b9` | `PaintWall` | `HandlePaintWall` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-038 | `81175b9` | `Teleport` | `HandleTeleport` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-039 | `81175b9` | `PlayerHealOther` | `HandleHealOther` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-040 | `81175b9` | `CatchNPC` | `HandleCatchNpc` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-041 | `81175b9` | `ReleaseNPC` | `HandleReleaseNpc` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-042 | `81175b9` | `TeleportationPotion` | `HandleTeleportationPotion` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-043 | `81175b9` | `CompleteAnglerQuest` | `HandleCompleteAnglerQuest` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-044 | `81175b9` | `NumberOfAnglerQuestsCompleted` | `HandleNumberOfAnglerQuestsCompleted` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-045 | `81175b9` | `PlaceObject` | `HandlePlaceObject` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-046 | `81175b9` | `LoadNetModule` | `HandleLoadNetModule` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-047 | `81175b9` | `PlaceTileEntity` | `HandlePlaceTileEntity` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-048 | `81175b9` | `PlaceItemFrame` | `HandlePlaceItemFrame` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-049 | `81175b9` | `UpdateItemDrop` | `HandleItemDrop` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-050 | `81175b9` | `SyncExtraValue` | `HandleSyncExtraValue` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-051 | `81175b9` | `KillPortal` | `HandleKillPortal` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-052 | `81175b9` | `PlayerTeleportPortal` | `HandlePlayerPortalTeleport` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-053 | `81175b9` | `NpcTeleportPortal` | `HandleNpcTeleportPortal` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-054 | `81175b9` | `GemLockToggle` | `HandleGemLockToggle` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-055 | `81175b9` | `MassWireOperation` | `HandleMassWireOperation` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-056 | `81175b9` | `ToggleParty` | `HandleToggleParty` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-057 | `81175b9` | `CrystalInvasionStart` | `HandleOldOnesArmy` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-058 | `81175b9` | `PlayerHurtV2` | `HandlePlayerDamageV2` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-059 | `81175b9` | `PlayerDeathV2` | `HandlePlayerKillMeV2` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-060 | `81175b9` | `Emoji` | `HandleEmoji` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-061 | `81175b9` | `TileEntityDisplayDollItemSync` | `HandleTileEntityDisplayDollItemSync` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-062 | `81175b9` | `RequestTileEntityInteraction` | `HandleRequestTileEntityInteraction` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-063 | `81175b9` | `SyncTilePicking` | `HandleSyncTilePicking` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-064 | `81175b9` | `LandGolfBallInCup` | `HandleLandGolfBallInCup` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-065 | `81175b9` | `FishOutNPC` | `HandleFishOutNPC` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-066 | `81175b9` | `FoodPlatterTryPlacing` | `HandleFoodPlatterTryPlacing` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-067 | `81175b9` | `SyncCavernMonsterType` | `HandleSyncCavernMonsterType` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-068 | `81175b9` | `SyncLoadout` | `HandleSyncLoadout` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-069 | `81175b9` | `SpectatePlayer` | `HandleSyncPlayerSpectating` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-070 | `81175b9` | `TeamChangeFromUI` | `HandlePlayerTeam` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |
| packet-071 | `81175b9` | `TEDeadCellsDisplayJar` | `HandleDisplayJar` | Unresolved | Semantic branch audit pending | Unresolved | Phase 25 | Open | Pending |

## Connection-state ledger

The connection audit must separately cover passwordless and password joins, slot reuse and reconnect. The canonical sequence under review is:

`ConnectRequest (1)` → slot/password challenge → player info/inventory burst → `ClientUUID (68)` / platform metadata → world request/data → section transfer → spawn → `FinishedConnectingToServer (129)` → disconnect/reconnect.

The ClientUUID rule is explicit: vanilla treats the persisted value as an opaque string. Vega must accept a non-empty bounded completely-consumed token, preserve exact comparison semantics and never treat the client-controlled token as cryptographic authentication.

## Command ledger

The pinned TerraZ command registry contains 95 registrations. Phase 25 requires a separate row for every registration with canonical name, aliases, permission, console availability, parsing/selectors, side effects, audit behavior and configuration/database dependencies. Rows are not yet claimed complete here; their enumeration remains open and must not be inferred from the packet ledger.

## Lifecycle and non-command ledger

Open audit families include admission policy, save/shutdown/crash recovery, configuration reload, accounts/groups/permissions, moderation/chat, regions/protections, item/projectile/tile bans, console/log routing, plugin lifecycle/background cleanup and malformed-packet resynchronization.

## Completion rule

This file is allowed to contain `Unresolved`/`Open` rows during development. Phase 25 is complete only when every pinned vanilla inbound connection/packet branch and every pinned TerraZ packet/command registration has a reviewed decision and executable evidence or an explicit reviewed drop rationale.
