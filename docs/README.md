# Vega documentation

This directory records Vega's target architecture, roadmap and Architecture Decision Records.

## Documents

- `architecture.md` — target architecture and subsystem boundaries.
- `roadmap.md` — staged implementation plan.
- `events.md`, `extension-state.md`, `cluster.md`, and `operations.md` — implemented subsystem contracts.
- `decisions/` — Architecture Decision Records (ADRs).

World-image operation and benchmark evidence are recorded in [`world-images.md`](world-images.md).

Documentation changes are part of architecture changes. Any change that alters a subsystem boundary, threading rule, or ownership decision must update the corresponding document or ADR in the same logical slice.
