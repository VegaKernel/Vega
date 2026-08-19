# 0005: Modern core API and explicit integration boundaries

## Status

Superseded as target architecture by ADR 0016.

The Contracts/Core separation pioneered here remains useful, but the target roles assigned to `TShockAPI`, `TShockLauncher` and source-level TShock compatibility no longer govern Vega.

## Context

This ADR was written during the TShockNG migration era. It separated implementation-independent contracts, modern server behavior and a TShock-facing integration surface while keeping the existing server executable.

That migration step produced useful ownership boundaries, but Vega is now an independent runtime rather than a TShock modernization project.

## Historical decision

TShockNG used three roles during migration:

1. `TShockNG.Contracts` for implementation-independent contracts.
2. `TShockNG.Core` for migrated server behavior.
3. `TShockAPI` as Terraria/TShock integration and a temporary source-level plugin surface.

`TShockLauncher` acted as composition root.

The key durable constraint was that Core should not depend on `TShockAPI` or mutable Terraria globals.

## Superseding direction

ADR 0016 replaces the target dependency model with Vega-native layers:

```text
Terraria
   ↓
OTAPI
   ↓
Vega.Runtime / Vega.Terraria
   ↓
Vega.Contracts
   ↓
Vega.Core
   ↓
Vega.Modules
```

`TShockAPI` and TSAPI are migration scaffolding only. No compatibility facade, legacy hook surface, `TSPlayer`, `Command` model or TShock global is a required long-term boundary.

## Historical value retained

The following ideas remain active through ADR 0016:

- small implementation-independent contracts;
- explicit composition instead of service-locator globals;
- typed packet processing;
- explicit result types;
- immutable snapshots where mutation is unnecessary;
- game-thread affinity for Terraria state;
- world-aware services;
- separation of Core behavior from runtime integration.

## Consequences

This ADR remains readable as the architectural bridge from TShock to early TShockNG. New implementation work must follow ADR 0016 and ADR 0017 rather than extending the TShockAPI/legacy migration surfaces described here.
