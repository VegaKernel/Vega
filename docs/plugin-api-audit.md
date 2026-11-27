# Plugin API anti-framework audit

Phase 24 audits the surface an external author must use, not every public domain contract that happens to be reusable. The golden path is `VegaPlugin` plus `IVegaPluginContext`; direct `IHotReloadableVegaModule` and low-level networking contracts are advanced opt-ins.

## Findings and decisions

| Concern | Finding | Decision |
| --- | --- | --- |
| Entry point | Direct hot-module lifecycle exposed start/stop/dispose and registration leases. | `VegaPlugin.Configure` is the ordinary entry. The base owns lifecycle adaptation. |
| Context size | Passing every Core service would create a service locator. | Expose exactly five named facets: commands, players, tasks, storage and log. No generic resolve method exists. |
| Registration | Commands, events and timers otherwise need paired cleanup. | Host records them in one scope and removes them in reverse order on unload. |
| Threading | Raw dispatcher and synchronization primitives leak runtime topology. | `Tasks.Post`, `Every` and `RunIo` express the supported operations without exposing dispatcher, queue or context types. |
| Player access | Terraria/`TSPlayer` access couples plugins to mutable state. | Expose immutable snapshots and narrow message/kick/teleport capabilities. |
| Configuration/state | Giving plugins the Vega database connection breaks domain ownership. | Provide plugin-owned atomic JSON beneath a confined data directory. This is not a generic DAL. |
| Reflection/DI | Assembly scanning, attribute registration and replaceable global services hide ownership. | Registration remains explicit C#; no container, scanning or universal `IServer` is introduced. |
| Performance | A facade could wrap every dispatch. | Command handlers and player callbacks are registered directly. The facade exists only at registration/lifecycle boundaries; it adds no per-command or per-hook dispatch layer. |

`Vega.PluginSdk` depends only on `Vega.Contracts` and is built with trimming/AOT compatibility analyzers. Architecture tests reject references or vocabulary from Terraria, TerrariaRuntime, UI, packet queues, `AssemblyLoadContext`, `SynchronizationContext` and service locators. The sample and template use the same public surface, and CI builds a repository-independent project from the locally packed single `Vega` package.

## Ceremony comparison

The maintained Vega template requires one plugin class, one `Configure` override and explicit registration calls. It has zero reverse-registration calls and zero thread-marshalling calls. The equivalent TShock-style behaviors require plugin initialization/disposal pairing, command add/remove ownership, hook add/remove ownership and mutable `TSPlayer` access. Vega intentionally maps the behavior rather than those types.

The representative sample covers command permissions/aliases/replies, join and leave hooks, player lookup/messaging, validated JSON configuration, persistent JSON state, repeating work, async file I/O with game-thread apply, and deterministic unload. Cancellable build/gameplay hooks and the advanced typed-packet sample remain open; they must be added only with real authoritative hook ownership, not as unused abstractions.

The SDK is still a development surface. Its assembly version follows the repository's `1.0.0` baseline; the optional `VegaPluginApiGenerationAttribute` preflights the declared source/API generation before activation, not a frozen binary ABI. The expanded ordinary SDK includes native world-entry/pre-reset player notifications and explicit activation-owned disposable/async resources. Notifications use the per-activation execution guard; cleanup runs after drain, is included in bounded retirement and must surface failures. The architecture delegate inventory and `PluginSdkContextTests` cover these ownership boundaries.

## Facade baseline

The original .NET 11 Preview 7 x64 Windows harness measured unguarded reference-identical command/event registration. That baseline is historical: plugin fault quarantine now deliberately owns a dispatch boundary. The current .NET 11 x64 Windows harness (`benchmarks/Vega.PluginSdk.Benchmarks`, 5,000,000 calls per path) measured direct/guarded commands at 29.28/55.64 ns and direct/guarded join events at 10.49/35.78 ns; all paths allocated zero bytes. Command operation labels are cached at registration and lifecycle callbacks pass their argument directly without a per-event closure. CI now asserts guarded registration, exact observed callback count, no unexpected faults and absence of per-call allocation (allowing at most 4096 bytes of aggregate measurement noise). The benchmark does not impose a hardware-specific latency threshold.
