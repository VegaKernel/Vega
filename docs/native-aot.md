# Runtime specialization and NativeAOT audit

Phase 23 keeps runtime selection evidence-driven. NativeAOT is an option for a small fixed-function executable, not a replacement for the dynamic Vega server.

## Current runtime boundary

`Vega.Server` runs on CoreCLR. Its project explicitly enables tiered compilation and dynamic PGO, defaults ReadyToRun off, and rejects `PublishAot=true`. ReadyToRun remains an optional CoreCLR experiment through `-p:PublishReadyToRun=true`; it does not change the plugin or process model.

The server must retain managed `ServerPlugins`, collectible `AssemblyLoadContext` hot replacement, soft-restart generations, runtime assembly resolution and the OTAPI/MonoMod hook stack. Those are deliberate dynamic boundaries. The Phase 22 packet path also stays in the same process: the Terraria hook calls the Multiplicity adapter and policy pipeline directly. No IPC boundary is introduced merely to create an AOT worker.

`Vega.Contracts` is the first AOT-compatible library boundary. `IsAotCompatible=true` enables the SDK single-file, trimming and AOT analyzers; warnings are errors in that project. It has no project or package dependencies and architecture tests prevent reflection/plugin-host, Terraria and UI concerns from entering it. This does not make `Vega.Contracts` a separately deployed component.

## Executable audit

Only `src/Vega.Runtime/Vega.Runtime.csproj` is a product executable today. Benchmark programs are measurement harnesses and `Vega.Sandbox.TestBackend` is test infrastructure; neither is a deployable NativeAOT candidate.

| Audit order | Current implementation | Result |
| --- | --- | --- |
| Launcher/bootstrapper | The generated `Vega.Server` apphost starts the dynamic server; there is no separate launcher project. | No isolated candidate. |
| Updater | Update checks run inside the server; no updater executable exists. | No candidate. |
| Crash reporter | No standalone executable exists. | Candidate category only when an operational requirement is defined. |
| Standalone admin/world tools | No shipping executable exists. | Candidate category only when a fixed command surface is required. |
| Sandbox supervisor | `Vega.Sandboxes` is a server-composed library; the only backend executable is test-only. | Do not AOT test infrastructure or split the current control plane speculatively. |
| Backup/world workers | Backups and world-image work are in-process libraries coordinated with authoritative server state. | No isolated candidate. |
| Cluster relay/gateway | `Vega.Cluster` is an optional library composed by the server. | No isolated candidate. |

The audit therefore adds no NativeAOT executable. Creating a process solely to satisfy the roadmap would add deployment and diagnostics cost without a consumer or a measured benefit.

## Admission and comparison gate

A future candidate may use NativeAOT only when all of the following are true:

- it is a shipping, fixed-function executable with a clear operational owner and fallback;
- it does not load arbitrary managed plugins, emit code at runtime or depend on the Terraria/plugin-host/UI graph;
- its complete dependency graph builds with trimming and AOT analyzers enabled;
- a clean-machine deployment validates startup, shutdown and crash diagnostics;
- CoreCLR self-contained, ReadyToRun and NativeAOT variants are compared for startup, private memory/working set, publish size, CPU, workload throughput/latency and build/operational complexity.

Keep the CoreCLR variant when NativeAOT does not provide a measured end-to-end advantage. Phase 22 packet measurements must stabilize independently before runtime specialization is credited with any server performance change.
