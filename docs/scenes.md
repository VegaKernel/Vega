# Observer-scoped scenes

Vega's scene runtime is the replacement boundary for the useful parts of TerraZ
FakeProvider. It composes immutable tile overlays for a `WorldId` and observer without
replacing `Terraria.Main.tile`. Terraria remains the owner of canonical world state.

Every scene is created through an `ISceneLease` with an explicit owner. Disposing the
lease removes all of its overlays and schedules restoration of the affected regions.
Overlays have stable creation order, an explicit numeric order and either a global or
personal observer scope. A later overlay wins when orders are equal; equal-order
overlaps are reported as conflicts in Operations instead of depending on hook order.

The bundled `Vega.Modules.Scenes` consumers cover the audited legacy use cases:

- WorldEdit clipboard and mask previews can be moved or resized by updating existing
  overlay identities and are cleared with the player/session-owned lease;
- Region-Vision uses a personal border overlay, while inbound interaction with its
  phantom tiles is rejected by the same observer decision;
- lobby visuals support global and personal immutable UI/animation frames;
- FakeProviderClustering owns player, NPC and projectile visibility leases.

Outbound tile squares are composed per recipient. Native section delivery is followed
by bounded overlay redraws. Entity packets use the same visibility layer for targeted
and broadcast delivery, and incoming NPC/projectile interactions with hidden entities
are rejected through typed Multiplicity views. The redraw queue is bounded to 256
regions and packets to 64x64 tile chunks. Adapter failures, dirty regions, owners,
conflicts and visibility layers are visible in Operations/TUI.

## Performance baseline

Run `dotnet run --project benchmarks/Vega.Scene.Benchmarks -c Release`. On the x64
.NET 10.0.11 development host, the 64-overlay synthetic baseline measured:

| Workload | Result | Managed allocation |
| --- | ---: | ---: |
| lookup among 64 overlays, 250,000 operations | 1.18 us/op | 56 B total |
| compose a 64x64 region | 2.14 ms total | 56 B total |
| replace 200 observers, 1,000 times | 27.1 us/op | 13.0 MB total |
| create 1,000 personal 32x32 overlays sharing immutable tile data | 2.69 us/op | 1.36 MB total |

These are regression baselines, not hardware-independent guarantees. The harness now
also executes WorldEdit preview movement with a mask and a 200-observer/300-entity lobby
composition churn workload; CI runs it as a regression gate and reports elapsed time plus
managed allocation for every workload. Socket throughput under real clients remains a
deployment-qualification measurement rather than a scene-runtime implementation gate.
The hot tile lookup performs no per-call LINQ materialization. Monitoring takes sampled
immutable snapshots and never performs per-tick cross-process or full-world work.

Single-process multi-world virtualization remains deliberately outside this API. Phase
17 gives each live world a process; a future in-process experiment must prove isolation
of Terraria globals before it can change that decision.
