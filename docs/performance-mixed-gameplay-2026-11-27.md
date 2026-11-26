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
