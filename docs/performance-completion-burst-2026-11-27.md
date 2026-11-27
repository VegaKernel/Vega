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

The SDK adds bounded `Tasks.TryRunCpu` and moves the synchronous portion of
`RunIo` off the game thread. The native PvE resolver now checks current native
participants and uses TerrariaRuntime's validation boundary; PvP checks
current-world participants. TerrariaRuntime 1.1.9 adds detached native strike
source observations, not proof of the attacker or damage authorization. Full
weapon/damage authority and live combat semantics remain separate work.
