# Vega.Core cohesion audit

This is the Phase 21 ownership inventory. “Frequency” is relative to the repository history reviewed during the audit; consumers are current runtime consumers, not hypothetical extensions.

| Namespace | Responsibility and dependencies | Public boundary / runtime consumer | Test owner | Frequency | Decision |
| --- | --- | --- | --- | --- | --- |
| `Vega.Core.Authorization` | Effective permissions, role leases and session projection; Contracts only | authorization contracts; Runtime, commands and Regions | authorization/persistence tests | high | **Keep**: cross-domain kernel policy |
| `Vega.Core.Bans` | typed ban matching/enforcement over contract stores | ban/moderation contracts; Runtime admission | ban command and persistence tests | medium | **Keep** with moderation for 1.0; no second independent consumer justifies a project |
| `Vega.Core.Commands` | dispatcher, live catalog and discovery | command contracts; every operator surface | command dispatcher/discovery tests | high | **Keep**: kernel routing boundary |
| `Vega.Core.Commands.Administration` | accounts, groups, moderation, inspection, config, server/plugin/restart control | domain contracts; Runtime | command-specific tests | high | **Keep** now; region command extracted, remaining handlers coordinate kernel-owned services |
| `Vega.Core.Commands.Players` | login/session/language/playing commands | account, player and localization contracts | session/localization tests | medium | **Keep** with player session application layer |
| `Vega.Core.Commands.System` | server info, tick rate and update query | operations/update contracts | command/operations tests | low | **Merge/keep** in command application layer; a separate project would be packaging only |
| `Vega.Core.Configuration` | validated launch precedence and restart-only classification | immutable effective config; Runtime | configuration tests | medium | **Keep**: process application policy, not file infrastructure |
| `Vega.Core.Events` | typed sync/async event pipelines | event contracts; Runtime/module composition | pipeline tests | medium | **Keep**: kernel dispatch primitive with real consumers |
| `Vega.Core.Extensions` | session-scoped extension state | extension contracts; PVP module | extension-state tests | low | **Keep** until another implementation appears; extraction would be one class plus pass-through |
| `Vega.Core.GeoIP` | bounded GeoIP lookup policy and updater boundary | GeoIP contracts; security configuration | pinned MaxMind tests | low | **Keep**: security input service; reassess if distributed as an optional database module |
| `Vega.Core.Localization` | catalog registration, fallback and recipient culture | localization contracts; commands/Runtime | localization tests | medium | **Keep**: shared application boundary |
| `Vega.Core.Messaging` | ordered chat transform/moderation/render pipeline | messaging contracts; Runtime/modules | chat tests | medium | **Keep**: shared kernel pipeline |
| `Vega.Core.Modules` | static lifecycle, soft-restart generations and collectible plugin host | module contracts; Runtime/Operations | lifecycle/hot-reload/restart tests | high | **Keep**: process application lifecycle; not a gameplay domain |
| `Vega.Core.Networking` | generic packet pipeline and authoritative protocol/security policies | networking/player/world contracts; Runtime | packet incident regressions | high | **Keep**: security kernel; region ACL moved out because it had separate domain ownership |
| `Vega.Core.Operations` | lifecycle and safe module/read-model adapters | operations contracts; Runtime | operations tests | low | **Keep**: thin kernel projection, no UI dependency |
| `Vega.Core.Players` | account sessions, inspection and authorization projection | account/player contracts; Runtime/commands | account/session tests | high | **Keep**: identity application layer |
| `Vega.Core.Scenes` | authoritative overlay/visibility runtime over contracts | scene contracts; Scenes module and Terraria renderer | scene tests/benchmark | medium | **Keep**: shared infrastructure with multiple real consumers |
| `Vega.Core.Updates` | bounded GitHub release query | update contract; system command | update tests | low | **Keep** until a second provider exists; extraction adds no ownership value |
| `Vega.Core.Worlds` | world clock, reset orchestration and image option parsing | world/runtime contracts; Runtime/modules | world clock/reset/image tests | medium | **Keep**: coordinated kernel world policy; Terraria mutation remains outside |

## Candidate conclusions

- **Regions — Extract.** Command and ACL policy formed a complete independently testable domain slice. ADR 0023 records the implemented split.
- **Administration/identity — Keep.** Account sessions, effective authorization and delegated administration are mutually coupled kernel policy. Persistence is already separate; another project would make Core a mediator.
- **Moderation — Keep for 1.0.** Ban/mute matching and commands share identity/admission services. Revisit only when an independent moderation module needs the same application implementation.
- **Packet security — Keep.** Ownership, sanity and rate-limit checks are the authoritative network kernel. Domain policies such as Regions may plug into the typed pipeline without moving the pipeline itself.
- **GeoIP and updates — Keep.** Each is a small bounded service with one consumer. Separate assemblies would be project-per-folder churn.
- **Module lifecycle — Keep.** It owns process application generations and registration gates. External module implementations depend on Contracts, not this implementation assembly.

Project references are acyclic: Contracts is the semantic root; Regions depends on Contracts; Persistence implements Contracts; Core depends on Contracts; Terraria implements Contracts; Runtime alone composes them. The architecture suite enforces that extracted domains never use Core as a transit dependency.
