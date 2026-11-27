# Roadmap review — 26 November 2026

**Target:** independently replace TShock as an operator-facing server, retaining official Terraria 1.4.6.1 gameplay, protocol 329, and `.wld` semantics through TerrariaRuntime 1.1.9. The protocol library is Multiplicity 4.0.1. Neither TShock API compatibility nor a separate Terraria simulation is a prerequisite.

## Current implementation checkpoint

- Historical registry decisions: 46 packet policy slices and 25 native baselines (71 total); 92 command implementations/slices/alternatives, one command conditional on native SSC, two explicit omissions (`su`, `sudo`). Counts describe decisions and implemented slices, not complete branch equivalence. See [packet ledger](server-behavior-audit.md) and [command ledger](server-command-audit.md).
- Native primary SSC is now available by explicit configuration, with a native world-data flag, captured import state before WorldData, blank pending state, exact authenticated restoration, ready-state gate, logout reset and shared sandbox store. Non-disabled sandbox SSC modes use the native flag. The initial and joined inventory/loadout sanity checks are part of production ingress. Native hooks already exist in the released patcher; no new patcher version is required for this slice.
- Shared whitelist and bans apply to primary and running sandbox worlds. Their ordinary cross-world lifecycle has automated smoke coverage; remaining operator-equivalence details are still ledger work.
- The native game thread remains the authority for mutable gameplay; isolated worlds already have individual processes. The optional ordered TCP adapter and paged tiles are experiments, not default replacements. FakeProvider-backed scenes and plugin lifecycle are implemented, including native tile capture.
- Current-schema offline migration and protected REST measurements are implemented; the 200/255-peer loopback transport workload is verified separately from actual joined players. The Ordered completion count ceiling was raised to 8192 under the unchanged cooperative time budget after A/B confirmed the previous count ceiling caused overload. Shared SSC read persistence has a measured allocation/latency improvement. Repeated-fault quarantine and automatic retirement for dynamic plugins use the existing collectible unload boundary.
- P0/P1 performance round: empty Ordered completion probes now avoid connection locks (sparse 255-peer loopback drain p95/p99 improved about 24%/34%, dense load essentially unchanged). Periodic SSC validation/JSON encoding runs on the existing asynchronous worker path after a detached native snapshot; synchronous pre-reset capture stays on the game thread. Scene redraws rotate incomplete requests after each batch to avoid head-of-line stalls. These changes are tested without moving native simulation or mutable Terraria state off its owning thread; full joined-client qualification remains open.
- TerraRuntime reuse: the independent `.trschem` v1 codec supports native region export (including NPCs and typed tile entities), file inspection and non-canonical scene preview. File encoding/decoding and preview projection use bounded background work; directories are local to each world process. Source codec and NPC data round trips are verified. Complete native schematic world materialization and server-owned native PlayerBots remain separate work. Game-thread CPU percentage from the TerraRuntime thread clock is exposed in the tick-rate snapshot and terminal dashboard.
- Plugin SDK round: native NPC create/death/transform/loot and projectile ingress notifications expose detached world-local records. Transform and loot decisions use the existing per-plugin fault guard and automatically retire with the plugin. Schematics expose bounded capture and tiles/NPC materialization in idle isolated sandboxes; unsupported native entity sections are explicitly refused. Automatic full `.trschem` sandbox creation and other object materialization remain open; PlayerBots are deferred.
- Custom NPC actors: adapted TerraRuntime's generation-owned actor, horizontal intent and immutable offer catalog boundaries to native NPCs. Plugin-owned leases retire on unload, support name/position/intent, and expose session-confirmed conversation events. Stock shop packet presentation and explicit coin/inventory purchases are separate; stock client UI opening and receipt qualification, banks/custom currencies and richer behavior/dialogue remain open. See [custom NPCs](custom-npcs.md).

## Remaining work, ordered by actual release relevance

**27 November update:** [255-player mixed native gameplay benchmark](performance-mixed-gameplay-2026-11-27.md) now exercises clustered movement, native boss AI/hits, chat rendering and native fanout in a disposable process. Two ABBA series verified identical workloads and reduced chat-render allocation by 43.8%. Movement fanout accounts for about 96% of measured process allocation; prioritize allocation stacks and a compatible TerrariaRuntime send-hook fast path over adding workers to the native simulation. This closes the initial mixed synthetic profiling milestone only, not the P0 full-tick, joined-client or sustained TCP qualification gates.

**27 November P1 integration round:** TerrariaRuntime 1.1.6 provides an allocation-free observed `SendBytesFast` path with legacy compatibility and instance-owned native send callbacks. Vega migrated movement interest and metrics to it; the 255-player ABBA reduced allocation by 69.5% overall and 71.8% in movement fanout with identical sends and damage. FakeProvider now supports explicit, authoritative SSC-backed virtual chest item transfers and opt-in session/revision-bound virtual sign edit requests. Custom NPC native shop decoding and SSC inventory sync are tested headlessly, with revision-cached catalog packets. `.trschem` placement now restores native chests/signs and six supported tile entity types with rollback. Typed REST message/kick actions revalidate exact player sessions, and dynamic plugin retirement is bounded across cancellation, non-cooperative shutdown and failed activation; separate loaded plugin soak exercised 32 retirements/160 faults. These are implementation and synthetic qualification milestones, not stock-client UI, production migration or full schematic world boot certification.

The expanded round also verifies native virtual item-frame metadata creation/update/removal per session without registering the frames in the canonical tile-entity world; exact native client decoder and render-sink fixtures pass. `sandbox create-schematic` stages a supported region into a copied base `.wld` in a dedicated builder process, saves the template before catalog publication and uses that template for ordinary sandbox startup/reset. Actual native startup, reset and reload/export confirmed a chest, sign, item frame and NPC; both source files remained unchanged. The SDK now accepts an optional exact API-generation declaration (currently generation 1); incompatible declarations fail before plugin code activation and do not replace the active plugin. This is a deliberate development compatibility boundary, not a stable binary ABI declaration.

Paced [mixed TCP qualification](performance-mixed-gameplay-2026-11-27.md) now verifies every one of 255 passive loopback streams and all native send callbacks (6,859,245 over 600 ticks). Ordered TCP was 6.80× faster than native TCP in the dense clustered scenario; independent atomic budget reservations improved Ordered elapsed a further ~2.8%. Both still exceed the 60 Hz budget. Paged versus stock on the same loaded medium world saved ~76 MiB private memory; defaults remain unchanged.

**Next P0/P1 round:** [TerrariaRuntime 1.1.7 borrowed packets and actual TShock comparison](performance-native-borrowed-2026-11-27.md) removes eight per-recipient serialization materializations for explicitly capable synchronous-copy sinks while retaining the mutable-hook/unknown-socket path. 255-player recording allocation fell from ~1.10 GB to 156 MB; the actual TShock 6.2.1 (Terraria 1.4.5.8) server and Vega 1.4.6.1 produced exactly the same 6.86 million output callbacks and bytes in the 600-tick workload. The different game versions and absence of inbound stock-client traffic are explicit limitations. Native SSC now has actual character adapter/store lifecycle cases for primary authority on/off, account login/logout, same-slot reconnect and all four sandbox transfer modes; actual stock-client handshakes are still required.

FakeProvider added exact-session native sign presentation/refresh, read-only weapon rack, display doll, hat rack and food platter projection with native decoder tests; the existing explicit SSC inventory transfer remains, while stock chest drag/drop requires transactional GUI qualification. Native NPC actors now provide revisioned dialog/choice graphs via chat/plugin surfaces, session-bound item hand-in quests, exact-item alternative currency, and horizontal patrol/guarded behavior events. Native schematic placement supports all eight v1 tile entity kinds and live dropped world items; native persistent bootstrap/reload also verified pylons and sensors. Dropped items are ephemeral and rejected when building `.wld` templates, and v1's 40-slot limit explicitly rejects expanded chest capture rather than truncating. REST now provides six paginated admin catalogs and 23 typed mutation routes under a separate admin credential (existing maintenance tokens are not elevated). SDK activation resources and native entry/pre-reset events are guarded and scoped to plugin lifetime. These are tested native/application capabilities, not graphical-client or complete TShock behavioral certification.

| Priority | Phase | Work and acceptance criteria |
| --- | --- | --- |
| P0 | 25 / 27 | Actual native character-adapter integration now exercises primary authority on/off, login/logout, reused slot and four admitted sandbox transfer modes with real store/tickets and native sync packets. Complete ordinary stock-client password/passwordless joins, client-visible character restore, reconnect and direct transfers across SSC modes; native in-process tests are not handshakes or graphical client qualification. |
| P0 | 25 / 27 | Finish semantic packet and command branch review against current native 1.4.6.1. Prioritize normal gameplay outcomes, persistent restrictions, regions, containers, item operations, lifecycle cleanup, and correction/resynchronization. Native passthrough is valid where it satisfies the required behavior. |
| P0 for existing servers | 19 / 27 | Legacy Vega primary SSC migration and current pinned TShock SQLite account/BCrypt, 350-slot SSC, active ban, region, warp and text/CIDR whitelist imports are implemented and tested (see [migration guide](migration.md)). Groups use explicit mappings to reviewed existing Vega roles. Remaining: older layouts/source schemas, automated permission mapping if demanded, external providers, complete real deployment migration and live qualification. |
| P0 | 18 / 27 | Qualify clean deployment with representative existing worlds, Windows/Linux, real clients and sustained load. Measure p50/p95/p99 ticks, GC pauses, private memory, throughput and queue age at 32/100/200–255 players. Existing loopback and save benchmarks are not a substitute. |
| P1 | 9 / 16 / 19 / 24 / 25 | Typed REST now covers account/group/region/ban/mute and sandbox lifecycle under a separate admin token; additional typed sandbox creation/reset/destruction are console operations, arbitrary command execution remains intentionally absent. Live whitelist/warp and stock-client operator qualification remain. FakeProvider provides explicit authoritative native chest transfers, native sign presentation/edit intents and five read-only native display entity types; transactional native chest GUI, interactive display entities, and graphical client qualification remain. `.trschem` supports all eight v1 typed entities; expanded chest/nonstandard frames, broader persistence and ephemeral dropped-item world templates remain limited by format/native save semantics. |
| P1 | 26 | Current SDK command/join/leave/task guard, rolling breaker, timeout-bounded retirement, failed-activation handling and telemetry are implemented. Independent loaded-plugin fault/reconnect soak and non-cooperative cancellation/stop regressions pass. Exact declared API-generation preflight, guarded entry/pre-reset events and activation-owned resources are implemented, while a stable API/ABI declaration and compatibility baseline await a supported release. Longer production soak and ongoing extension callback audits remain. |
| P1, evidence-dependent | 11 / 22 / 27 | 1.1.7 removes recipient serialization copies for explicit synchronous-copy sinks and provides read-only send hooks; opt-in deferred writer wakes reduce measured socket write attempts by ~74% in same-binary ABBA. A 255-peer 1,800-tick mixed output trial completed in 30.07 s with 98.1% of busy ticks meeting cadence; not a full-world or real-client TPS guarantee. Stock/paged comparison and separate actual TShock output workload now exist. Continue native/ingress/scene profiling, background pure detached work and per-world concurrency; preserve native mutation authority and choose transport defaults only after joined-client/WAN/latency qualification. |
| P2 | 23 | NativeAOT for a future fixed-function operational tool only if measurement justifies it. CoreCLR retains hot-loaded plugins. No server rewrite or standalone worker executable merely to check a roadmap box. |
| Deferred research | 11 / 17 | Memory-mapped/chunked storage or single-process multiple mutable vanilla worlds. Per-world processes provide the isolation required today; paged tiles continue as a measured optional alternative, not a proven general speedup. |

## 27 November follow-up

The [functional parity audit](tshock-parity-2026-11-27.md) checks official
TShock 6.2.1, all 95 command registrations, 48 TSAPI collections, 21 TShock
domain hooks and 53 typed gameplay handler lists against the current native
Vega deployment and ordinary plugin SDK. It identifies specific remaining
P0/P1 implementation gaps; the 93/95 command names are not semantic parity.
The implementation follow-up records the newly closed group/region/account/ban,
content-restriction and world-event subbranches, player action APIs, activity
notifications, immutable region reads, and native tick/save completion hooks.
The pinned comparison is retained rather than silently redefining the baseline.

Current-schema migration now supports TShock SQLite **and MySQL** sources for
accounts, bans, regions, warps and SSC, with provider-neutral transactional Vega
account/ban/region destination imports into SQLite, MySQL or PostgreSQL. See
[migration](migration.md) for separate schema/backup and group-mapping rules.
Legacy source schema variations, automatic permission mapping, third-party data
and stock-client restoration still require qualification.

The SDK adds credential-free identity and effective permission reads for exact
sessions, public current-world warp snapshots, and TerrariaRuntime 1.1.8's
completed NPC strike notification with detached native results. The latter
is an observation of positive native damage, not a mutable damage policy.

The MySQL 8.4 / PostgreSQL 17 CI contract job passed all five cases, including
SELECT-only TShock MySQL source to SQLite and to a separate MySQL destination,
transaction/rollback and dump contracts for both external providers. The first
run caught MySqlConnector's implicit CHAR(36) GUID decoding; Vega now explicitly
uses text identifiers, including bans, temporary roles and mutes.

Native 1.1.8 integration: 900 local test cases passed (five external-provider
cases skipped locally and exercised separately in CI); deployment smoke passed
for REST, schemas, Ordered transport, sandbox isolation and parity commands.
A 600-update full-world, 255-peer synthetic loopback fixture with deferred
producer batching (2 ms) completed in 9.437 s, 25,500 movements, 5,100 boss hits
(71,400 damage), 1,275 messages, 8,261,324 completion callbacks and 144,253,282
bytes with all peer hashes verified. Busy p95 was 19.260 ms with 139 cadence
budget overruns: 63.58 average updates/s is not stable 60 TPS or stock-client
qualification. The 1,800-update follow-up completed in 28.950 s (62.18 average
updates/s), 76,500 movements, 15,300 boss hits (214,200 damage), 3,825 messages,
24,821,029 callbacks and 433,669,826 bytes with all 255 stream hashes verified.
Its busy p95 was 19.435 ms and 503/1,800 updates exceeded the 16.667 ms budget.
Raw local evidence: `artifacts/mysql-migration-2026-11-27/`.

**Follow-up:** [Ordered callback burst and gameplay foundation](performance-completion-burst-2026-11-27.md).
SQLite, MySQL and PostgreSQL now use schema v3 with idempotent authenticated
world-residence checkpoints. `/playtime` and `context.Statistics` expose live
and persisted totals; AFK classification and player/boss historical rankings
remain open. `/platform`, `/platforms`, SDK connection summaries and REST
aggregation report only observed client platform metadata. The SDK's bounded
CPU work lane and true background I/O prefix preserve retirement semantics.
Native PvE and PvP participant eligibility is enforced; broader native combat
authority and stock-client combat qualification remain open.
Final 1.1.9 Windows tests passed 880 runtime and 33 architecture cases; eight
external-dependent cases were skipped locally. Real MySQL 8.4 / PostgreSQL 17
CI plus SQLite passed all 10 provider/migration cases. Native deployment smoke
passed. Controlled 255-peer completion-burst ABBA and the 1,800-tick soak are
recorded in the linked report; the latter still has 235 over-budget updates.


- **P0 native gameplay load:** real native world counter now verifies a 255-slot
  mixed fixture, including boss AI, movement, chat and native background traffic.
  Ordered loopback completed all 1,800 updates and 24.8 million sends with exact
  per-peer hashes, but averaged 39.15 TPS. An opt-in deferred-wake producer-block
  pipeline removes the second coalescing copy: same-binary 600-tick ABBA averaged
  60.48 verified updates/s versus 43.71 before, CPU −37.2%, writes −21.6%.
  The final integrated 1,800-update run averaged 64.35 updates/s (27.97 s),
  with p95 18.19 ms and 240 of 1,800 busy ticks over budget. Stable 60 TPS
  and ingress/real-client/WAN qualification remain open. Previous paced
  output-only results are not full TPS.
- **P0 SSC:** `scripts/New-StockSscQualification.ps1` prepares four isolated
  stock Terraria 1.4.6.1 cases (password/passwordless × SSC on/off); server
  readiness passed in all cases. [The acceptance guide](stock-client-ssc-qualification.md)
  records the still-pending actual login, character restore, disconnect/reconnect
  and trusted-transfer checks. No graphical client pass is asserted.
- **P1 FakeProvider:** managed native 40-slot chest GUI now sends real metadata,
  opens, refreshes and closes through the stock client decoder. Plugin-approved
  balanced single-slot transfers use exact SSC session ownership; unsupported
  operations close/correct, with managed inventory remaining authoritative until
  disconnect. Item frames and weapon racks now support plugin-armed deposit
  into an empty virtual entity with the same exact-session inventory authority;
  native inventory commit and stock client metadata decoder are tested. Ordinary
  drag timing, sorting, multi-slot interactions, stock display removal/swap,
  mannequin/hat-rack inventories and graphical behavior remain open.
- **P1 NPC:** durable world/account quest ledger and named wallet, injectable
  economy provider, stable operation receipts, exact-session SSC checkpoint and
  pending reconciliation are implemented. Dynamic provider registration and
  cross-store recovery automation remain open; no cross-store atomicity claim.
- **P1 REST:** curated `.wld` templates, managed sandbox create/reset/destroy,
  revision + creation identity confirmations and isolated source checks tested.
  Arbitrary paths and externally owned world destruction remain intentionally absent.
- **P1 worker isolation:** runtime tile runs are detached before worker encoding;
  native file format 329 snapshot save/reload is fixed and verified. Owner-thread
  capture fell 45.9% in same-world ABBA (631.8 → 341.65 ms). Full tile scan and
  canonical hash still run under world ownership; fast liquid restoration is off.
- **P1 schemas/SDK:** `.trschem` v2 adds expanded native chest capacity through
  200 slots, preserving byte-compatible v1 for ordinary chests. Native sandbox
  template creation, save, reload/export and reset verified slots 0, 79 and 199
  and unchanged input fingerprints. Canonical expanded-chest ingress now
  checks the exact active native chest's current capacity, with no client
  resize authority. Graphical qualification, expanded virtual GUI and
  ephemeral dropped-item template persistence remain distinct limits.
  The new account NPC and managed chest contracts extend the plugin SDK, while
  broader compatibility/event qualification remains open. See
  [performance evidence](performance-2026-11-26.md).

Final integrated Windows verification: Release solution build, 872 tests
passed (two external PostgreSQL/MySQL cases skipped without local connection
settings), native deployment smoke with producer batching and sandbox isolation,
and all four isolated stock-SSC server readiness cases passed. The actual
graphical client acceptance fields remain `pending-manual`.

## Stale assumptions to remove

- Protocol 326 / Terraria 1.4.5.8 is historical comparison evidence, not the current production protocol. Preserve the pinned ledger, and independently qualify 329.
- The original 232 B/packet parser baseline is superseded by the 4.0.1 measurement (~0.032 B/ordinary PlayerUpdate, including harness allocations). A fixed pool for every packet is no longer justified by that old allocation figure. Measure heavy work and tail latency rather than queueing the cheap path by default.
- Inventory/loadout are no longer unassigned ledger decisions. Full joined-client semantics are still open.
- Native SSC is no longer an entirely unimplemented feature or a permanently disabled mode. Its real-client qualification and data migration are still mandatory for a production SSC migration.
- Closed implementation phases should not be reopened simply because production qualification remains pending. Conversely, closed implementation tasks do not establish full compatibility.

## Recommendation

Treat the current code as an advanced deployment candidate for a fresh/test world. Do not advertise a drop-in production replacement for an existing TShock installation until the relevant P0 gates pass. Compare on the same protocol/runtime/world and equivalent policy settings, and record operator-visible differences rather than reimplementing TShock internals by default. See [performance measurements](performance-2026-11-26.md).
