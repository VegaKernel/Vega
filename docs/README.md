# TShockNG documentation

This directory records the architecture, migration plan, plugin migration policy, and architectural decisions for TShockNG.

## Documents

- `architecture.md` — target architecture and subsystem boundaries.
- `roadmap.md` — staged migration plan.
- `compatibility.md` — source-level plugin migration policy; binary plugin compatibility is not guaranteed.
- `migration.md` — migration from the current TShock codebase to TShockNG.
- `decisions/` — Architecture Decision Records (ADRs).

Documentation changes are part of architecture changes. A change that alters a boundary, plugin migration rule, threading rule, or ownership of a subsystem must update the corresponding document or ADR in the same change.
