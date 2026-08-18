# TShockNG documentation

This directory records the architecture, migration plan, compatibility guarantees, and architectural decisions for TShockNG.

## Documents

- `architecture.md` — target architecture and subsystem boundaries.
- `roadmap.md` — staged migration plan.
- `compatibility.md` — source and binary compatibility policy for existing TShock plugins.
- `migration.md` — migration from the current TShock codebase to TShockNG.
- `decisions/` — Architecture Decision Records (ADRs).

Documentation changes are part of architecture changes. A change that alters a boundary, public compatibility rule, threading rule, or ownership of a subsystem must update the corresponding document or ADR in the same change.
