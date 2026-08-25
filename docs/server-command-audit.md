# Vega server command audit

This is the Phase 25 command sub-ledger referenced by `server-behavior-audit.md`.

Pinned source: `TerraZ-Team/TShock` `general-devel@81175b934497f9ef0ee5b5200334e4260b0cc06c`, `TShockAPI/Commands.cs::InitCommands`.

The pinned registry contains exactly 95 built-in `Command` registrations. Every registration stays visible until its method branches are reviewed and assigned `Vega built-in`, `Terraria native passthrough`, `Optional module` or `Drop`.

`Pending` is not a decision. It means the row is inventoried but the corresponding method/side-effect audit is still open.

| ID | Canonical | Aliases | Permission | Console | Handler | Parsing / selectors | Side effects | Audit behavior | Config / DB dependencies | Vega decision | Status | Evidence |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| command-001 | `setup` | - | - | No | `SetupToken` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-002 | `user` | - | `Permissions.user` | Yes | `ManageUsers` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-003 | `login` | - | `Permissions.canlogin` | No | `AttemptLogin` | TShock accepts zero args for UUID login when enabled, one password for the character-name account, or username/password only when `AllowLoginAnyUsername`; Vega accepts one password for the player-name account or two args for any account and has no zero-arg UUID form | TShock enforces login-attempt/crowd-control gates, hooks, group/account state, SSC restore, remembered position and UUID update; Vega verifies password, enforces active ban/account-in-use, refreshes authorization/culture, records login time and identity evidence, but does not yet reproduce the TShock attempt/crowd-control/SSC branches | TShock sets `DoLog=false` and emits explicit success/failure logs without the password; Vega descriptor is explicitly `NameOnly`, so the dispatcher cannot expose credentials, but the Operations sink is not yet wired into runtime composition | TShock uses account/character DB plus `MaximumLoginAttempts`, `DisableUUIDLogin`, `AllowLoginAnyUsername`, SSC and remembered-position settings; Vega uses account/auth/ban/role-lease/identity stores with a fixed command shape | `Vega built-in` | Partial | `Commands.cs::AttemptLogin`; `PlayerAccountCommands.cs::LoginAccountCommand`; `PlayerAccountSessionService::LoginAsync` |
| command-004 | `logout` | - | `Permissions.canlogout` | No | `Logout` | TShock has no explicit argument-count branch; Vega requires zero arguments | TShock rejects logout while an NPC window is open, clears the TShock session and warns when SSC requires login; Vega removes the authenticated account, permissions and session culture but has no NPC-window/SSC warning branch | TShock sets `DoLog=false`; Vega defaults to `NameOnly`, but the Operations command-audit sink is not yet wired into runtime composition | TShock depends on live TSPlayer/SSC state; Vega depends on the account-session and authorization state | `Vega built-in` | Partial | `Commands.cs::Logout`; `PlayerAccountCommands.cs::LogoutAccountCommand`; `PlayerAccountSessionService::Logout` |
| command-005 | `password` | - | `Permissions.canchangepassword` | No | `PasswordUser` | Both require an authenticated player and old/new password arguments; Vega rejects any argument count other than two | Both verify the current password and replace the stored hash; TShock uses configured minimum length, while Vega validates a fixed 8..1024 range and revision-guards the account mutation | TShock sets `DoLog=false` but explicitly logs success/failure without either password; Vega descriptor is explicitly `NameOnly`, so credentials cannot enter an audit record, but runtime sink wiring remains open | TShock uses account DB and `MinimumPasswordLength`; Vega uses the account service/revision and fixed password bounds | `Vega built-in` | Partial | `Commands.cs::PasswordUser`; `PlayerAccountCommands.cs::PasswordAccountCommand`; `PlayerAccountSessionService::ChangePasswordAsync` |
| command-006 | `register` | - | `Permissions.canregister` | No | `RegisterUser` | TShock accepts one password for the character-name account or two args only when `AllowRegisterAnyUsername`; Vega accepts one password or two account/password args unconditionally | TShock creates the account in configured default group, stores UUID, echoes the password back to the player and leaves login separate; Vega creates group `default`, never echoes the password and immediately establishes/authenticates the new account with authorization/culture/identity evidence | TShock sets `DoLog=false` and explicitly logs the account name; Vega descriptor is explicitly `NameOnly`, so password/account arguments cannot enter command audit, but runtime sink wiring remains open | TShock uses account DB, `MinimumPasswordLength`, `AllowRegisterAnyUsername`, `DefaultRegistrationGroupName` and UUID-login settings; Vega uses account/auth/ban/identity services and fixed name/password bounds | `Vega built-in` | Partial | `Commands.cs::RegisterUser`; `PlayerAccountCommands.cs::RegisterAccountCommand`; `PlayerAccountSessionService::RegisterAsync` |
| command-007 | `accountinfo` | `ai` | `Permissions.checkaccountinfo` | Yes | `ViewAccountInfo` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-008 | `ban` | - | `Permissions.ban` | Yes | `Ban` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-009 | `broadcast` | `bc`, `say` | `Permissions.broadcast` | Yes | `Broadcast` | TShock: zero args is a no-op; Vega: zero args is invalid usage; otherwise both join all arguments; no selectors | TShock broadcasts `(Server Broadcast) ` plus text using configured RGB; Vega fans the plain text out through `IPlayerMessenger` and reports delivery/failure counts | TShock uses the normal `DoLog=true` command audit; Vega now has descriptor-level redaction and an Operations audit sink, but runtime composition has not wired the sink yet and `broadcast` still uses the safe default `NameOnly` | TShock depends on `BroadcastRGB`; Vega has no config or DB dependency | `Vega built-in` | Partial | `Commands.cs::Broadcast`; `TerrariaAdministrationCommands.cs::BroadcastCommand`; `BroadcastCommandTests`; `CommandDispatcherAuditTests` |
| command-010 | `displaylogs` | - | `Permissions.logs` | Yes | `DisplayLogs` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-011 | `group` | - | `Permissions.managegroup` | Yes | `Group` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-012 | `itemban` | - | `Permissions.manageitem` | Yes | `ItemBan` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-013 | `projban` | - | `Permissions.manageprojectile` | Yes | `ProjectileBan` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-014 | `tileban` | - | `Permissions.managetile` | Yes | `TileBan` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-015 | `region` | - | `Permissions.manageregion` | Yes | `Region` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-016 | `kick` | - | `Permissions.kick` | Yes | `Kick` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-017 | `mute` | `unmute` | `Permissions.mute` | Yes | `Mute` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-018 | `overridessc` | `ossc` | `Permissions.savessc` | Yes | `OverrideSSC` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-019 | `savessc` | - | `Permissions.savessc` | Yes | `SaveSSC` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-020 | `uploadssc` | - | `Permissions.uploaddata` | Yes | `UploadJoinData` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-021 | `tempgroup` | - | `Permissions.settempgroup` | Yes | `TempGroup` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-022 | `su` | - | `Permissions.su` | Yes | `SubstituteUser` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-023 | `sudo` | - | `Permissions.su` | Yes | `SubstituteUserDo` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-024 | `userinfo` | `ui` | `Permissions.userinfo` | Yes | `GrabUserUserInfo` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-025 | `annoy` | - | `Permissions.annoy` | Yes | `Annoy` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-026 | `rocket` | - | `Permissions.annoy` | Yes | `Rocket` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-027 | `firework` | - | `Permissions.annoy` | Yes | `FireWork` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-028 | `checkupdates` | - | `Permissions.maintenance` | Yes | `CheckUpdates` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-029 | `off` | `exit`, `stop` | `Permissions.maintenance` | Yes | `Off` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-030 | `off-nosave` | `exit-nosave`, `stop-nosave` | `Permissions.maintenance` | Yes | `OffNoSave` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-031 | `reload` | - | `Permissions.cfgreload` | Yes | `Reload` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-032 | `serverpassword` | - | `Permissions.cfgpassword` | Yes | `ServerPassword` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-033 | `version` | - | `Permissions.maintenance` | Yes | `GetVersion` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-034 | `whitelist` | - | `Permissions.whitelist` | Yes | `Whitelist` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-035 | `give` | `g` | `Permissions.give` | Yes | `Give` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-036 | `item` | `i` | `Permissions.item` | No | `Item` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-037 | `butcher` | - | `Permissions.butcher` | Yes | `Butcher` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-038 | `renamenpc` | - | `Permissions.renamenpc` | Yes | `RenameNPC` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-039 | `maxspawns` | - | `Permissions.maxspawns` | Yes | `MaxSpawns` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-040 | `spawnboss` | `sb` | `Permissions.spawnboss` | No | `SpawnBoss` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-041 | `spawnmob` | `sm` | `Permissions.spawnmob` | No | `SpawnMob` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-042 | `spawnrate` | - | `Permissions.spawnrate` | Yes | `SpawnRate` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-043 | `clearangler` | - | `Permissions.clearangler` | Yes | `ClearAnglerQuests` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-044 | `home` | - | `Permissions.home` | No | `Home` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-045 | `spawn` | - | `Permissions.spawn` | No | `Spawn` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-046 | `tp` | - | `Permissions.tp` | No | `TP` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-047 | `tphere` | - | `Permissions.tpothers` | No | `TPHere` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-048 | `tpnpc` | - | `Permissions.tpnpc` | No | `TPNpc` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-049 | `tppos` | - | `Permissions.tppos` | No | `TPPos` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-050 | `pos` | - | `Permissions.getpos` | No | `GetPos` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-051 | `tpallow` | - | `Permissions.tpallow` | No | `TPAllow` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-052 | `worldmode` | `gamemode` | `Permissions.toggleexpert` | Yes | `ChangeWorldMode` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-053 | `antibuild` | - | `Permissions.antibuild` | Yes | `ToggleAntiBuild` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-054 | `grow` | - | `Permissions.grow` | No | `Grow` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-055 | `forcehalloween` | - | `Permissions.halloween` | Yes | `ForceHalloween` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-056 | `forcexmas` | - | `Permissions.xmas` | Yes | `ForceXmas` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-057 | `worldevent` | - | `Permissions.manageevents` | Yes | `ManageWorldEvent` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-058 | `hardmode` | - | `Permissions.hardmode` | Yes | `Hardmode` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-059 | `evil` | - | `Permissions.switchevil` | Yes | `SwitchEvil` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-060 | `protectspawn` | - | `Permissions.editspawn` | Yes | `ProtectSpawn` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-061 | `save` | - | `Permissions.worldsave` | Yes | `Save` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-062 | `setspawn` | - | `Permissions.worldspawn` | No | `SetSpawn` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-063 | `setdungeon` | - | `Permissions.dungeonposition` | No | `SetDungeon` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-064 | `settle` | - | `Permissions.worldsettle` | Yes | `Settle` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-065 | `time` | - | `Permissions.time` | Yes | `Time` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-066 | `wind` | - | `Permissions.wind` | Yes | `Wind` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-067 | `worldinfo` | - | `Permissions.worldinfo` | Yes | `WorldInfo` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-068 | `buff` | - | `Permissions.buff` | No | `Buff` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-069 | `clear` | - | `Permissions.clear` | Yes | `Clear` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-070 | `gbuff` | `buffplayer` | `Permissions.buffplayer` | Yes | `GBuff` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-071 | `godmode` | `god` | `Permissions.godmode` | Yes | `ToggleGodMode` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-072 | `heal` | - | `Permissions.heal` | Yes | `Heal` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-073 | `kill` | `slay` | `Permissions.kill` | Yes | `Kill` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-074 | `me` | - | `Permissions.cantalkinthird` | Yes | `ThirdPerson` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-075 | `party` | `p` | `Permissions.canpartychat` | No | `PartyChat` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-076 | `reply` | `r` | `Permissions.whisper` | Yes | `Reply` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-077 | `rest` | - | `Rests.RestPermissions.restmanage` | Yes | `ManageRest` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-078 | `slap` | - | `Permissions.slap` | Yes | `Slap` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-079 | `serverinfo` | - | `Permissions.serverinfo` | Yes | `ServerInfo` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-080 | `warp` | - | `Permissions.warp` | Yes | `Warp` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-081 | `whisper` | `w`, `tell`, `pm`, `dm` | `Permissions.whisper` | Yes | `Whisper` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-082 | `wallow` | `wa` | `Permissions.whisper` | No | `Wallow` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-083 | `dump-reference-data` | - | `Permissions.createdumps` | Yes | `CreateDumps` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-084 | `sync` | - | `Permissions.synclocalarea` | Yes | `SyncLocalArea` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-085 | `respawn` | - | `Permissions.respawn` | Yes | `Respawn` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-086 | `aliases` | - | - | Yes | `Aliases` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-087 | `help` | - | - | Yes | `Help` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-088 | `motd` | - | - | Yes | `Motd` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-089 | `playing` | `online`, `who` | - | Yes | `ListConnectedPlayers` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-090 | `rules` | - | - | Yes | `Rules` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-091 | `death` | - | - | No | `ShowDeath` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-092 | `pvpdeath` | - | - | No | `ShowPVPDeath` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-093 | `alldeath` | - | - | Yes | `ShowAllDeath` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-094 | `allpvpdeath` | - | - | Yes | `ShowAllPVPDeath` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |
| command-095 | `bossdamage` | - | - | Yes | `BossDamage` | Pending | Pending | Pending | Pending | Pending | Open | `Commands.cs::InitCommands` |

## Completion rule

Inventory completion is not semantic completion. Every `Pending` field must be resolved by reviewing the pinned handler method and comparing its behavior with Vega and the pinned Terraria 1.4.5.8 server. Intentional omissions require an operator-visible `Drop` rationale.