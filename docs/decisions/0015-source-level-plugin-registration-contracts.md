# 0015: Keep source-level plugin registration contracts, not binary compatibility

## Status

Superseded by ADR 0016.

## Context

This ADR removed the original requirement to preserve binary compatibility with compiled TShock plugins while retaining a narrow source-level transition surface for hooks and legacy commands.

That was a useful migration simplification, but Vega no longer treats TShock source compatibility as a target requirement either.

## Historical decision

TShockNG did not guarantee binary compatibility. During migration it exposed narrow registration abstractions for legacy-shaped hooks and `Command` objects so existing plugin source could be rebuilt against the evolving server.

## Superseding direction

ADR 0016 establishes a Vega-native module API. Existing TerraZ/TShock plugins are ported to that API based on required behavior and capabilities.

`IHookRegistry<TEventArgs>`, `ILegacyCommandRegistry`, TShock hook argument types and legacy `Command` models may exist temporarily as migration scaffolding, but they are not long-term public contracts and must not constrain Vega design.

## Consequences

- neither TShock binary compatibility nor source-level compatibility is a target requirement;
- legacy registration surfaces can be deleted once the native Vega module path covers required production modules;
- module/event/command APIs are designed for Vega semantics rather than historical TShock shapes.
