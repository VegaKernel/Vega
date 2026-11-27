# Architecture and plugin development review

## Assessment

The architecture has a useful separation: Contracts describe detached domain values; Core owns policy and orchestration; Terraria owns authoritative native operations; Persistence owns provider differences; Runtime composes an isolated server process; PluginSdk exposes curated operations rather than direct internal game arrays. Keep these boundaries. A single service locator, a generic SQL API, or a compatibility layer exposing all of TShock's mutable objects would undermine them.

The primary quality problems were in composition, duplicate pre-release initialization, lifecycle plumbing and the amount of ceremony required for simple plugins. File size is a signal, not a reason to split one coherent state machine into unrelated classes.

## Changes made during the review

| Area | Before | Now |
| --- | --- | --- |
| Relational schema | Six pre-release history entries and repeated provider-specific upgrade branches | One release baseline, `vega-initial` (v1), with shared table definitions and provider-specific initialization locks. TShock imports are retained. Unsupported developmental databases are refused, not silently rewritten. |
| Primary SSC | Separate experimental primary file plus migration into the shared SSC store | Both primary persistence modes use the same shared store from the outset. The obsolete internal migration command and archive path are removed; TShock import still works transactionally. |
| Runtime composition | `Program.cs` roughly 59.5 KiB, repetitive individual command registration, localization and module catalog in the entry point | Roughly 49 KiB; cohesive command families and composition support extracted. Registration publishes whole families in one immutable map update; concurrent registration disposal cannot lose a command lifetime handle. |
| Plugin activation scope | Roughly 44 KiB in the central partial file | Roughly 17 KiB central lifecycle plus focused Commands, Players, WorldEvents, Tasks, Configuration and Operations partials. One bounded tracked operation mechanism for identity reads and service/data/REST/cluster work. |
| World SDK | Public contracts and all host adapters in a 35 KiB file | A 7 KiB public contract file and focused host adapters for regions, blocks, NPCs and inventory. |
| Item delivery | Unused parallel CustomItems module and direct socket delivery | One native delivery path with typed recipes/stats and normal restrictions; removed the unused module project. |
| Plugin authoring | Class + descriptor + handler implementation for every simple command | Delegate command registration, standard replies, exact command-player lookup, logging helpers, and single-document CAS helpers. The existing guarded registration and storage paths are reused. |

## Recommended plugin shape

```csharp
public sealed class WelcomePlugin : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } =
        new("TerraZ.Welcome", "1.0.0", ["Vega.PluginSdk"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register("welcome", "welcome.use", command =>
            plugin.Players.TryGetCommandPlayer(command, out var player)
                ? PluginReply.Success($"Welcome, {player!.Name}!")
                : PluginReply.Error("welcome.player", "This command needs a connected player."),
            allowServer: false);
        plugin.Players.OnEnteredWorld(player => plugin.Players.Message(player, "Welcome!"));
    }
}
```

Use typed snapshots for delayed operations, not reusable player slot indices. Native world mutations remain marshalled to the authoritative game thread. Long I/O uses tracked SDK operations; CPU work uses the bounded task API. Explicit JSON metadata on persisted documents and inter-plugin contracts is intentional: it keeps serialization independent of retired collectible assemblies and avoids a reflection-based runtime dependency graph.

## Remaining architectural priorities

1. **Real-client qualification:** the native hooks, typed gestures, item tweaks and SSC have synthetic/fixture coverage, but stock-client timing and complete cross-world behavior still require deployment tests. Do not derive full TShock behavioral equivalence from unit tests.
2. **Authoritative item and combat models:** native returned hurt is distinguishable from client-reported provenance. Arbitrary item stats are transient world-item tweaks; durable custom item identity across inventory, trading, death and SSC needs an authoritative item representation. Keep the ordinary packet API closed to plugins.
3. **Composition root:** remaining Program initialization is lengthy because native lifecycle ordering is explicit. Further extract coherent startup components only where their ownership/disposal and error paths can be tested. Avoid a general service locator. Large account, NPC and sandbox state-machine files should be split by cohesive operations, not by line-count targets.
4. **World identity:** the internal SDK world service binding is process-local. It is appropriate for the current one-native-world-per-process model; supporting multiple native worlds in a single process would require scoped service bindings and a separate isolation review.
5. **Cluster authority:** the implemented peer notifications are an actual authenticated transport, but not distributed database consistency, inter-server character transfer or universal remote operation routing. Keep global bans/whitelist authority separate and qualify transport under real deployment latency.
6. **Persistence:** the shared baseline and provider-neutral contract are now simpler. Run the same contract against real MySQL/PostgreSQL before deployment; retain offline backup/import checks and do not remove external TShock source conversion.

See [plugin SDK](plugin-sdk.md), [migration readiness](terraz-plugin-port-readiness.md), [cluster](plugin-cluster.md), and [persistence](persistence.md) for integration details.
