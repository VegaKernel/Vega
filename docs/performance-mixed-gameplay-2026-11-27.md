# 255-player mixed native gameplay benchmark — 27 November 2026

The disposable benchmark loads the pinned TerrariaRuntime 1.1.5 native server, creates 255 synthetic player slots on its game thread, runs Eye of Cthulhu AI and native `StrikeNPC`, updates player positions and invokes normal native movement/NPC broadcasts. It also runs the real Vega chat pipeline and native chat delivery. No client connections or inbound packets are generated. Recording sockets count native output; this is a serialization/fanout benchmark, not TCP, full-world tick, packet ingress, or authentication qualification. Boss life is artificially extended and all players are deliberately close together. The simulation loop is unpaced: 1,800 measured ticks represent 30 simulated seconds, not 30 seconds of elapsed time.

## Reproduce

From the repository root, on Windows (use a clean local world copy as input):

```powershell
dotnet build benchmarks/Vega.MixedGameplay.Benchmarks -c Release
dotnet publish src/Vega.Runtime -c Release -r win-x64 --self-contained false -o artifacts/mixed-gameplay-publish
.\scripts\Measure-MixedGameplay.ps1 -ServerRoot artifacts/mixed-gameplay-publish -WorldFile C:/path/to/test.wld -OutputDirectory artifacts/mixed-gameplay-abba -Ticks 1800
```

The runner starts fresh loopback-only disposable processes in ABBA order, enables the test plugin explicitly, and saves JSON and logs from each run. The plugin is not included in the normal server deployment. The baseline uses the pre-change mention renderer; the optimized path uses the production renderer. The movement, combat and delivery paths are the same in both modes. Each run warms up for 120 ticks. The fixture checks exact movement fanout, positive damage and chat recipient count.

## Results

Windows, AMD Ryzen 9 5950X (16 cores / 32 logical), .NET 11.0.100, Release. Two complete ABBA series plus a 120-tick pilot passed. The final instrumented series is below; raw output is in local `artifacts/mixed-gameplay-abba-final/`.

Every measured run produced the same counts:

| Work | Per run |
|---|---:|
| Synthetic players | 255 |
| Native movement broadcasts | 76,500 |
| Movement recipient sends | 19,431,000 |
| Native NPC strikes / damage | 15,300 / 214,200 |
| Chat messages / messages with mentions | 3,825 / 765 |
| Chat rendering recipients | 975,375 |
| All socket sends / serialized bytes | 20,576,460 / 378,168,315 |

| Metric (ms unless otherwise noted) | Legacy A1 | Optimized B1 | Optimized B2 | Legacy A2 |
|---|---:|---:|---:|---:|
| Measured elapsed | 2012.41 | 1875.95 | 1890.70 | 1974.73 |
| Process CPU | 2828.13 | 2703.13 | 2750.00 | 2828.13 |
| Partial tick p95 | 1.873 | 1.571 | 1.619 | 1.755 |
| Partial tick p99 | 3.572 | 3.672 | 3.416 | 3.498 |
| Mean movement stage | 0.823 | 0.796 | 0.806 | 0.813 |
| Mean boss/combat stage | 0.133 | 0.130 | 0.129 | 0.131 |
| Mean chat rendering stage | 0.0522 | 0.0312 | 0.0313 | 0.0484 |
| Mean native chat delivery stage | 0.109 | 0.084 | 0.083 | 0.103 |
| GC pauses (sum) | 54.60 | 52.26 | 51.75 | 52.16 |
| Gen0 collections | 217 | 216 | 216 | 217 |
| Process managed allocations (GB decimal) | 3.640 | 3.609 | 3.609 | 3.640 |
| Working set (MiB) | 823.7 | 822.4 | 823.7 | 824.4 |

Mean chat stage duration fell 38%; exact chat allocation fell from 71,310,240 to 40,098,240 bytes (43.8%, or 32 bytes per recipient rendering). Whole-run elapsed improved about 5.5%; p95 fell 12%, but p99 was unchanged. The smaller whole-run differences also include JIT, GC and run-to-run scheduling differences and are not proof of the same percentage improvement on a live server.

### Identified bottleneck

| Game-thread stage | Legacy allocation | Optimized allocation |
|---|---:|---:|
| Movement fanout | 3,463,308,000 B | 3,463,308,000 B |
| Combat and NPC sync | ~42,041,440 B | ~42,041,440 B |
| Chat rendering | 71,310,240 B | 40,098,240 B |
| Native chat delivery | 62,742,240 B | 62,742,240 B |

Movement accounts for about 96% of optimized process allocation and 76% of measured partial-tick time, or about 178 bytes per movement recipient send. The native output hook currently creates a `SendBytesEventArgs` per send. This is a concrete candidate for a compatible allocation-free observer/policy path in TerrariaRuntime, but an allocation-stack profile is needed to attribute the remaining bytes. Do not move mutable Terraria player/NPC state to workers. Preserve native callback, ordering and cancel semantics; perform the same benchmark after each change. All players are near each other here, so distant-interest throttling cannot reduce this fanout without affecting ordinary gameplay.

The separate existing 255-peer 30-second TCP tests in [previous measurements](performance-2026-11-26.md) verify bytes and callbacks on real loopback sockets. The present benchmark intentionally excludes these costs. Next qualification gates are a paced mixed native/TCP fixture with queue-age and callback latency, true stock-client joins/SSC and real-world full-tick measurements, followed by equivalent TShock comparison on the same release, world and settings.

## Native send-hook optimization (TerrariaRuntime 1.1.6)

An isolated source worktree at Vega `43cf580c` was used for an additional ABBA comparison. Both server builds use the already-optimized mention renderer and identical benchmark assembly (including callback identity counts); the candidate differs only in the TerrariaRuntime version and Vega's migration from legacy `SendBytes` to `SendBytesFast`. All 20,576,460 sends, 378,168,315 output bytes, 76,500 movement broadcasts, 15,300 strikes, 214,200 damage and 3,825 chat messages match in all four runs.

| Metric | 1.1.5 A1 | 1.1.6 B1 | 1.1.6 B2 | 1.1.5 A2 |
|---|---:|---:|---:|---:|
| Elapsed (ms) | 2077.69 | 1679.33 | 1671.94 | 3239.56 |
| Process CPU (ms) | 2937.50 | 2484.38 | 2484.38 | 4609.38 |
| Partial tick p95 (ms) | 1.706 | 1.744 | 2.168 | 2.986 |
| Partial tick p99 (ms) | 4.701 | 3.307 | 3.157 | 3.535 |
| Managed process allocation (bytes) | 3,608,991,664 | 1,100,052,888 | 1,100,052,904 | 3,609,432,088 |
| Movement allocation (bytes) | 3,463,308,000 | 976,140,000 | 976,140,000 | 3,463,308,000 |
| Gen0 collections | 216 | 66 | 66 | 216 |
| GC pause sum (ms) | 99.17 | 30.41 | 28.39 | 230.09 |
| Callback identity changes | 20,576,460 | 1,434,375 | 1,434,375 | 20,576,460 |

The important deterministic result is **69.5% less total managed allocation** and **71.8% less movement allocation** (128 bytes removed per movement recipient send), with unchanged gameplay/fanout counters. The unobserved and observed fast-hook smoke each execute 1,000,000 sends with zero hook allocations, while legacy cancellation and exact callback behavior are tested. Runtime elapsed times are noisy because other local P1 project builds ran during the comparison, notably A2; do not infer a specific production throughput improvement from these timings. Raw files: `artifacts/native-hook-abba/`.

The remaining movement allocation is about 50 bytes per recipient. The native `orig_SendData` IL calls `MemoryStream.ToArray()` for each eligible recipient, so sharing immutable serialized buffers is the next evidence-backed investigation. Do not blindly share mutable arrays with legacy hooks or asynchronous transports; first establish buffer ownership, per-recipient mutation and completion semantics. Native chat module sends also still have callback allocations outside the optimized `NetMessage.SendPacket` path.

## Paced mixed gameplay with actual localhost TCP

The benchmark now also supports `-TransportModes @('native','ordered')` (use a real PowerShell array). Each synthetic native player sends only to a passive receiver accepted by a private ephemeral loopback listener; no client packets, external targets or real player connections are involved. The fixture offers a 60 Hz cadence without catch-up bursts, counts original native callbacks, and verifies each of the 255 received byte streams by length and cumulative hash after warmup/final drain. Process CPU/memory include the local readers and validation instrumentation. Ordered uses the default bounded queue, batch 16, game-thread callback pump and sampled queue age.

```powershell
.\scripts\Measure-MixedGameplay.ps1 -ServerRoot artifacts/mixed-loopback-publish -WorldFile C:/path/to/test.wld -OutputDirectory artifacts/mixed-loopback-600 -Ticks 600 -Modes @('optimized') -TransportModes @('native','ordered')
```

Both 120-tick pilots and 600-tick runs passed byte/callback/hash verification; both use TerrariaRuntime 1.1.6. The 600 measured ticks represent 10 simulated seconds, following 120 warmup ticks. All 6,859,245 callbacks and 126,034,260 bytes matched across both modes (255 validated streams).

| Metric | Native TCP | Ordered TCP |
|---|---:|---:|
| Elapsed (s) | 86.898 | 12.778 |
| Process CPU (s) | 469.406 | 200.922 |
| Managed allocation (MB decimal) | 1824.29 | 1298.11 |
| Partial tick p95 (ms) | 155.096 | 23.718 |
| Partial tick p99 (ms) | 165.133 | 25.293 |
| Missed 60 Hz cadence | 600/600 | 570/600 |
| Sampled max retained entries | n/a | 8178 |
| Sampled max send / completion age (ms) | n/a | 6.720 / 20.716 |
| Final drain (ms) | 1.92 | 26.76 |

Ordered is 6.80× faster in this concentrated small-packet workload and consumes 57% less CPU, but neither meets a 16.67 ms tick budget. This is a deliberately dense output-fanout fixture, not actual stock-client TPS or WAN qualification. It establishes a transport bottleneck after optimizing native serialization hooks. The native transport remains the default pending additional throughput/latency and joined-client verification. Baseline raw artifacts: `artifacts/mixed-loopback-600/`.

### Shared queue-capacity counter A/B

The per-world ordered outbound budget now reserves entry credits and retained byte capacity with independent atomic compare/exchange operations instead of serializing all producer and consumer threads through one monitor. Each individual cap is exact; an unsuccessful byte reservation releases its entry immediately. Diagnostic snapshots may sample the two counters at different instants. Requests remain ordered and generation-bound by the per-connection queue; capacity rejections still close only the affected connection.

Isolated-source ABBA with 600 paced mixed ticks, Ordered batch 16, 255 passive receivers, otherwise identical builds:

| Metric | Monitor A1 | Atomic B1 | Atomic B2 | Monitor A2 |
|---|---:|---:|---:|---:|
| Elapsed (s) | 13.360 | 12.972 | 12.716 | 13.076 |
| CPU (s) | 215.594 | 211.609 | 202.969 | 214.906 |
| Partial tick p95 (ms) | 24.516 | 24.344 | 23.608 | 24.206 |
| Partial tick p99 (ms) | 25.940 | 25.153 | 24.997 | 25.450 |
| Missed cadence | 570 | 567 | 561 | 576 |

All four runs matched 6,859,245 callbacks, 126,034,260 bytes and 255 stream hashes. Mean elapsed decreased by about 2.8% and mean CPU by 3.7%; p95 improved only about 1.6%. The candidate still exceeds the target tick budget, and one run recorded a higher queue peak (13,818 entries rather than 6,575–7,057). This is a modest contention reduction, not a cure for dense broadcast fanout. Raw output: `artifacts/budget-abba/`.

Increasing the coalescing ceiling from 16 to 64 requests in a separate experimental build did not improve a 240-tick ABBA (mean elapsed 4.94 s versus 4.96 s, mean p95 22.14 versus 23.27 ms; all 2,744,055 callbacks, 50,424,975 bytes and per-peer hashes matched). Local build/smoke activity overlapped the first run, so time/CPU are not definitive; there is no evidence to adopt the larger batch. The production ceiling/default stays 16. Raw output: `artifacts/batch-abba/`.

### Stock versus paged tile storage on the same mixed scenario

Four isolated 1.1.6 runs used `VEGA_PAGED_TILES=off/on/on/off`, the same loaded medium world (8401×2401 native tile grid), 255 synthetic players, 1,800 unpaced recording ticks and the same binary. Startup logs confirmed that both B runs selected paged storage. Every run produced 20,576,460 sends, 378,168,315 bytes and 214,200 boss damage.

| Metric | Stock A1 | Paged B1 | Paged B2 | Stock A2 |
|---|---:|---:|---:|---:|
| Private memory (MiB) | 822.17 | 745.73 | 746.14 | 822.56 |
| Working set (MiB) | 824.00 | 752.66 | 753.33 | 823.52 |
| Managed trial allocation (GB decimal) | 1.100 | 1.100 | 1.100 | 1.100 |
| Elapsed (s) | 1.810 | 1.461 | 1.498 | 1.677 |

Paged storage saved roughly 76.4 MiB of private memory (9.3%) in this loaded fixture. It remains opt-in: elapsed differences can include concurrent local builds, and the mixed benchmark does not exercise full-world tile scans or persistence; earlier native tile scan/save results still apply. Raw output: `artifacts/paged-mixed-abba/`.

Final integrated P1 package: 600 paced Ordered ticks again verified exactly 6,859,245 callbacks, 126,034,260 bytes and all 255 stream hashes, with 12.349 s elapsed, 189.188 s process CPU, 1,284.64 MB allocations, p95 22.775 ms, p99 23.486 ms and 532/600 cadence misses. Sampled send/completion ages were 6.090/22.870 ms. This is an integration check, not another controlled A/B attribution. Raw output: `artifacts/p1-integrated-mixed/`. The full test suite passed 781 runtime and 33 architecture tests; two external database tests skipped. Clean deployment smoke passed native REST, schematic export, Ordered TCP listener/connection lifecycle and isolated sandbox/shared admission/graceful shutdown.
