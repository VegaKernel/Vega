# 0018: Interpretation note for production-derived ADRs after Vega separation

## Status

Accepted

## Context

ADR 0012, ADR 0013 and ADR 0014 contain substantial production-derived requirements that remain useful, but they were written using TShockNG names and include transitional compatibility language.

## Decision

ADR 0012, ADR 0013 and ADR 0014 remain accepted for their behavioral and capability requirements, subject to ADR 0016:

- references to `TShockNG` should be read as Vega where they describe the future product;
- references to TShockAPI/TSPlayer/legacy command or hook adapters describe migration-era implementation, not target API;
- TerraZ plugins remain evidence for requirements and regression scenarios, not compatibility surfaces;
- optional gameplay/community behavior remains modular;
- server invariants, security primitives, identity/authorization primitives and safe world/runtime abstractions remain candidates for Core/runtime ownership;
- all Terraria-specific implementation belongs behind the Vega runtime/Terraria boundary;
- Core remains free of Terraria, OTAPI, TSAPI and TShockAPI dependencies.

This note avoids rewriting the historical ADR prose as if those documents had originally been authored for Vega.
