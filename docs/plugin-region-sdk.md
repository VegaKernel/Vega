# Plugin region SDK

The `context.World.Regions` facade is scoped to the world hosted by the current Vega process. It uses the same durable relational region repository and ACLs as native `/region`, Operations and REST; no plugin-owned duplicate region store is required. Region writes are privileged plugin operations, not player command authorization.

```csharp
using Vega.Contracts.Regions;

var regions = context.World.Regions;
var created = await regions.CreateAsync("house", 100, 120, 20, 12, priority: 5,
    owner: "alice", cancellationToken: token);
if (!created.Succeeded || created.After is not { } house)
    return;

var grant = await regions.SetAccessAsync(house, RegionAccessKind.Member, "bob", true, token);
if (grant.Succeeded && grant.After is { } updated)
    await regions.UpdateAsync(updated, new RegionUpdate(Flag: "build", FlagValue: false), token);
```

Operations:

- `Snapshot()`, `FindTop(x, y)` and `FindContaining(x, y)`: detached snapshots; `FindContaining` includes overlaps ordered by protection precedence (priority, area, identity).
- `CreateAsync`: validates against dimensions of the currently loaded world.
- `UpdateAsync`: geometry, priority, owner, and boolean flags; geometry updates are validated against the current world and derive unchanged geometry from the current stored region, not caller-supplied data.
- `SetAccessAsync`: add/remove account members or groups.
- `RenameAsync`: preserves the stable region ID and all ACLs.
- `DeleteAsync`: removes the region and its access records.

Pass the last observed `RegionSnapshot` as the expected handle for writes. The repository validates both stable ID and revision transactionally; after another command or plugin changes the region, re-read before retrying. An old snapshot does not match a region recreated under the same name. Foreign-world snapshots are rejected; the plugin's audit identity is supplied by Vega. Region IDs, flags and ACLs are persisted by the configured database provider. Invalid arguments also use the normal repository validation exceptions; they are not successful mutations.

## Region events

```csharp
context.WorldEvents.OnRegionEntered(e => {
    var membership = e.Membership;
    // Use both PlayerId and SessionId before applying delayed work to this player.
});
context.WorldEvents.OnRegionLeft(e => {
    // Restore per-region UI/visuals for this exact player session.
});
context.WorldEvents.OnRegionChanged(e => {
    // e.Kind: Created, Updated, Renamed, AccessChanged or Deleted
    // e.Before / e.After: detached snapshots, with null before creation / after deletion
});
```

The host runs one tracker for all subscribers. It compares published authoritative player positions after the native update, including all overlapping regions, and retains exact session IDs. A player enters/leaves when their position or region geometry changes; deletion and disconnect produce exits. A rename or ACL-only revision change does not emit false enter/leave events. The position represents the native snapshot's top-left pixel coordinate divided by 16. Membership sampling is tick-based, not continuous geometric interpolation; crossing a thin region between samples is not guaranteed to emit a transition. A plugin subscribing after players have already been tracked should inspect current positions using `FindContaining` to initialize its own state.

`OnRegionChanged` reports committed local repository mutations including native commands and REST, dispatched on the bounded game-thread queue. Offline imports and writes in separate processes are not an event stream; a dropped observation is not a failed mutation. Re-read authoritative snapshots when reconciling state. All handlers are guarded and detached with the plugin lifetime. Keep them short; schedule longer I/O using plugin tasks. This is not a raw packet interception API or a hook for overriding the built-in region protection policy.

## Additional interaction restrictions

Plugins can observe a player's validated interaction request and add a restriction without exposing a packet or a mutable native tile:

```csharp
using Vega.Contracts.Worlds;

context.WorldEvents.AllowTileInteraction(request =>
{
    var region = context.World.Regions.FindTop(request.Start.X, request.Start.Y);
    return request.Operation != TileEditOperation.BreakTile ||
           region?.Name != "preserve";
});

context.WorldEvents.OnTileInteractionRequested(request =>
{
    // Record an intent, not a successful world edit. Native processing may still refuse it.
});
```

These callbacks run synchronously on the game thread **after** built-in authentication, region authorization, inventory/ownership and sanity checks. Returning `true` cannot override any existing restriction; `false` prevents the native interaction. The callback carries the exact session and world, a typed operation family (`Tile`, `ObjectPlacement`, `Door`, `Paint`, `Container`, etc.), `Build`/`Use`, requested anchor coordinates, and (for tile requests) a specific place/break/replace/wiring/shape/actuator operation. The `ContentType` is present for tile/wall placement or replacement, not arbitrary native state. Multi-tile objects and wiring may affect cells beyond requested anchors; this is not a per-cell committed history or the complete native framing/edit transaction. Natural simulation and plugin-owned edits are not player requests. Callbacks must not block or perform database I/O; subscriptions retire with the plugin.

The analogous PvP stance/team policy supports region rules such as forbidding hostility in a lobby:

```csharp
context.WorldEvents.AllowPlayerPvpChange(request =>
{
    var region = context.World.Regions.FindTop((int)(request.Position.X / 16), (int)(request.Position.Y / 16));
    return region?.Name != "lobby" || request.RequestedHostile != true;
});
```

`OnPlayerPvpChangeRequested` observes the same request; `OnPlayerPvpChanged` remains a post-tick observation of actual state. The request contains current live native position/stance, exact session, and the requested hostility or team value. It cannot force a stance, alter damage, or turn a client-reported attack source into authenticated attribution.


## Observed committed cell changes

For History-like plugins, subscribe to **actual detached cell differences** from accepted native player interactions:

```csharp
context.WorldEvents.OnPlayerTileChangesCommitted(change =>
{
    foreach (var cell in change.Changes)
        RecordCellChange(change.Intent.SessionId, cell.Position, cell.Before, cell.After);
});
```

The native GetData wrapper captures after the typed policy allows the operation and compares after native processing returns. The callback carries the exact world/session, original intent, observed bounds, and a read-only list of before/after `SceneTile` values. No callback is emitted for ineffective/denied operations, a replaced session/world grid, or an observation outside the supported bound. The scan uses two tiles of padding for direct tile/paint/liquid operations and four for other anchor operations; at most **4,096** cells are inspected. It is not a complete framing, wiring, chest-inventory, entity or natural-simulation history, and effects outside the observed bounds are not attributed. The ordinary unsubscribed path performs no snapshots; the observation runs on the owning game thread. Buffer persistence batches separately and avoid blocking in callbacks.
