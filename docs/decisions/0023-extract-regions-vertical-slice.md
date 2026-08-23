# ADR 0023: extract the regions vertical slice

Status: accepted and implemented

## Context

The Phase 21 cohesion audit found two region-specific implementations in `Vega.Core`: the complete operator command and the packet ACL policy. They changed for region lifecycle and packet coverage, consumed only region/player/authorization contracts plus Multiplicity views, and did not participate in another Core domain. Persistence and Terraria integration were already behind narrow contracts.

Keeping these implementations in Core gave Core an unrelated reason to change and made future region modules depend transitively on the application kernel. Moving only DTOs would not improve ownership, so the extraction must move the complete application slice.

## Decision

Create `Vega.Regions`, depending only on `Vega.Contracts` and Multiplicity. It owns:

- `RegionCommand`, including world-scoped authorized mutations;
- `RegionBuildAuthorizationPolicy`, including overlap resolution consumption and build/use packet admission.

Ownership remains deliberately split at stable boundaries:

- public region identities, snapshots and service capabilities: `Vega.Contracts`;
- relational tables, transactions, cache invalidation and Vega 1.0 baseline schema: `Vega.Persistence`;
- authoritative chest and tile-entity coordinate lookup: `Vega.Terraria` through `IWorldInteractionState`;
- command/policy construction: the `Vega.Runtime` composition root;
- immutable display: Operations/TUI through Contracts.

`Vega.Regions` does not reference Core, Persistence, Terraria, Operations or TerminalUI. Core does not reference Regions. Runtime is the only project that composes both. There is no service locator and no domain event chatter between the projects.

## Migration and compatibility

The command names, permissions, error keys, persistence schema, packet decisions and runtime order do not change. The implementation namespace intentionally moves from `Vega.Core.*` to `Vega.Regions`; Vega 1.0 has not shipped a public compatibility promise for those concrete implementation types. Contracts remain stable. Tests moved with the owning slice and the release/NuGet manifests now include `Vega.Regions`.

Startup failure of the new assembly remains a normal process composition failure. Storage unavailability remains owned by Persistence. A packet lookup failure remains fail-closed where an authoritative object coordinate is required.

## Evidence

The extraction adds one 34,304-byte Release assembly and no extra packet indirection: Runtime constructs the same policy directly and the hot method body is unchanged. On the audit machine, an incremental Release solution build took 2.236 seconds and the 16 region-focused tests took 1.535 seconds. The existing packet-policy tests, relational lifecycle regression and architecture dependency test guard behavior and direction. A split that required Core mediation or measurable hot-path adapters would be reverted.

## Consequences

Core loses a product domain and consumers can reason about region ownership from one assembly. Persistence and Terraria remain independent implementations of contracts. Release packaging has one additional library under `libs/`, not a hot-loadable `ServerPlugins` module, because region admission is part of the authoritative server security path.
