# Vega documentation

[English project guide](en/project-guide.md) · [Руководство на русском](ru/project-guide.md)

This directory contains Vega's project documentation, implementation notes, roadmap, and Architecture Decision Records.

## Start here

- `en/project-guide.md` — complete English guide to architecture, build, startup, modules, packaging, and releases.
- `ru/project-guide.md` — полное русское руководство по архитектуре, сборке, запуску, модулям, упаковке и релизам.
- `architecture.md` — target architecture and subsystem boundaries.
- `roadmap.md` — staged implementation plan and completed validation.
- `migration-audit.md` — pinned TShock/TerraZ sources, capability dispositions, and the Phase 9/10 closure gate.
- `releasing.md` — versioning, automatic tagged releases, and manual server publication.

## Subsystems

- `events.md` — typed event processing.
- `extension-state.md` — scoped typed session state.
- `operations.md` — operations API and Terminal UI.
- `cluster.md` — optional typed cluster messaging.
- `world-images.md` — `.vworld` format, fallback rules, and benchmark evidence.
- `decisions/` — Architecture Decision Records.

Documentation changes are architecture changes when they alter ownership, threading, public contracts, lifecycle semantics, or runtime packaging. Update both project-guide languages and the affected subsystem document or ADR in the same logical change.
