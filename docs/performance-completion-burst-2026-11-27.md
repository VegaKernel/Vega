# Ordered callback burst, CPU work and combat/session foundation

The opt-in Ordered TCP transport can drain up to four completed callbacks
from one peer at once before returning to the round-robin pass. FIFO order,
callback count cap, socket-generation validation, and global credit accounting
remain intact. The 2 ms budget is cooperative, checked between bursts; one
burst can finish up to four callbacks after the final check. `orderedCompletionDrainBatch`
defaults to 4 for Ordered and can be set to 1 for the previous path. Vanilla
transport remains the default.

The same-binary 255-peer full native world fixture (600 updates each, deferred
producer batching at 2 ms) gave:

| Trial | Completion batch | Wall (s) | Busy p95 (ms) | Completion p95 (ms) | Busy overruns |
|---|---:|---:|---:|---:|---:|
| A1 | 1 | 10.225 | 20.988 | 1.718 | 290 |
| B1 | 4 | 9.203 | 18.878 | 1.002 | 110 |
| B2 | 4 | 9.525 | 19.809 | 1.068 | 160 |
| A2 | 1 | 11.554 | 24.240 | 2.135 | 488 |

All four preserved each of the 255 loopback byte streams and completion
callbacks; native background sends varied slightly between runs. Parallel
implementation/build activity overlapped this initial series, so overall wall
time is indicative, not a controlled timing attribution. The observed mean
completion-pump p95 fell from 1.926 to 1.035 ms. This does not establish stable
60 TPS or real-client ingress readiness. Raw local evidence:
`artifacts/completion-burst-2026-11-27/`.

The integrated server restored the official 1.1.9 package from NuGet and passed
the native deployment smoke. A second ABBA used the same published binary,
without overlapping local builds:

| Trial | Completion batch | Wall (s) | Busy p95 (ms) | Completion p95 (ms) | Busy overruns |
|---|---:|---:|---:|---:|---:|
| A1 | 1 | 9.157 | 18.634 | 1.515 | 101 |
| B1 | 4 | 7.915 | 16.699 | 0.819 | 32 |
| B2 | 4 | 8.253 | 16.512 | 0.896 | 26 |
| A2 | 1 | 9.237 | 18.680 | 1.592 | 103 |

Mean completion p95 decreased 44.8% (1.553 → 0.857 ms); busy p95 decreased
11.0% (18.657 → 16.606 ms), and average elapsed time decreased 12.1%. All
255 streams and callback totals were validated independently per trial. Native
background traffic varies, so aggregate byte counts across trials are not
expected to be identical. In the longer 1,800-update trial, 255 synthetic
players produced 76,500 movements, 15,300 strikes / 214,200 damage, 3,825 chat
messages, 24,808,414 callbacks and 433,257,342 bytes; all streams verified.
The trial completed in 26.324 s (68.38 average updates/s), busy p95 18.287 ms,
completion p95 1.024 ms, and 235/1,800 busy updates exceeded 16.667 ms. Compared
with the earlier 1.1.8 integrated soak, the overrun count fell from 503 to 235;
the cross-version comparison is indicative. This is still not a stable-60-TPS
claim, nor joined real-client/WAN qualification.

To reproduce, publish the server and benchmark, set `VEGA_BENCH_COMPLETION_BATCH`
to 1 or 4, and run `scripts/Measure-MixedGameplay.ps1` with `-FullWorld`,
`-Modes optimized -TransportModes ordered -DeferredWake -ProducerBatching
-DeferredWakeMilliseconds 2 -Ticks 600` and a disposable copied `.wld`.

The SDK adds bounded `Tasks.TryRunCpu` and moves the synchronous portion of
`RunIo` off the game thread. The native PvE resolver now checks current native
participants and uses TerrariaRuntime's validation boundary; PvP checks
current-world participants. TerrariaRuntime 1.1.9 adds detached native strike
source observations, not proof of the attacker or damage authorization. Full
weapon/damage authority and live combat semantics remain separate work.
