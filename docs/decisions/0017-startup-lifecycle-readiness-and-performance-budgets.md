# 0017: Startup lifecycle, readiness and performance budgets

## Status

Accepted

## Context

Vega must start quickly by design. Startup speed cannot depend on modules merely behaving well by convention, nor can the runtime report itself ready only after every optional cache, index or external integration has completed.

The primary user-visible readiness point is when the world is loaded, required server capabilities are available and the network can accept players safely. Optional warmup may continue after that point.

Terraria world loading is a separate bottleneck from Vega overhead. Vega must measure both independently so framework regressions are not hidden by world-load time and world-load cost is not falsely attributed to Vega.

## Decision

Vega defines an explicit startup state machine and a budgeted critical path.

The canonical lifecycle is:

```text
ProcessStart
    |
    v
Bootstrap
    |
    v
RuntimeReady
    |
    v
ModulesRegistered
    |
    v
WorldLoading
    |
    v
WorldReady
    |
    v
NetworkReady
    |
    v
BackgroundWarmup
    |
    v
FullyWarmed
```

`NetworkReady` is the primary operational readiness point. It means the server may advertise itself online and accept players.

`FullyWarmed` is informative and must not normally block player acceptance.

## Lifecycle stages

### ProcessStart

The operating system has started the process. Only unavoidable runtime/static initialization exists before Vega timing begins.

### Bootstrap

Parse process options, establish logging/telemetry, create the minimal runtime host and resolve only configuration required to decide how the server starts.

Bootstrap must not scan worlds, perform external HTTP calls, query application databases or eagerly instantiate optional services.

### RuntimeReady

The minimum Vega runtime infrastructure exists:

- clocks/timing;
- game-thread dispatcher abstractions;
- cancellation/shutdown control;
- startup telemetry;
- module catalog mechanism;
- core composition primitives.

No world is implied to be loaded at this stage.

### ModulesRegistered

Module metadata has been discovered and each enabled module has synchronously declared its services, commands, handlers and startup work. Registration must be cheap and deterministic.

### WorldLoading

Terraria/runtime integration is loading or creating the world. Vega records this duration separately from Vega framework overhead.

### WorldReady

The authoritative world exists and world-critical Vega capabilities have completed successfully.

### NetworkReady

All work required to safely accept players has completed and the network listener/player admission path is enabled.

This is the main readiness signal for operators, orchestration and status endpoints.

### BackgroundWarmup

Non-critical work runs after player acceptance, subject to bounded concurrency and cancellation.

Examples include optional cache prefill, index expansion, update checks, analytics initialization, auxiliary HTTP integrations and non-essential filesystem scans.

### FullyWarmed

All scheduled startup warmup tasks have either succeeded, failed with an accepted degraded state, or transitioned to lazy/on-demand initialization.

## Module registration contract

The target registration surface is intentionally synchronous and cheap, for example:

```csharp
public interface IVegaModule
{
    void Configure(IModuleBuilder builder);
}
```

`Configure` describes composition. It is not a startup task.

The following are forbidden during module registration except for trivially bounded metadata access supplied by the host:

- SQL queries;
- HTTP requests;
- long-running file I/O;
- world scans;
- heavy computation;
- `Thread.Sleep`;
- waiting on external systems;
- cache warmup;
- starting uncontrolled background threads/tasks.

The module builder should collect declarations rather than executing expensive work immediately.

## Startup work model

A separate asynchronous lifecycle contract handles startup work, for example:

```csharp
public interface IVegaModuleStartup
{
    ValueTask StartAsync(
        ModuleStartupContext context,
        CancellationToken cancellationToken);
}
```

Startup work is classified by a required readiness barrier rather than by vague priority names.

The normative categories are:

```text
RuntimeBarrier
WorldBarrier
NetworkBarrier
Background
Lazy
```

### RuntimeBarrier

Must complete before `RuntimeReady` can be declared. Use only for infrastructure without which subsequent lifecycle stages cannot execute safely.

Failure: startup fails.

### WorldBarrier

Must complete after runtime composition and before `WorldReady`.

Use for work required to establish authoritative world semantics, not merely because a module happens to prefer early initialization.

Failure: the world does not become ready and startup fails for that world/server instance.

### NetworkBarrier

Must complete after the world is ready and before players may connect.

Use for security, connection admission, authoritative player/session state dependencies and other capabilities that would make accepting a player unsafe or semantically invalid.

Failure: network readiness is withheld and startup fails unless the capability was explicitly configured as optional.

### Background

Starts no earlier than its declared prerequisites permit and normally after `NetworkReady`. It does not block player acceptance.

Failure: the capability enters a degraded/failed state; the server continues running unless policy explicitly promotes that capability to a barrier.

### Lazy

No startup execution is required. Initialization occurs on first capability use and should support single-flight semantics so concurrent callers do not duplicate expensive work.

Failure: the specific capability fails for that use. The server remains operational unless the capability is independently required by policy.

This barrier model is preferred over `Critical`/`WorldCritical` because it states exactly which readiness transition is blocked.

## Critical path definition

The Vega startup critical path is all synchronous work and awaited asynchronous work from `ProcessStart` through `NetworkReady`, excluding the measured Terraria world-load interval when reporting Vega framework overhead.

A module may not place work on an earlier barrier merely to simplify its implementation. Earlier barriers require an explicit readiness reason.

Independent barrier work may run concurrently when:

- it does not mutate shared Terraria state;
- dependencies are declared;
- failure ordering is deterministic;
- concurrency is bounded;
- cancellation is propagated.

Concurrency must not be introduced where sequential work is already below budget or where it complicates ownership for negligible gain.

## Performance budgets

Budgets are engineering targets, not promises about every host machine. CI/performance environments should establish stable hardware-specific baselines and alert on regressions.

Initial reference budgets for a production build on a warm local filesystem are:

```text
Vega bootstrap                         < 100 ms
Module discovery                       < 25 ms
Module registration                    < 50 ms
Core/runtime composition               < 50 ms
Other pre-world Vega critical work     < 100 ms
------------------------------------------------
Vega overhead before world loading     < 300 ms

Post-world NetworkBarrier work          < 200 ms
```

Additional module-level guidance:

```text
single module Configure                 warning > 5 ms
single barrier startup task             warning > 25 ms
single synchronous service factory      warning > 10 ms
```

These thresholds are configurable and may be adjusted after measurements on representative production hosts. Raising a budget requires evidence; it must not become the default response to regressions.

`Terraria world load time` is always emitted separately and is not counted inside `Vega overhead before world loading`.

## Startup telemetry

Every lifecycle transition is timestamped using a monotonic clock.

Telemetry must expose at least:

- total time to `RuntimeReady`;
- module discovery duration and module count;
- per-module registration duration;
- runtime/Core composition duration;
- world loading duration;
- per-module barrier task duration;
- time from `WorldReady` to `NetworkReady`;
- time from `NetworkReady` to `FullyWarmed`;
- degraded/failed capabilities;
- startup budget warnings.

Human-readable startup output should remain compact, for example:

```text
Vega starting
Runtime ready                  41 ms
34 modules discovered          17 ms
34 modules registered          16 ms
World loaded                  738 ms
Network ready                  35 ms
Background warmup continues
```

Detailed per-module timings belong in structured telemetry and diagnostics, with warnings surfaced when thresholds are exceeded.

## Detecting startup blockers

The runtime records synchronous registration/factory timings and awaited startup task timings.

A module or service exceeding its budget should produce a warning containing:

- module/service identity;
- lifecycle barrier;
- elapsed duration;
- configured budget;
- whether the work blocked `NetworkReady`.

Repeated regressions should be benchmark/test candidates.

The runtime should make it difficult for modules to hide blocking work in constructors. Module instances and service factories are timed, and documentation/analyzers/tests may enforce the no-I/O registration rule where practical.

## Terraria world loading boundary

Vega does not promise that `.wld` loading becomes fast as a consequence of this ADR.

World load is a distinct runtime phase owned by the Terraria integration boundary. Future work may optimize:

- world metadata access;
- lazy/partial indexes;
- startup-time scans;
- world provider abstractions;
- sandbox backends;
- serialization/load paths where safely replaceable.

Those optimizations must not require changing `Vega.Core` merely because the Terraria-backed implementation changes.

`IWorldContext` and related contracts must preserve this separation.

## Threading rules during startup

Mutable Terraria state follows ADR 0003 and ADR 0016:

- Terraria state mutation occurs only on the appropriate game thread;
- SQL, HTTP, file I/O, serialization and pure CPU work may run on workers;
- results that mutate Terraria state are marshalled back to the game thread;
- large game-thread operations should support time slicing where semantics permit;
- background warmup must not race with authoritative world/player state ownership.

Startup concurrency is allowed only for independent work with explicit dependencies.

## Failure semantics

Startup failure is tied to readiness barriers:

- `RuntimeBarrier` failure: process/server startup fails and no world/network readiness is reported.
- `WorldBarrier` failure: `WorldReady` is not reached; the server does not accept players.
- `NetworkBarrier` failure: `NetworkReady` is not reached; the server remains offline even if the world loaded.
- `Background` failure: server remains online in a declared degraded state; the failed capability is observable and may be retried according to its policy.
- `Lazy` failure: the requested capability reports failure to its caller; unrelated server operation continues.

Optional capabilities must declare optionality explicitly. Catching and logging an exception does not by itself make a barrier optional.

Shutdown/cancellation during startup must stop pending work promptly and must never emit a later readiness transition after cancellation has won.

## Consequences

- Startup speed becomes a measurable architecture property.
- `NetworkReady` is separated from `FullyWarmed`, allowing useful work to continue without keeping the server offline.
- Slow modules become observable instead of silently extending startup time.
- Terraria world-load cost is measured honestly and independently from Vega overhead.
- Module authors receive a clear rule: registration describes; lifecycle tasks execute.
- The runtime gains explicit degraded-state semantics rather than treating every optional integration failure as a server-wide startup failure.
