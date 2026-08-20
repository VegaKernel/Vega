# 0021: Opt-in collectible server plugins

## Status

Accepted

## Context

Vega's first-party modules are explicitly composed `IVegaModule` instances. They can hold direct packet-pipeline and runtime references and therefore cannot be truthfully unloaded from the default .NET load context.

Operators nevertheless need an emergency administrative path for replacing some optional server plugins without restarting a populated server. The historical TerraZ `PluginLoader` attempted this through reflection and filesystem events, but deinitialization alone could leave commands, hooks, static references or locked files behind.

## Decision

Vega supports hot replacement only for a separate, opt-in contract:

- one assembly directly exports exactly one public, parameterless `IHotReloadableVegaModule`;
- every plugin image receives its own collectible `AssemblyLoadContext` and is loaded from memory so deployment does not depend on replacing a locked primary DLL;
- `Vega.Contracts` is shared with the default context, while plugin-local dependencies resolve beside the plugin;
- commands are registered with disposable leases, and all other hooks/resources must be registered through `IHotModuleContext.Track` or `RegisterCleanup`;
- unload first removes tracked registrations in reverse order, then calls `StopAsync`, `DisposeAsync`, requests context unload and verifies collection through a weak reference;
- replacement stages and validates the new image before detaching the active revision;
- a cleanup, staging or collection failure is observable and requires a process restart; Vega never reports an unverified unload as successful.

`config/modules.json` controls the capability and disables it by default. The same filesystem watcher remains active while disabled, but it only publishes `restart required`. Explicit first-party `IVegaModule` assemblies stay in the default context and remain restart-only.

## Consequences

- `plugin list`, `plugin reload` and `plugin unload` are privileged administrative commands, not a compatibility promise for TShock or arbitrary DLLs;
- copying, changing, renaming or deleting a conforming DLL can be applied automatically when the switch is enabled;
- plugin authors must use host-owned registration lifetimes and must not install untracked static hooks;
- in-flight or externally retained references may prevent collection and deliberately force restart fallback;
- safe replacement of the bundled PVP, CustomItems or future default-context modules requires a separate architectural conversion to dynamic registries, not a misleading loader flag.
