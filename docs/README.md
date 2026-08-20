# Vega documentation

This directory records Vega's target architecture, migration plan, compatibility policy, and Architecture Decision Records.

## Documents

- `architecture.md` — target architecture and subsystem boundaries.
- `roadmap.md` — staged migration plan.
- `compatibility.md` — migration and compatibility policy while legacy TShock surfaces are being removed.
- `migration.md` — migration from the inherited TShock codebase to Vega.
- `decisions/` — Architecture Decision Records (ADRs).

Documentation changes are part of architecture changes. Any change that alters a subsystem boundary, migration rule, threading rule, or ownership decision must update the corresponding document or ADR in the same logical slice.
