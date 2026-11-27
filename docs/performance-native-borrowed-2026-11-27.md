# Native borrowed packets and TShock comparison — 27 November 2026

This is a follow-up to the [255-player mixed workload](performance-mixed-gameplay-2026-11-27.md). All fixtures are game-side, in a disposable server process, with fixed synthetic player slots. They do not generate inbound client packets or join a public listener. A passive loopback mode validates every byte stream and original completion callback. This distinguishes native game/output costs from a full stock-client login, packet-validation, SSC and world-tick test.

## TerrariaRuntime 1.1.7

The actual native `SendData` recipient paths formerly called `MemoryStream.ToArray()` eight times in different control-flow branches, potentially one call per recipient of a broadcast. The patch replaces these exact sites with a synchronous borrowed stream path. No packet is shared mutably; a recipient receives the current serialized bytes, including native recipient-dependent changes. An explicitly capable socket (`IBorrowedPacketSocket`) must consume/copy the span before returning; unknown sockets and mutable hook subscribers retain exact-length independent arrays. Generated mutable `SendPacket` subscribers also see an owned copy inside the captured-subscriber branch. Vega metrics/interest hooks use `SendBytesReadOnly`, and the ordered queue copies directly from the span once into its own buffer.

The runtime smoke exercises real generated `SendPacket` hooks, independent mutation, unknown-socket ownership, complete native callback and 1,000,000 borrowed native sends with zero managed allocations. The cardinality check rejects a changed native Patcher layout rather than silently applying an incomplete patch.

### 1,800-tick recording fixture

The 1.1.7 isolated publish completed the identical 76,500 movement broadcasts, 15,300 strikes / 214,200 damage, 3,825 messages, 20,576,460 sends and 378,168,315 output bytes. 19,601,085 sends used the borrowed path. Previous 1.1.6 results are from the [same fixture](performance-mixed-gameplay-2026-11-27.md).

| Metric | 1.1.6 | 1.1.7 |
|---|---:|---:|
| Managed process allocations | ~1,100,052,888 B | 156,189,520 B |
| Movement-stage allocations | 976,140,000 B | 43,452,000 B |
| Gen0 collections | 66 | 9 |
| GC pause sum | 28–30 ms | 8.99 ms |

The movement stage allocates 95.5% less than 1.1.6 (932,688,000 fewer bytes, 48 per movement recipient); the whole recording scenario allocates 85.8% less. Compared with the original 1.1.5 run this is roughly a 95.7% reduction in total allocation. Timing is not asserted as an equivalent throughput gain: benchmark scheduling and local parallel builds differed. Raw output: `artifacts/borrowed-recording/`.

### 600 paced Ordered ticks

The real loopback fixture also passed exact 6,859,245 callbacks, 126,034,260 bytes and 255 per-peer stream hashes. 1.1.7 recorded 6,534,120 borrowed sends, 983,428,528 managed bytes, p95 22.080 ms, 12.091 s elapsed and 552/600 cadence misses. The previous integrated 1.1.6 run allocated 1,284,643,120 B and took 12.349 s; this cross-check is not controlled timing attribution. The remaining bottleneck is socket delivery and queue scheduling, rather than native serialization allocations alone. Raw output: `artifacts/borrowed-ordered/`.

## Actual TShock server comparison

The official [TShock 6.2.1 release](https://github.com/Pryaxis/TShock/releases/tag/v6.2.1) supports Terraria **1.4.5.8**, while Vega uses Terraria **1.4.6.1**; this is intentionally a cross-version output workload, not a same-protocol or complete server-parity claim. The test-only plugin is built against the extracted official release and runs on the actual TShock server. It submits identical game-side movement, boss AI/hits and already-rendered chat to the native broadcast methods (it does not pretend TShock has Vega's mention renderer). It uses the same passive peer fixture and validation as Vega, a copy of the identical input `.wld`, and a protected loopback-only process.

```powershell
dotnet build benchmarks/Vega.TShockComparison.Benchmarks -c Release -p:TShockRoot=C:/path/to/extracted/official/tshock
.\scripts\Measure-TShockMixedGameplay.ps1 -TShockRoot C:/path/to/extracted/official/tshock -WorldFile C:/path/to/test.wld -OutputDirectory artifacts/tshock-mixed-600 -Ticks 600
```

The measured work matched exactly: 255 synthetic player slots, 25,500 movements, 5,100 strikes/71,400 damage, 1,275 messages, 6,859,245 completion callbacks, 126,034,260 serialized bytes and all 255 received stream hashes. TShock's 120-tick pilot and 600-tick test passed. Prior Vega 1.1.6 and 1.1.7 samples use the same scenario; local builds overlapped part of the TShock run, so timing comparison is indicative, not a controlled hardware/JIT/runtime comparison.

| Metric, 600 measured ticks | TShock 6.2.1 native | Vega 1.1.6 native | Vega 1.1.7 Ordered |
|---|---:|---:|---:|
| Elapsed s | 88.766 | 86.898 | 12.091 |
| Process CPU s | 284.313 | 469.406 | 194.594 |
| Managed allocations GB | 6.737 | 1.824 | 0.983 |
| Partial tick p95 ms | 262.288 | 155.096 | 22.080 |
| Partial tick p99 ms | 295.522 | 165.133 | 24.017 |
| Cadence misses | 600/600 | 600/600 | 552/600 |

The major result is the correctly validated dense-output workload; even Ordered still misses 60 Hz. The TShock process uses a different game version and .NET runtime, and the fixture deliberately bypasses inbound client packet/security/authentication processing. This is not a universal percentage claim, nor full joined-client/TShock compatibility. Raw output: `artifacts/tshock-mixed-pilot2/`, `artifacts/tshock-mixed-600/`.

## Dense-burst writer wake batching

The previous coalescing writer usually woke before the producer could enqueue the next packet for that recipient. The opt-in `orderedDeferredWake` path defers an idle writer's wake only for a bound connection and only on the authoritative game thread. It flushes after a short elapsed window during dense bursts (checked every 256 enqueues), at native tick completion and before the completion pump. Non-deferred sends, unbind, completion, abort and cancellation release the wait immediately; a post-enqueue unbind recheck prevents a lost wake. The per-connection FIFO, retained buffer/entry limits and generation-checked completions remain unchanged. This is a cooperative window, not a hard wall-clock guarantee if the native thread stalls or is blocked. The optional production config remains disabled by default pending joined-client qualification.

Initial 0.5 ms ABBA decreased actual socket write attempts from ~6.4 million to ~2.7 million and p95 busy tick to 15.7–16.2 ms. This exposed an artificial Windows short-sleep overshoot in the paced fixture (elapsed 16.5 s despite only 10–24 busy tick overruns). The corrected pacing yields/spins for the short 60 Hz remainder and includes the tick flush in busy timing. Thus compare the following **same-binary 1 ms** ABBA rather than the old wall timing:

| Metric, 600 ticks | Immediate A1 | Deferred B1 | Deferred B2 | Immediate A2 |
|---|---:|---:|---:|---:|
| Elapsed s | 12.470 | 10.046 | 10.077 | 12.463 |
| Process CPU s | 202.625 | 54.047 | 51.172 | 207.375 |
| Allocation MB decimal | 970.44 | 307.87 | 304.69 | 985.81 |
| Native socket write attempts | 6,325,545 | 1,660,266 | 1,630,977 | 6,429,466 |
| Partial tick p95 ms | 23.396 | 15.382 | 15.880 | 23.339 |
| Partial tick p99 ms | 24.635 | 16.764 | 17.489 | 24.857 |
| Cadence misses | 546 | 9 | 24 | 560 |

All 6,859,245 callbacks and 126,034,260 bytes were exact in every run. The 1 ms batching window decreased mean write attempts by **74.2%**, process CPU by ~74%, and busy p95 by ~33%. Socket attempts are measured at the actual `SendAsync` boundary with diagnostics enabled (not a kernel-level syscall tracer). Raw output: `artifacts/deferred-wake-final-abba/`.

The longer opt-in **2 ms** trial completed 1,800 offered ticks (30 simulated seconds) in **30.070 s**, with 20,576,460 callbacks, 378,168,315 bytes, 255 validated streams, 3,346,973 socket write attempts, 947.23 MB allocations, 157.438 CPU seconds, p95 15.134 ms and p99 17.028 ms. 34 of 1,800 busy ticks exceeded the cadence (98.1% met it). Sampled maximum send and completion age were 14.209 and 12.670 ms, respectively. This is close to 60 Hz for the mixed output fixture, **not confirmation of stable full-world 60 TPS with joined clients**. Raw output: `artifacts/deferred-wake-2ms-1800/`.

Reproduce with a clean server publish and the copied fixture world:

```powershell
.\scripts\Measure-MixedGameplay.ps1 -ServerRoot artifacts/server -WorldFile C:/path/to/test.wld -OutputDirectory artifacts/wake-test -Ticks 1800 -Modes @('optimized') -TransportModes @('ordered') -DeferredWake -DeferredWakeMilliseconds 2
```

For an explicitly experimental deployed world set `useOrderedTcp`, `orderedDeferredWake` to `true` and `orderedDeferredWakeMilliseconds` to 1 or 2 in `config/network.json`. The configured range is 0.25–2 ms; the default remains immediate wakes (and vanilla transport remains the general default). Do not conflate small packet batching with moving mutable Terraria gameplay to workers: native serialization stays on the owning thread; only socket writers consume owned bytes asynchronously.

Final integrated package independently repeated the 2 ms / 1,800-tick trial: 30.082 s elapsed, 20,576,460 callbacks and 378,168,315 bytes with all 255 per-peer hashes, 3,390,321 actual write attempts, p95 15.454 ms, p99 17.259 ms, 38 cadence misses (97.9% met the budget). Integrated tests passed 802 runtime + 33 architecture cases (two external DB cases skipped locally); clean deployment smoke passed loopback REST, native deferred/Ordered listener, schema export and isolated/shared-admission sandbox shutdown. Raw output: `artifacts/p0-p1-final-mixed-1800/` and `artifacts/p0-p1-final-tests.log`.
