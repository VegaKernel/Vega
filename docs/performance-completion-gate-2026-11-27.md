# Ordered completion gate candidate — 27 November 2026

The candidate separates the completion FIFO lock from the payload ownership/coalescing lock. The
accepted sequence remains owned by the payload gate and the released-entry counter is atomic, so
the producer does not add another atomic increment. The writer/abort/snapshot paths always acquire
payload then completion gate; callbacks remain on the authoritative pump, one at a time with the
existing count/time budgets and exact-generation check.

Run the same fixture on the baseline and candidate builds:

```powershell
dotnet run -c Release --project benchmarks/Vega.NativeTcp.Benchmarks -- --split-gate
```

255 passive localhost peers, 32 ordinary messages per peer per tick, sizes 16/24/32/48 bytes, 180
measured ticks and the benchmark's usual warmup. Ordered batch16, lifetime wake, queue age enabled.
All four ABBA runs validated exactly 1,468,800 original callbacks and 42.02 MiB. No inbound client
packets or remote endpoints are involved. Raw output: `artifacts/network-split-gate-abba/`.

| Metric | Baseline A1 | Candidate B1 | Candidate B2 | Baseline A2 |
| --- | ---: | ---: | ---: | ---: |
| Wall s | 3.035 | 3.063 | 3.044 | 3.022 |
| CPU s | 38.859 | 36.453 | 38.047 | 39.781 |
| Allocation MB | 155.80 | 148.24 | 157.45 | 155.29 |
| Producer p95 ms | 19.360 | 18.069 | 18.085 | 19.211 |
| Callback p95 ms | 22.912 | 21.936 | 23.211 | 21.247 |
| Pump p95 ms | 2.224 | 2.276 | 2.283 | 2.221 |
| Drain p95 ms | 2.001 | 2.001 | 2.001 | 2.001 |
| Sampled max send age ms | 8.414 | 8.252 | 8.460 | 8.416 |
| Sampled max completion age ms | 30.581 | 30.329 | 31.906 | 32.250 |

Mean CPU fell 5.3%, producer p95 fell 6.3%, allocations fell 1.7%; wall time rose 0.8%, callback
p95 rose 2.2%, and pump p95 rose 2.6%. This is a modest CPU/producer improvement, not a throughput
win. All 49 targeted queue/transport tests passed, including a deterministic blocked rental test
demonstrating that completion consumption can progress without the payload lock. The subsequent
[full-world ABBA](performance-2026-11-26.md#completion-fifo-gate-experiment) retained the split
gate for its structural independence and modest CPU/completion-pump gains; no material throughput
improvement or stable 60 TPS was demonstrated by that change alone.
