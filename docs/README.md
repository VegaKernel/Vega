# Vega documentation

[English project guide](en/project-guide.md) · [Руководство на русском](ru/project-guide.md)

This directory contains Vega's project documentation, implementation notes, roadmap, and Architecture Decision Records.

## Start here

- `en/project-guide.md` — complete English guide to architecture, build, startup, modules, packaging, and releases.
- `ru/project-guide.md` — полное русское руководство по архитектуре, сборке, запуску, модулям, упаковке и релизам.
- `architecture.md` — target architecture and subsystem boundaries.
- `roadmap.md` — staged implementation plan and completed validation.
- `net11-dependency-audit.md` — resolved package assets, upgrades, known MonoMod exception, and clean-consumer validation for Phase 18.
- `releasing.md` — versioning, automatic tagged releases, and manual server publication.

## Subsystems

- `events.md` — typed event processing.
- `extension-state.md` — scoped typed session state.
- `operations.md` — operations API and Terminal UI.
- `packet-pipeline.md` — Phase 22 ingress classification, threading boundary, and synchronous baseline.
- `native-aot.md` — Phase 23 CoreCLR boundary, executable audit, and NativeAOT admission gate.
- `plugin-sdk.md` / `ru/plugin-sdk.md` — Phase 24 first-plugin path, lifecycle ownership, async I/O, and TShock behavior mapping.
- `plugin-api-audit.md` — Phase 24 public-surface findings, rejected framework patterns, and remaining gaps.
- `cluster.md` — optional typed cluster messaging.
- `persistence.md` / `ru/persistence.md` — SQLite, PostgreSQL and MySQL configuration, the Vega 1.0 baseline, backup and recovery.
- `world-images.md` — `.vworld` format, fallback rules, and benchmark evidence.
- `world-maintenance.md` / `ru/world-maintenance.md` — autosave and world-image refresh.
- `auto-world-cleaner.md` / `ru/auto-world-cleaner.md` — configurable item/projectile cleanup module.
- `scenes.md` / `ru/scenes.md` — observer-scoped overlays, visibility and performance evidence.
- `sandboxes.md` / `ru/sandboxes.md` — isolated world lifecycle, routing, security and capacity evidence.
- `backups.md` / `ru/backups.md` — scheduled world/database deployment archives and restore drills.
- `decisions/` — Architecture Decision Records.

Documentation changes are architecture changes when they alter ownership, threading, public contracts, lifecycle semantics, or runtime packaging. Update both project-guide languages and the affected subsystem document or ADR in the same logical change.
