# TShock 6.2.1 — Vega: command inventory (27 November 2026)

Baseline: official `Pryaxis/TShock` tag `v6.2.1` (`b25b3e5bf59870e08175a33df3f8a9c7fc9958c1`), compared with local TerraZ fork `81175b93` and Vega `8078b5ba`. All 95 registration names, alias lists, first permission expressions and console flags match between the two TShock source registries. The previous [command ledger](server-command-audit.md) contains the original handler/permission and behavior investigations. This supplement checks the current Vega deployed catalog rather than claiming every original branch is equivalent.

A clean native Vega instance was started with a copied medium world on loopback. Console `help`, `aliases` and `help <name>` for every TShock canonical command were executed; no administrative gameplay mutations were issued. Raw capture: `artifacts/parity-2026-11-27/console.log`. Console discovery found 86 Vega canonical commands and 38 aliases; 70 of the 95 TShock names resolve on the console. The other 23 have source-confirmed player registrations; two (`su`, `sudo`) are deliberately omitted. All 27 original alias names map to the corresponding commands (console or player source).

Legend: **C** = actual console help; **P** = player-only source registration, not a joined-client test; **A** = operator alternative; **S** = SSC conditional; **D** = deliberately absent. The table is an inventory, not a complete semantic pass. `help` reports Vega permission names for console commands; player permission rules and original detailed decisions are in the previous ledger.

| # | TShock name and aliases | Original console | Vega current syntax | Vega aliases | Vega availability / evidence |
|---:|---|---|---|---|---|
| 1 | `setup` | no | `setup (server console)` | none | A (console bootstrap); vega.accounts.manage |
| 2 | `user` | yes | `account list \| show <name> \| create <name> <password> [group] \| delete <name> \| group <name> <group> \| password <name> <new-password> \| temp <name> <group> <duration> \| temp-clear <name> \| temp-list` | user | C; vega.accounts.manage |
| 3 | `login` | no | `login <password> \| login <account> <password>` | - | P; PlayerAccountCommands |
| 4 | `logout` | no | `logout` | - | P; PlayerAccountCommands |
| 5 | `password` | no | `password <current> <new>` | - | P; PlayerAccountCommands |
| 6 | `register` | no | `register <password> \| register <account> <password>` | - | P; PlayerAccountCommands |
| 7 | `accountinfo, ai` | yes | `accountinfo <account>` | ai | C; vega.accounts.inspect or vega.accounts.manage |
| 8 | `ban` | yes | `ban <player> [reason] \| ban add <name\|account\|uuid\|ip\|cidr> <value> <duration\|permanent> [reason] \| ban list \| ban show <id> \| unban <id>` | unban | C; vega.admin.ban |
| 9 | `broadcast, bc, say` | yes | `broadcast <message>` | bc, say | C; vega.admin.broadcast |
| 10 | `displaylogs` | yes | `displaylogs [count:1..20] [information\|warning\|error\|critical]` | none | C; vega.operations.logs |
| 11 | `group` | yes | `group list \| show <name> \| create <name> [parent] \| delete <name> \| parent <name> <parent\|none> \| grant\|deny\|revoke <name> <permission>` | groups | C; vega.groups.manage |
| 12 | `itemban` | yes | `<command> list [page] \| add <id> \| del <id>` | none | C; vega.restrictions.manage |
| 13 | `projban` | yes | `<command> list [page] \| add <id> \| del <id>` | none | C; vega.restrictions.manage |
| 14 | `tileban` | yes | `<command> list [page] \| add <id> \| del <id>` | none | C; vega.restrictions.manage |
| 15 | `region` | yes | `region list \| show <name> \| create <name> <x> <y> <width> <height> [priority] [owner] \| delete <name> \| resize <name> <x> <y> <width> <height> \| priority <name> <value> \| owner <name> <owner\|none> \| member\|group <name> add\|remove <subject> \| flag <name> <flag> <true\|false>` | regions | C; vega.regions.manage |
| 16 | `kick` | yes | `kick <player> [reason]` | none | C; vega.admin.kick |
| 17 | `mute, unmute` | yes | `mute <online-player> <duration> [reason] \| mute add <name\|account> <value> <duration> [reason] \| mute list \| mute show <id> \| unmute <id>` | unmute | C; vega.admin.mute |
| 18 | `overridessc, ossc` | yes | `overridessc <authenticated-player>` | ossc | C; vega.ssc.override |
| 19 | `savessc` | yes | `savessc` | none | C; vega.ssc.save |
| 20 | `uploadssc` | yes | `uploadssc [player] confirm` | none | C; vega.ssc.upload |
| 21 | `tempgroup` | yes | `tempgroup <account> <group> <duration> \| tempgroup <account> clear \| tempgroup list` | none | C; vega.accounts.manage |
| 22 | `su` | yes | `Not implemented` | - | D — explicit permission/delegation model |
| 23 | `sudo` | yes | `Not implemented` | - | D — explicit permission/delegation model |
| 24 | `userinfo, ui` | yes | `userinfo <player>` | ui | C; vega.players.inspect |
| 25 | `annoy` | yes | `annoy <player> <seconds:1..30>` | none | C; vega.players.annoy |
| 26 | `rocket` | yes | `rocket <player>` | none | S (authoritative SSC required); vega.players.rocket |
| 27 | `firework` | yes | `firework <player> [red\|green\|blue\|yellow\|star\|spiral\|rings\|flower]` | none | C; vega.players.firework |
| 28 | `checkupdates` | yes | `checkupdates` | none | C; vega.cfg.maintenance |
| 29 | `off, exit, stop` | yes | `shutdown [reason]` | stop, off, exit | C; vega.cfg.maintenance |
| 30 | `off-nosave, exit-nosave, stop-nosave` | yes | `off-nosave [reason]` | exit-nosave, stop-nosave | C; vega.cfg.maintenance.nosave |
| 31 | `reload` | yes | `reload` | none | C; vega.config.manage |
| 32 | `serverpassword` | yes | `serverpassword <password> \| serverpassword clear \| serverpassword set <password>` | none | C; vega.cfg.password |
| 33 | `version` | yes | `version` | none | C; none |
| 34 | `whitelist` | yes | `whitelist list [page] \| on \| off \| add <ip\|cidr> \| del <ip\|cidr>` | none | C; vega.admin.whitelist |
| 35 | `give, g` | yes | `give <item> <player> [stack] [prefix-id]` | g | C; vega.items.give |
| 36 | `item, i` | no | `item <item> [stack] [prefix-id]` | i | P; ItemCommands |
| 37 | `butcher` | yes | `butcher [npc-id\|name]` | none | C; vega.world.butcher |
| 38 | `renamenpc` | yes | `renamenpc <npc-id\|name> <new name>` | none | C; vega.world.renamenpc |
| 39 | `maxspawns` | yes | `maxspawns [0..200]` | none | C; vega.world.maxspawns |
| 40 | `spawnboss, sb` | no | `spawnboss <boss-id\|name> [count:1..5]` | sb | P; NpcCommands |
| 41 | `spawnmob, sm` | no | `spawnmob <npc-id\|name> [count:1..20]` | sm | P; NpcCommands |
| 42 | `spawnrate` | yes | `spawnrate [1..60000]` | none | C; vega.world.spawnrate |
| 43 | `clearangler` | yes | `clearangler` | none | C; vega.world.clearangler |
| 44 | `home` | no | `home` | - | P; NavigationModule |
| 45 | `spawn` | no | `spawn` | - | P; NavigationModule |
| 46 | `tp` | no | `tp <player> \| tp <source> <destination>` | - | P; NavigationModule |
| 47 | `tphere` | no | `tphere <player>` | - | P; NavigationModule |
| 48 | `tpnpc` | no | `tpnpc <npc-name\|slot:number>` | - | P; NavigationModule |
| 49 | `tppos` | no | `tppos <tile-x> <tile-y>` | - | P; NavigationModule |
| 50 | `pos` | no | `pos [player]` | - | P; NavigationModule |
| 51 | `tpallow` | no | `tpallow` | - | P; NavigationModule |
| 52 | `worldmode, gamemode` | yes | `worldmode [normal\|expert\|master]` | gamemode | C; vega.world.mode |
| 53 | `antibuild` | yes | `antibuild [on\|off\|status]` | none | C; vega.world.antibuild |
| 54 | `grow` | no | `grow <tree-type> [tile-x tile-y (console)] \| grow help` | none | C; vega.world.grow |
| 55 | `forcehalloween` | yes | `forcehalloween [on\|off\|status]` | none | C; vega.world.forcehalloween |
| 56 | `forcexmas` | yes | `forcexmas [on\|off\|status]` | none | C; vega.world.forcexmas |
| 57 | `worldevent` | yes | `worldevent <rain\|bloodmoon\|eclipse\|fullmoon\|slimerain\|sandstorm\|pumpkinmoon\|frostmoon\|goblinarmy\|snowlegion\|pirateinvasion\|martianmadness> [on\|off]` | none | C; vega.world.events |
| 58 | `hardmode` | yes | `hardmode [status\|start confirm]` | none | C; vega.world.hardmode |
| 59 | `evil` | yes | `evil [crimson\|corruption]` | none | C; vega.world.evil |
| 60 | `protectspawn` | yes | `protectspawn [on\|off\|status\|radius <1..500>]` | none | C; vega.world.protectspawn |
| 61 | `save` | yes | `save` | none | C; vega.cfg.maintenance |
| 62 | `setspawn` | no | `setspawn [tile-x tile-y]` | none | C; vega.world.setspawn |
| 63 | `setdungeon` | no | `setdungeon [tile-x tile-y]` | none | C; vega.world.setdungeon |
| 64 | `settle` | yes | `settle` | none | C; vega.world.settle |
| 65 | `time` | yes | `time [day\|noon\|night\|midnight\|HH:mm]` | none | C; vega.world.time |
| 66 | `wind` | yes | `wind <mph>` | none | C; vega.world.wind |
| 67 | `worldinfo` | yes | `worldinfo` | none | C; vega.world.info |
| 68 | `buff` | no | `buff <buff> [seconds]` | - | P; PlayerVitalCommands |
| 69 | `clear` | yes | `clear <item\|projectile\|npc> [all]` | none | C; vega.world.clear |
| 70 | `gbuff, buffplayer` | yes | `gbuff <player> <buff> [seconds]` | buffplayer | C; vega.players.buff.others |
| 71 | `godmode, god` | yes | `godmode [player] [on\|off]` | god | C; vega.players.godmode |
| 72 | `heal` | yes | `heal [player] [amount]` | none | C; vega.players.heal |
| 73 | `kill, slay` | yes | `kill <player>` | slay | C; vega.players.kill |
| 74 | `me` | yes | `me <action>` | - | P; PlayerChatCommands |
| 75 | `party, p` | no | `party <message>` | p | P; PlayerChatCommands |
| 76 | `reply, r` | yes | `reply <message>` | r | P; PlayerChatCommands |
| 77 | `rest` | yes | `rest status \| listusers \| destroytokens` | none | C; vega.operations.rest.manage |
| 78 | `slap` | yes | `slap <player> [damage:0..32767]` | none | C; vega.players.slap |
| 79 | `serverinfo` | yes | `serverinfo` | none | C; vega.info |
| 80 | `warp` | yes | `warp <name> \| list [page] \| add <name> \| del <name> \| hide <name> <true\|false> \| send <player> <name>` | none | C; vega.navigation.warp |
| 81 | `whisper, w, tell, pm, dm` | yes | `whisper <player> <message>` | w, tell, pm, dm | P; PlayerChatCommands |
| 82 | `wallow, wa` | no | `wallow` | wa | P; PlayerChatCommands |
| 83 | `dump-reference-data` | yes | `dump-reference-data [item\|npc\|buff\|projectile\|tile\|all]` | none | C; vega.world.reference |
| 84 | `sync` | yes | `sync [radius:1..16] \| sync <player> [radius]` | none | C; vega.world.sync |
| 85 | `respawn` | yes | `respawn [player]` | none | C; vega.players.respawn |
| 86 | `aliases` | yes | `aliases` | none | C; none |
| 87 | `help` | yes | `help [command]` | commands | C; none |
| 88 | `motd` | yes | `motd` | none | C; none |
| 89 | `playing, online, who` | yes | `playing [-i] [page]` | online, who | C; none |
| 90 | `rules` | yes | `rules` | none | C; none |
| 91 | `death` | no | `death` | - | P; PlayerStatisticsCommand |
| 92 | `pvpdeath` | no | `pvpdeath` | - | P; PlayerStatisticsCommand |
| 93 | `alldeath` | yes | `alldeath [page]` | none | C; vega.players.deaths.others |
| 94 | `allpvpdeath` | yes | `allpvpdeath [page]` | none | C; vega.players.deaths.others |
| 95 | `bossdamage` | yes | `bossdamage [page]` | none | C; none |

## Important catalog differences

- TShock allows console `me`, `reply`, `whisper`; Vega registers them only for player sessions. `su` and `sudo` are absent by design. `setup`, `grow`, `setspawn`, `setdungeon` are available on the Vega console; `setup` is an operator bootstrap alternative, not a remote setup token.
- Console extras: `backup`, `chestpolicy`, `config`, `fake`, `invsee`, `lang`, `pfake`, `plugin`, `sandbox`, `scene`, `schematic`, `softrestart`, `tps`, `wipe`, `worldclean`, `worldtransfer`. They are not substitutes for missing TShock subcommands.
- Source and live-console names/aliases match, but argument, permission and side-effect differences are substantive. See the [functional and hook comparison](tshock-parity-2026-11-27.md) for the branch-level findings and implementation priorities.

## Original literal selectors / subbranches

The following literal selectors were extracted from the actual official `Commands.cs` handler bodies (`case` labels and first-argument/subcommand comparisons). Nested choices are included, e.g. region expansion directions and world-event helper handlers. This inventory is not a substitute for inspecting argument counts, non-literal branches, permissions or native side effects; the functional gap matrix assigns the important differences.

| Official handler | Explicit selector literals |
|---|---|
| `ManageUsers` | `add`, `del`, `password`, `group`, `help` |
| `Ban` | `add`, `del`, `list`, `details`, `identifiers`, `examples`, `help` |
| `ManageRest` | `listusers`, `destroytokens` |
| `ManageWorldEvent` | `meteor`, `fullmoon`, `full moon`, `bloodmoon`, `blood moon`, `eclipse`, `invade`, `invasion`, `sandstorm`, `rain`, `lanternsnight`, `lanterns`, `meteorshower` |
| `Invade` | `goblin`, `goblins`, `snowman`, `snowmen`, `pirate`, `pirates`, `pumpkin`, `pumpkinmoon`, `frost`, `frostmoon`, `martian`, `martians` |
| `Rain` | `slime`, `coin` |
| `SpawnBoss` | `*`, `all`, `brain`, `brain of cthulhu`, `boc`, `destroyer`, `duke`, `duke fishron`, `fishron`, `eater`, `eater of worlds`, `eow`, `eye`, `eye of cthulhu`, `eoc`, `golem`, `king`, `king slime`, `ks`, `plantera`, `prime`, `skeletron prime`, `queen bee`, `qb`, `skeletron`, `twins`, `wof`, `wall of flesh`, `moon`, `moon lord`, `ml`, `empress`, `empress of light`, `eol`, `queen slime`, `qs`, `lunatic`, `lunatic cultist`, `cultist`, `lc`, `betsy`, `flying dutchman`, `flying`, `dutchman`, `mourning wood`, `pumpking`, `everscream`, `santa-nk1`, `santa`, `ice queen`, `martian saucer`, `solar pillar`, `nebula pillar`, `vortex pillar`, `stardust pillar`, `deerclops` |
| `TP` | `*` |
| `Warp` | `add`, `del`, `hide`, `send`, `list` |
| `Group` | `add`, `addperm`, `help`, `parent`, `suffix`, `prefix`, `color`, `rename`, `del`, `delperm`, `list`, `listperm` |
| `ItemBan` | `add`, `allow`, `del`, `disallow`, `help`, `list` |
| `ProjectileBan` | `add`, `allow`, `del`, `disallow`, `help`, `list` |
| `TileBan` | `add`, `allow`, `del`, `disallow`, `help`, `list` |
| `Time` | `day`, `night`, `noon`, `midnight` |
| `Region` | `name`, `set`, `define`, `protect`, `delete`, `clear`, `allow`, `remove`, `allowg`, `removeg`, `list`, `info`, `z`, `resize`, `expand`, `u`, `up`, `r`, `right`, `d`, `down`, `l`, `left`, `rename`, `tp`, `help` |
| `FireWork` | `red`, `r`, `green`, `g`, `blue`, `b`, `yellow`, `y`, `r2`, `star`, `g2`, `spiral`, `b2`, `rings`, `y2`, `flower` |
| `Clear` | `item`, `items`, `i`, `npc`, `npcs`, `n`, `proj`, `projectile`, `projectiles`, `p` |
| `Grow` | `help`, `basic`, `boreal`, `mahogany`, `sakura`, `willow`, `shadewood`, `ebonwood`, `pearlwood`, `palm`, `hallowpalm`, `crimsonpalm`, `corruptpalm`, `topaz`, `amethyst`, `sapphire`, `emerald`, `ruby`, `diamond`, `amber`, `cactus`, `herb`, `mushroom` |
