# Plugin API anti-framework audit

Phase 24 audits the surface an external author must use, not every public domain contract that happens to be reusable. The golden path is `VegaPlugin` plus `IVegaPluginContext`; direct `IHotReloadableVegaModule` and low-level networking contracts are advanced opt-ins.

## Findings and decisions

| Concern | Finding | Decision |
| --- | --- | --- |
| Entry point | Direct hot-module lifecycle exposed start/stop/dispose and registration leases. | `VegaPlugin.Configure` is the ordinary entry. The base owns lifecycle adaptation. |
| Context size | Passing arbitrary Core services would create a service locator as the SDK grows. | Expose only named curated facets with explicit responsibilities. There is no generic resolve method and adding a facet requires a supported plugin use case. |
| Registration | Commands, events and timers otherwise need paired cleanup. | Host records them in one scope and removes them in reverse order on unload. |
| Simple commands | Requiring a dedicated `ICommandHandler` type for every trivial command made the smallest Vega command locally more verbose than a TShock delegate. | `IPluginCommands` also accepts an inline callback plus command metadata. It is wrapped into the normal `ICommandHandler` path and receives the same authorization and lifetime ownership. Explicit handlers remain the preferred form for larger commands. |
| Threading | Raw dispatcher and synchronization primitives leak runtime topology. | `Tasks.Post`, `Every` and `RunIo` express the supported operations without exposing dispatcher, queue or context types. |
| Player access | Terraria/`TSPlayer` access couples plugins to mutable state. | Expose immutable snapshots and narrow message/kick/teleport capabilities. |
| Configuration/state | Giving plugins the Vega database connection breaks domain ownership. | Provide managed plugin configuration plus plugin-owned confined storage instead of a generic DAL. |
| Reflection/DI | Assembly scanning, attribute registration and replaceable global services hide ownership. | Registration remains explicit C#; no container, scanning or universal `IServer` is introduced. |
| Performance | Convenience must not create an alternate command/event implementation. | Explicit `ICommandHandler` registrations remain direct. The simple-command callback creates one small wrapper at registration and delegates through the same command registry; it adds no reflection, service lookup or alternate authorization path. |

`Vega.PluginSdk` depends only on `Vega.Contracts` and is built with trimming/AOT compatibility analyzers. Architecture tests reject references or vocabulary from Terraria, TerrariaRuntime, UI, packet queues, `AssemblyLoadContext`, `SynchronizationContext` and service locators. The sample and template use the public surface, and the packaging fixtures build a repository-independent consumer from the locally packed single `Vega` package.

## Ceremony comparison

The measured comparison is recorded in [`plugin-ceremony-comparison.md`](plugin-ceremony-comparison.md). It uses the maintained Vega template/sample and the pinned project-owned `TerraZ-Team/Homes@4e7d33fe3dba7c94a498b04553df10c92f056007` TShock plugin as concrete lifecycle evidence.

The first pass found one genuine ergonomic defect: the smallest command required a dedicated handler type. The callback registration overload removes that ceremony without creating a second command pipeline. After that correction, complete ordinary Vega scenarios are at or below TShock-class ceremony once plugin bootstrap, reverse registration, task cancellation and thread-return responsibilities are counted instead of comparing only one terse TShock delegate line.

The representative suite covers command permissions/aliases/replies, join and leave hooks, player lookup/messaging, validated JSON configuration, persistent JSON state, repeating work, async file I/O with game-thread apply, deterministic unload and the separate advanced typed-packet validator. The cancellable build/gameplay hook remains open and must land only with real authoritative hook ownership rather than an unused abstraction.

The SDK is still a development surface. Its assembly version follows the repository's `1.0.0` baseline, but a frozen API compatibility baseline remains a separate gate before public stability is declared.

## Facade baseline

The recorded .NET 11 Preview 7 x64 Windows harness in `benchmarks/Vega.PluginSdk.Benchmarks` ran 5,000,000 invocations per path. Direct registry command dispatch measured 22.99 ns/call and the facade-registered explicit handler 22.75 ns/call; direct player-join event dispatch measured 5.61 ns/call and the facade-registered callback 5.38 ns/call. Every recorded path allocated zero bytes, and reference-identity assertions proved that explicit handler registration inserted no dispatch wrapper.

The newer simple-command callback convenience is intentionally a separate registration shape: it allocates one handler wrapper when the plugin registers the command and then forwards execution through the same registry. Its benchmark fixture should measure that path before any future claim that the callback itself is allocation/performance-identical to explicit handler registration.
