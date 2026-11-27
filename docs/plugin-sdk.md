# First Vega plugin

An ordinary plugin needs one NuGet package, one base class and no knowledge of Terraria, TerrariaRuntime, packet buffers, worker lanes or collectible load contexts.

For typed cross-plugin endpoints and durable SQLite/MySQL/PostgreSQL documents, see [Services and persistent data](plugin-services-data.md).
For extending the authenticated loopback operator API without a separate HTTP server, see [Plugin REST operations](plugin-rest.md).

For account lifecycle and delegated group assignments see [Identity](plugin-identity-sdk.md). Region APIs, committed bounded player tile-cell differences and exact tile editing/undo are documented in [Region SDK](plugin-region-sdk.md) and [World editing](plugin-world-edit-sdk.md). `WorldEvents.OnHardmodeChanged` observes the before/after native hardmode flag after a tick; loading an already-hardmode world establishes a baseline rather than firing a new progression event.

## Create and build

From a Vega checkout, install the maintained template and create a project:

```powershell
dotnet pack packaging/Vega.Package/Vega.Package.csproj -c Release -p:PackageVersion=1.0.0-preview.7.local -o artifacts/plugin-feed
dotnet new install ./templates/vega-plugin
dotnet new vegaplugin --name MyPlugin
dotnet restore MyPlugin/MyPlugin.csproj "-p:RestoreAdditionalProjectSources=$PWD/artifacts/plugin-feed"
dotnet build MyPlugin/MyPlugin.csproj --configuration Release --no-restore
```

The generated project references the single `Vega` package at the repository's current prerelease baseline. Update that version together with the server/package version when moving the template to another release. Copy only `MyPlugin.dll` to the server's top-level `ServerPlugins/` directory. Set `enabled` to `true` in `config/modules.json` and restart the server to apply this setting. The loader then activates plugins at startup and watches the directory for changes.

### SDK compatibility declaration

The plugin template and sample opt into an assembly-level SDK API-generation declaration:

```csharp
using Vega.PluginSdk;
[assembly: VegaPluginApiGeneration(VegaPluginApiGenerationAttribute.Current)]
```

The current generation is `1`. The compiler embeds the integer from the referenced SDK assembly. A declared generation must exactly match the host; an incompatible declaration is rejected during metadata preflight, before plugin code is loaded or constructed, and an existing activation is left intact on failed replacement. Older plugins without a declaration continue to be admitted on the existing best-effort basis. This generation is independent of the plugin descriptor version, NuGet package version, .NET target-framework check and runtime capability flags. Increase the generation for intentional incompatible API changes; retain capability checks for optional features. Matching generation is **not a stable binary ABI guarantee**: rebuild and qualify plugins against the exact deployed Vega package before promoting a server update.

## Golden path

Derive one public type from `VegaPlugin`, declare its descriptor and override `Configure`:

```csharp
public sealed class HelloPlugin : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } =
        new("MyPlugin", "1.0.0", ["Vega.PluginSdk"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register("hello", "myplugin.hello",
            command => PluginReply.Success($"Hello, {command.Principal.Name}!"), aliases: ["hi"]);
        plugin.Players.OnEnteredWorld(player => plugin.Players.Message(player, "Hello from MyPlugin!"));
        plugin.Log.Info("MyPlugin configured.");
    }
}
```

The fixed context exposes these discoverable facets:

- `Commands` registers typed command handlers or synchronous/asynchronous delegates using the same guarded handler path; `PluginReply` returns common command responses, and `Players.TryGetCommandPlayer(invocation, out player)` resolves the exact originating session;
- `Players` provides immutable online snapshots, lookup, messaging, kick/teleport operations, join/leave/world-entry/pre-reset hooks and optimistic inventory access;
- `World` exposes the active world through immutable environment, tile, chest and NPC snapshots plus plugin-owned scenes;
- `WorldEvents` registers world-local gameplay notifications and narrowly scoped NPC transformation/loot policies;
- `Activity` observes exact authentication tenures, accepted ordinary public chat and completed authorized commands, including deny/transform policies, without obtaining credentials or command arguments;
- `Identity` provides minimal account identity, group snapshots, effective permission queries, committed changes and explicitly delegated group assignments;
- `Services`, `Data`, `Rest`, `Cluster` and `NpcEconomy` provide isolated typed service calls, durable documents, authenticated operator routes, peer notifications and wallet providers, respectively;
- `Statistics.ReadPlaytimeAsync(player)` reads authenticated entered-world playtime for the exact current player; unavailable or replaced sessions cannot expose another account's data;
- `Warps` reads public navigation points in the hosted persistent world, not hidden warps or other worlds;
- `Tasks` posts to the game thread, schedules repeating game-thread work and runs async I/O with a game-thread apply callback;
- `Resources` owns explicitly transferred disposable or asynchronous resources and cleanup actions for this activation;
- `Configuration` owns typed, validated and reloadable operator settings under `config/plugins/<plugin-name>/`;
- `Storage` stores arbitrary plugin-owned JSON beneath `data/plugins/<plugin-name>/`;
- `Log` writes structured records under the plugin's own source name;
- `Sandboxes` discovers managed worlds and, on the control-plane process, exposes sandbox lifecycle operations;
- `Debug` is a separately gated process-internal diagnostic surface for explicitly trusted tooling plugins.

For a one-document update, `plugin.Data.TrySetAsync(collection, key, expectedRevision, value, jsonTypeInfo)` is shorthand for a single CAS mutation. Multi-document operations still use `CommitAsync`. Source-generated `JsonTypeInfo` keeps serialization explicit and avoids retaining reflection metadata from retired plugin assemblies. These helpers do not create a second task scheduler or command registry.

The host owns every command, event subscription, repeating task and async operation created through these facets. Unload first cancels and joins background work, then removes registrations before releasing the collectible context. Ordinary plugins do not keep registration leases or reverse-registration lists.

`Players.OnEnteredWorld` emits the native game-thread world-entry snapshot with its exact session ID.
`Players.OnClosing` runs before the native character is reset, while the old session still exists;
`OnLeft` remains the subsequent session removal notification. These callbacks are notifications, not
cancellation policies, and all use the same per-plugin fault guard and automatic detachment as join/leave.

`Players.TryGetConnectionSummary(observedPlayer, out summary)` returns a detached,
non-sensitive summary only when its world and session ID still match; it includes
the client-reported platform, connection timestamp and optional login phase, not
endpoints, UUID, platform account names or credentials. `summary.ConnectionAge(now)`
is wall-clock connection age, not authenticated or active game time.
`Players.SnapshotPlatformCounts(worldId)` returns read-only online counts per
reported platform, including `Unknown`. Neither API attests the device or OS.

`plugin.Players.Actions` provides typed native player operations: `HealAsync`,
`BuffAsync`, `KillAsync`, `SlapAsync`, `RespawnAsync`, and `GiveAsync` (ordinary
native item delivery). Check `VitalsAvailable` / `ItemDeliveryAvailable` on hosts
that omit these services. Each method requires an exact current `PlayerSnapshot`
with a session ID; a stale snapshot or different world is rejected, never
redirected to a later occupant of a slot. Native services validate
item/buff identifiers, duration, stack/prefix and current character state, and
apply existing content restrictions to item delivery. The host links operations
to the activation lifetime, tracks pending work during retirement and cancels
not-yet-delivered requests on unload. `Applied` means the native operation was
issued; death, hurt and respawn requests do not by themselves acknowledge the
client's eventual game-state transition. No raw item/packet overrides are exposed.

`plugin.Players.Actions.CustomItems` is the activation-local registry for **named
native item recipes**. Check `ItemDeliveryAvailable` and `CustomItems.IsAvailable`
before registering. A `CustomItemRecipe` sets the native item type, stack and
native prefix; definitions are case-insensitively unique within one activation,
are not shared with other plugins, and are released on unload. `Snapshot()` is
detached. The native delivery path still checks actual item/prefix IDs, maximum
stack, currently bound player session, and content restrictions. Pending delivery
is canceled and drained on retirement; a reconnect reusing the slot does
not redirect an existing request to the new session.

```csharp
if (plugin.Players.Actions.ItemDeliveryAvailable && plugin.Players.Actions.CustomItems.IsAvailable)
{
    plugin.Players.Actions.CustomItems.Register(new CustomItemRecipe("starter-torches", 8, 20));
    PlayerOperationResult result = await plugin.Players.Actions.CustomItems.GiveAsync(player, "starter-torches", cancellationToken);
}
```

`CustomItems.SupportsWorldItemStats` additionally indicates native, transient
world-item stat delivery. For example:

```csharp
if (plugin.Players.Actions.CustomItems.SupportsWorldItemStats)
{
    plugin.Players.Actions.CustomItems.Register(new CustomItemRecipe("custom-sword", 24)
    {
        WorldStats = new CustomWorldItemStats { Damage = 40, UseTime = 20 }
    });
    await plugin.Players.Actions.CustomItems.GiveAsync(player, "custom-sword", cancellationToken);
}
```

Supported fields are color, damage, knockback, use animation/time, existing
projectile speed, and scale; native template and finite-range validation apply.
Gameplay overrides require a single nonstackable native item. Projectile and
ammo identity, geometry, and arbitrary packet overrides are not exposed.
Delivery uses the native world item plus native `NetMessage` sync/tweak, not a
direct socket sender. Stock inventory/SSC, trading, death and reconnect have no
durable identity for arbitrary custom stats; these are **transient world-item
stats**, not promises of permanent or authoritative per-weapon combat stats.
Persist recipe definitions in plugin configuration and register them again on
activation when needed.

For persistent, versioned virtual custom items with exact account ownership,
atomic transfer and permanent revocation, use [Custom item instances](plugin-custom-item-instances.md).
This deliberately does not attach arbitrary persistent stats to physical Terraria items.

### Player activity, command/chat policy and regions

Check `plugin.Activity.AuthenticationAvailable`, `PublicChatAvailable` and
`CommandCompletionAvailable` before subscribing on hosts that omit these services.
`OnAuthenticationEstablished` reports an account ID, player/session ID and exact
authentication tenure ID; use `TryGetAuthentication(playerSnapshot, out binding)`
to check the current tenure. `OnAuthenticationEnded` reports that same historical
tenure with `Logout`, `Disconnected` or `SessionReplaced`. Neither exposes credentials.
Obsolete queued establishments are dropped. An ended tenure must never be treated as
authentication for a newer connection on the same slot.

`OnPublicChatAccepted` reports common transformed text, exact sender session,
world and audience count after the chat pipeline accepts an ordinary public
message, before recipient-specific rendering/delivery is confirmed. It excludes
rejected messages and slash/dot command-like input. `OnCommandCompleted` reports
the canonical command name, world, exact player session (or server console) and
returned status, not arguments, output, or credential data. Dispatch-level
rejections, exceptions and commands lacking an exact player session are omitted.
Both are historical observations rather than authorization grants or delivery
receipts. The events queue bounded game-thread work; under queue saturation
observations can be dropped. All subscriptions are guarded and detached on unload.

`plugin.Commands.CatalogAvailable` / `Snapshot()` provides detached command
descriptors (canonical names, aliases, permissions, server-availability and syntax)
including dynamic plugin registrations. Discovery does not grant dispatch permission.

`plugin.Activity.PlayerCommandPolicyAvailable` and `PublicChatPolicyAvailable`
enable two synchronous, world-local policy hooks. `BeforePlayerCommand` runs
only for an exact live player session after ordinary dispatch permission checks:
return `CommandPolicyDecision.Allow` or `CommandPolicyDecision.Reject(reason)`.
The context includes canonical and invoked command names, but never arguments,
credentials, the executing principal, or the output handle. No console or REST
operator commands are intercepted. Rejection is final and is not a completed
execution observation.

`BeforePublicChat` receives ordinary public text, exact player/session/world and
audience count. Return `PublicChatPolicyDecision.Allow`, `Replace(content)` or
`Reject(reason)`. Replacements must be non-empty ordinary text of at most 1024
characters, without control characters or command prefixes. The next subscriber
sees the previous replacement, and all replacements still pass through the
server's existing transforms and moderation (including mutes). Policies cannot
change the sender/recipients or approve a message rejected by built-in policy.
Private messages and slash/dot command-like input are not public chat.

Policies run inline on the originating command/chat thread; keep them short and
thread-safe, use cached data, never wait for game-thread work or external I/O.
Use the existing deferred observations for analytics or background work. Callback
exceptions (including invalid replacements) are isolated and counted by the plugin
fault breaker; that callback's decision is ignored, never bypassing server checks.
Retirement detaches policies and drains in-flight guarded callbacks.

```csharp
if (plugin.Activity.PlayerCommandPolicyAvailable)
    plugin.Activity.BeforePlayerCommand(ctx =>
        ctx.CommandName.Equals("spawn", StringComparison.OrdinalIgnoreCase) && !CanSpawn(ctx.PlayerId)
            ? CommandPolicyDecision.Reject("Spawn is unavailable during this event.")
            : CommandPolicyDecision.Allow);

if (plugin.Activity.PublicChatPolicyAvailable)
    plugin.Activity.BeforePublicChat(ctx =>
        ctx.Content.Contains("example", StringComparison.OrdinalIgnoreCase)
            ? PublicChatPolicyDecision.Replace(ctx.Content.Replace("example", "sample", StringComparison.OrdinalIgnoreCase))
            : PublicChatPolicyDecision.Allow);
```

`plugin.World.Regions.IsAvailable` indicates read-only region support.
`Snapshot()` returns detached regions of the current world; `FindTop(tileX, tileY)`
uses the host's existing region priority/ranking. ACL members, groups and flags
are copied into immutable collections. Region mutation and change notifications
are intentionally not exposed by this read-only snapshot capability.

`plugin.Identity` exposes credential-free `FindAccountAsync(name)` (account ID,
name and stored group only), `TryGetAuthenticatedAccount(player, out account)`
and detached group snapshots via `SnapshotGroupsAsync` and `FindGroupAsync`.
Check availability before calling. `GroupHasPermissionAsync` uses the existing
inherited group authorization evaluator; `TryHasAuthenticatedPermission` uses
the exact current authentication tenure and effective player permission service,
including temporary group leases. A failed `Try` means no exact current authenticated
binding, distinct from a valid lookup returning `allowed = false`. These reads
are observations, not authorization leases for later mutation. Account identity
does not expose passwords, credential hashes, IP addresses, culture or last seen.
Group/account I/O is dispatched off the game thread and pending reads are tracked
and cancelled at activation retirement.

`plugin.Warps.SnapshotAsync()` returns an immutable public catalog and persistent
world-file identity, or `null` when the world has no current persistent identity.
`IsAvailable` indicates host support. Hidden warps are omitted. The reader uses
the same bounded warp store as the navigation module and rechecks the persistent
world identity after asynchronous file I/O; no arbitrary world ID, mutation or
teleport override is available through this facet.

For extra resources created by a plugin, register ownership once with `plugin.Resources.Own(resource)`
(`IDisposable`), `OwnAsync(resource)` (`IAsyncDisposable`) or `OnCleanup(action)`. The host drains callbacks
and lifetime work first, then retires resources in reverse registration order together with existing
registrations. Cleanup runs on the retirement path rather than the game thread; failures are reported as
incomplete retirement, but do not skip the remaining resources. Awaitable disposal participates in the
unload timeout. Do not register the same resource twice or rely on an unbounded background task to stop.

## World capabilities

`plugin.World` is the ordinary gameplay boundary. It never returns `Terraria.Main`, `Terraria.NPC`, `Terraria.Chest` or mutable tile references. Read operations return immutable snapshots; mutations are dispatched by the host to the authoritative game thread.

### Native gameplay events

```csharp
plugin.WorldEvents.OnNpcKilled(e =>
    plugin.Log.Write(OperationsLogLevel.Information,
        $"NPC {e.Npc.Type} generation {e.Npc.Generation} died in {e.Npc.WorldId}."));
plugin.WorldEvents.OnTickCompleted(e => RecordTickDuration(e.Duration));
plugin.WorldEvents.OnSaveCompleted(e => RecordSaveResult(e.Succeeded, e.FailureType));
plugin.WorldEvents.AllowNpcTransform(e => e.RequestedType != restrictedType);
plugin.WorldEvents.AllowNpcLoot(e => e.Stack <= configuredStackLimit);
```

The other notifications are `OnNpcCreating` (native allocation request, not
confirmation), `OnNpcTransforming` (approved prospective transformation),
`OnNpcLootDropped` (actual allocated world item), `OnProjectileSynchronized`
and `OnProjectileRemoved` (native ingress notifications, not a complete
projectile lifetime census). `OnNpcStrikeCompleted` runs only after native
`StrikeNPC` returns a positive damage result and its finally block has completed;
it reports requested damage, native returned damage, critical flag and a detached
post-strike NPC snapshot. Returned damage is not necessarily the life decrement
for shared-life/immortal NPCs. It is neither a cancellation policy nor an
attacker/session identity grant. All carry detached values and the current world
identity. NPC native generation is distinct from the optimistic mutation
revision in `NpcHandle`; capture a current handle through `World.Npcs` before
requesting a later mutation.

`OnNpcStrikeCompletedWithSource` adds detached observational caller context to
the same completion result (`e.Strike`). `e.Source.Kind` distinguishes player,
projectile, other and unknown contexts. `PlayerId` and `SessionId` are present
together only when a player reference is resolved to the exact active local
native session, including the current socket. A projectile snapshot carries
its observed key, slot, type, owner and position; that owner is never converted
to a player session by this event. Neither context proves valid damage, an
authenticated attacker or projectile ownership. The source-less event remains
available; subscribe to one or the other if only one notification is needed.

Notifications and decisions run synchronously at the native world-thread
boundary, only when subscribed. Keep callbacks short and post follow-up
mutations through `Tasks.Post` rather than re-entering an in-progress native
operation. A policy returning `false` vetoes the requested operation; a faulting
policy is isolated and does not veto. The existing per-plugin circuit breaker
and automatic registration cleanup cover these delegate surfaces.

`OnTickCompleted` reports elapsed native update work before the server tick's
pacing sleep; it is not a scheduler deadline or an invitation to perform expensive
work each tick. `OnSaveCompleted` reports the native save result, with only an
exception type name when available. Neither notification can cancel the completed
operation. Both are world-local and execute on the owning game thread.
Worker-thread save completions are posted to the existing bounded dispatcher;
notifications can be dropped when it is full or the adapter is stopping. Subscribers
are resolved when that queued notification is delivered, and retired registrations
cannot be invoked. With no subscribers, no event snapshot or queued dispatch is
created. Startup-ready and stopping callbacks are not exposed by this facade yet.

### Player state observations

`OnPlayerHurtCompleted` observes each positive return from the native `Player.Hurt` method, in addition to net post-tick vital changes. The victim is identified by exact local session and the event includes native damage return, before/after life, and native PvP/critical arguments. Native melee execution can provide an exact matching local attacking player session; a matching native projectile execution provides detached projectile identity, not a verified player owner. Other calls are explicitly `Unverified` (including client-reported damage processed by Terraria). `DeclaredPlayerSlot` is only the death reason's source claim; never promote it to attacker identity or use it for authoritative rewards. `NativeDamage` may differ from the actual life change, especially when the native result represents client synchronization. No cancellation, damage rewriting or arbitrary packet access is provided. Native HookGen availability is verified by the host and a hook installation failure leaves vanilla combat intact with a diagnostic.

For session statistics and role-play, the world event facade additionally exposes
`OnPlayerVitalsChanged`, `OnPlayerDeathObserved`, `OnPlayerRespawnObserved`,
`OnPlayerDeathCountersChanged`, `OnPlayerPvpChanged`, and `OnPlayerBuffsChanged`.
They sample live native state on the game thread after dedicated tick completion,
only while a relevant observer is registered. Each observation includes the
world, player slot, exact current connection session ID, timestamp, and detached
state (life/max life, mana/max mana, dead/hostile/team and native death counters).
The first sample for a connection is a baseline; disconnects, incomplete joins,
slot reuse or skipped sessions never compare two different tenures.

```csharp
plugin.WorldEvents.OnPlayerVitalsChanged(change =>
    RecordLifeDelta(change.After.PlayerId, change.After.SessionId, change.LifeDelta));

plugin.WorldEvents.OnPlayerDeathObserved(death =>
    RecordDeath(death.After.PlayerId, death.After.SessionId, death.Kind));

plugin.WorldEvents.OnPlayerBuffsChanged(change =>
    RecordBuffMembership(change.Player.PlayerId, change.After));
```

Vital deltas are net changes between observed ticks, not individual combat hits
or verified healing. Death/respawn report native dead-state transitions (not a
requested kill or respawn); `Kind` is `Pve`/`Pvp` only if the corresponding native
counter increases and the other does not change in the same sample, otherwise
`Unknown`. Counter changes are also separately observable. Terraria synchronizes
some of this state from clients; do not treat the observations as proof of damage,
killer identity, PvP authorization, or entitlement to rewards. Use them together
with exact authentication tenures and the existing native NPC strike observations
for analytics. Buff changes describe sorted sets of native buff types; countdown,
refresh and slot reordering do not generate membership changes. No mutable native
arrays, raw packets or damage override interfaces are exposed. All callbacks use
the same guarded, lifetime-managed world event subscriptions.

### Portable schematics

```csharp
var result = await plugin.World.Schematics.CaptureAsync(new SceneBounds(100, 200, 80, 60), cancellationToken);
if (result.Status == WorldSchematicCaptureStatus.Captured)
{
    // result.Data is a detached .trschem document; use SchematicBinary from the bundled codec.
    await File.WriteAllBytesAsync(Path.Combine(plugin.Storage.DataDirectory, "arena.trschem"),
        result.Data.ToArray(), cancellationToken);
}
```

Use a host-owned async task for long-lived background operations. The SDK shares
the world process's bounded schematic lane with operator commands and returns
`Busy` rather than admitting unlimited snapshots. `PlaceAsync` currently
materializes tiles, NPCs, supported native containers/signs, all eight v1 typed
tile entities and ephemeral dropped world items in an idle, isolated sandbox
only. Unsupported records are refused;
keep the supplied encoded memory unchanged until completion.
See [schematics](schematics.md) for exact placement constraints and support.

### Custom NPC actors

`plugin.World.Npcs.CreateActorAsync` produces a tracked native NPC actor lease.
Its reusable definition selects a vanilla presentation, name, friendliness,
immortality, movement mode and optional shop, dialogue and item-quest catalogs.
Read-only guarded actor events and bounded patrol intents provide programmatic
behavior without replacing native motion. See
[custom NPCs and shops](custom-npcs.md) for actor commands, conversation events,
stock shop presentation and explicit server inventory purchases.

```csharp
WorldEnvironmentSnapshot? state = await plugin.World.Environment.CaptureAsync();
IReadOnlyList<NpcSnapshot> npcs = await plugin.World.Npcs.SnapshotAsync();
WorldTileAreaSnapshot? tiles = await plugin.World.Tiles.CaptureAsync(
    new SceneBounds(100, 200, 20, 10));
ChestSnapshot? chest = await plugin.World.Chests.CaptureAsync(120, 220);
```

Mutable resources use optimistic revisions. A plugin changes the exact snapshot it observed; if Terraria or another plugin changed the resource first, Vega returns a revision conflict instead of applying a stale mutation.

```csharp
if (chest is not null)
{
    ChestMutationResult result = await plugin.World.Chests.ReplaceAsync(
        chest,
        chest.Items,
        "Event rewards");
}

PlayerInventorySnapshot? inventory = await plugin.Players.Inventory.CaptureAsync(playerId);
if (inventory is not null)
{
    InventoryMutationResult result = await plugin.Players.Inventory.ReplaceSlotAsync(
        inventory,
        new InventorySlotReplacement(0, itemType: 1, stack: 100));
}
```

`World.Environment` exposes dimensions, spawn, day/night time, moon phase, blood moon, eclipse, rain, wind and hardmode state. Its validated patch surface can change time and weather/event flags but deliberately does not make hardmode a generic boolean assignment.

`World.Npcs` supports snapshot, guarded capture, spawn, teleport and despawn. NPC handles carry world, slot, type and revision so a stale slot cannot accidentally mutate a different NPC after Terraria reuses it.

`World.Tiles` captures bounded `SceneTileArea` values and atomically replaces an observed area. `World.Chests` uses the same snapshot/revision model. `World.Scenes.Create` returns an `ISceneLease` owned by the plugin lifetime; the host automatically disposes the lease during unload.

The active `WorldId` follows the process. A primary process exposes `WorldId.Primary`; a sandbox backend exposes its own world id. Cross-world lifecycle belongs to `plugin.Sandboxes`, not to the live world facade.

## Managed configuration

Every plugin can create its own configuration without DEBUG privileges. Vega owns the conventional location:

```text
config/plugins/<PluginName>/
```

Use `Configuration.Use<T>` to register a typed settings file:

```csharp
public sealed record Settings(
    bool Enabled = true,
    int IntervalSeconds = 30);

private IPluginConfig<Settings>? settings;

protected override void Configure(IVegaPluginContext plugin)
{
    settings = plugin.Configuration.Use(
        "settings.json",
        static () => new Settings(),
        static value => value.IntervalSeconds > 0
            ? null
            : "IntervalSeconds must be positive.");

    Settings current = settings.Current;
}
```

If the file is missing, Vega validates and writes the default value. `Reload()` re-reads and validates the file. `Save(value)` validates the supplied value and atomically replaces the file; `Save()` persists the current value.

Configuration paths are relative to the plugin configuration directory and cannot escape it. Nested relative files are allowed.

Use `Storage` for arbitrary plugin data. Use `Configuration` for operator-editable settings that need defaults, validation and reload semantics.

Trusted DEBUG tooling can enumerate registered plugin configurations and read, merge-patch, replace or reload them. DEBUG writes require the `StateMutation` capability and still pass through the owning plugin's typed deserializer and validator before an atomic save.

## Sandboxes

Every plugin receives the `Sandboxes` facade:

```csharp
IReadOnlyList<SandboxStatus> worlds = plugin.Sandboxes.Snapshot();

if (plugin.Sandboxes.CanControl)
{
    await plugin.Sandboxes.RestartAsync(new WorldId("event-world"));
}
```

The facade exposes `Snapshot`, `CreateAsync`, `StartAsync`, `StopAsync`, `RestartAsync`, `ResetAsync` and `DestroyAsync`.

`CanControl` reports whether the current process owns the sandbox control plane. Plugin code should use that capability instead of inferring authority from process role or filesystem layout.

## Async I/O

Use `Tasks.RunIo` only for genuine database, HTTP or file I/O. The operation runs without Terraria access; the `apply` callback is posted to the authoritative game thread:

```csharp
plugin.Tasks.RunIo(
    "load-motd",
    cancellationToken => File.ReadAllTextAsync(path, cancellationToken),
    motd => plugin.Log.Write(OperationsLogLevel.Information, motd));
```

Do not capture mutable Terraria state in the I/O callback. Its synchronous prefix also runs on a worker, not the caller's game thread. Cancellation, exception logging and unload joining belong to the host.

For pure CPU work on detached snapshots, use `Tasks.TryRunCpu(name, compute, apply)`. It returns `false` rather than building an unbounded backlog. At most four admitted jobs per plugin and 32 per world process share up to four CPU lanes (one CPU reserved where possible). Results are posted through the bounded game-thread dispatcher; retirement cancels waiting work, suppresses late results and joins cooperative active work. Capture immutable data on the owning thread before calling and respect cancellation inside the computation. This is not an authorization to run native simulation in parallel.

## DEBUG boundary

`Debug` is not required for ordinary gameplay plugins. The host always supplies a debug context, but its capabilities are `None` unless the plugin is explicitly trusted in `config/debug-api.json`.

DEBUG is intended for diagnostic tooling such as `VegaToolsMCP`. It includes process/runtime inspection, bounded packet tracing and privileged state operations described in [debug-api.md](debug-api.md).

## Advanced boundary and migration

Low-level `IPacketProcessor<T>` and packet-security contracts remain separate from ordinary gameplay APIs. They require explicit pure/concurrent/buffer-lifetime reasoning and are not registered by the normal convenience facade. Dynamic lifecycle specialists may implement `IHotReloadableVegaModule` directly, but then own the extra ceremony intentionally.

Generic cancellable gameplay hooks are not fabricated by the SDK. They are added only where Vega owns an authoritative event pipeline with deterministic plugin registration and unload semantics. Player join/leave already use that rule; low-level packet processing remains the advanced boundary.

`samples/Vega.AdvancedPacketPlugin` shows the separate typed validator shape and states its concurrency/buffer rules in code. It is deliberately not referenced by the ordinary template or usability sample.

Typical TShock patterns map by behavior, not API shape:

| TShock-style intent | Vega golden path |
| --- | --- |
| plugin base plus `Initialize` | `VegaPlugin` plus `Configure` |
| add/remove chat command | `plugin.Commands.Register`; host removes it |
| player greet hook | `plugin.Players.OnJoined` plus `Message` |
| `TSPlayer` lookup/mutation | immutable snapshot lookup plus narrow player operation |
| player inventory edit | `plugin.Players.Inventory` snapshot + optimistic mutation |
| world/NPC/chest edit | `plugin.World` immutable snapshots + guarded mutations |
| timer and manual dispose | `plugin.Tasks.Every`; host cancels it |
| background I/O then main-thread callback | `plugin.Tasks.RunIo` |
| plugin configuration | `plugin.Configuration.Use<T>` |
| arbitrary plugin data | scoped `plugin.Storage` JSON |
| managed world lifecycle | `plugin.Sandboxes` |

There is no source or binary compatibility with TShock. The mapping preserves the behavior while keeping Terraria globals and runtime internals out of ordinary plugin code.

Ordinary managed exceptions from commands, player lifecycle callbacks, posted game-thread actions and asynchronous SDK I/O are attributed to the owning plugin, logged with the original exception, and isolated from the shared dispatcher. A per-activation breaker defaults to five faults within 60 seconds (`faultThreshold`, `faultWindowSeconds` in `config/modules.json`). Automatic and explicit retirement both stop new guarded callbacks, cancel owned work and wait for already running callbacks/tasks within `unloadTimeoutMilliseconds`. The host also bounds waiting for stop/disposal, tracks the outstanding cleanup rather than starting it twice, and prevents another generation of the same plugin from activating until retirement and collectible context reclamation are verified. Timeout produces a degraded plugin and `restart required`; unrelated plugin management remains available. An operator can retry retirement after outstanding work completes, but the restart warning remains conservative. Operations/TUI exposes the fault count and latest operation/failure. The fixture `ServerPluginHotReloadHostTests` verifies independent real collectible assemblies across 32 fault retirements, 160 exceptions, 64 synthetic reconnects and continuing periodic work, as well as bounded non-cooperative retirement. This is lifecycle isolation, not a hostile-code security sandbox; a stuck arbitrary plugin or finalizer, fatal CLR/native failures and corrupted authoritative state still require process supervision and last-known-good persistence.

## Plugin-facing FakeProvider

`context.World.FakeProvider` is a first-class SDK capability. It accepts immutable `SceneTileArea` data, creates global/personal `IPluginTileProvider` leases, and exposes `Move`, `Replace`, `SetLayer`, `SetTop`, `SetEnabled`, `SetObservers`, `Draw`, and `Dispose`. Personal providers accept `SceneSessionObserver.From(playerSnapshot)` or `.From(playerSessionOpened)` so previous connections cannot reappear on a reused slot. A single tracked owner releases all active providers and visibility layers on hot unload/reload; early disposal releases transient previews immediately. Names are descriptive, not cross-plugin lookup keys. `CreateVisibilityLayer` binds player observers and assignments to active sessions. NPC/projectile slots are native IDs and callers must remove assignments when their entity despawns. This is scene visibility, not full independent simulation or automatic entity resync. `CreatePersonal(..., fillOutsideWithAir: true)` and `SetFillOutsideWithAir(true)` enable a personal air background with cooperative refresh of loaded sections. This is visual only: canonical section data is still transmitted. Provider-owned logical chests are available through `SupportsVirtualChests` and `IPluginTileProvider.CreateVirtualChest(localX, localY, name, items)`. The handle supports revision-checked replacement, disposal and bounded exact-session `TryTakeOpenRequest`. `SupportsVirtualChestTransfers` and `Transfer(SceneVirtualChestTransferRequest)` allow explicit native inventory stack exchange on the game thread with authoritative SSC, exact-session/reach/visibility/revision checks and native item validation. `SupportsManagedVirtualChestGui` / `TryPresentManagedGui` provide an opt-in stock chest display with exact-session server-managed inventory until disconnect. Balanced one-slot native GUI movements become explicit `TryTakeManagedGuiTransfer` / `AcceptManagedGuiTransfer` intents; unsupported operations close and resynchronize. This constrained mode is for plugin-controlled minigame inventory, not unrestricted vanilla inventory. Graphical interaction qualification remains open. Logical signs use `SupportsVirtualSigns`, `IPluginTileProvider.CreateVirtualSign(localX, localY, text)`, `TrySetText` with a revision and `TryTakeReadRequest`. `SupportsVirtualSignEdits` and `CreateVirtualSign(..., allowEdits: true)` opt into edit requests after a current exact-session read; the plugin explicitly accepts via `TryTakeEditRequest` and `TryAcceptEdit` with revalidation. Provider lifetime, world/session visibility and footprint limits apply to both object types. When responding to an intent, use `plugin.Players.Message(playerSnapshot, text)` or `MessageAsync(playerSnapshot, text)`; the host rechecks the session and refuses unsupported exact delivery instead of using a numeric slot. `SupportsVirtualItemFrames`, `CreateVirtualItemFrame` and `TryReplace` provide read-only item-frame metadata rendered to exact sessions without canonical registration. `SupportsVirtualSignUi` and `TryPresentRead` send the stock opening response without canonical storage; `TryRefreshEdit` refreshes text without reopening, with a bounded exact-session index binding. `SupportsVirtualDisplays` and `CreateVirtualDisplay` provide read-only weapon racks, mannequins, hat racks and food platters through native metadata. `SupportsManagedDisplayInteractions` and `TryArmManagedInteraction` provide opt-in managed-SSC placement into an empty item frame or weapon rack. `TryTakeManagedTransfer` / `AcceptManagedTransfer` atomically commit a balanced inventory decrease and the exact display revision; rejection/cancellation resynchronizes. The managed scope lasts until disconnect. Unrestricted native chest UI, automatic display withdrawal, mannequin/hat-rack inventory interactions and graphical-client qualification remain open. See [scene contracts](scenes.md) and `samples/Vega.PluginSamples/VirtualChestPreviewCommand.cs`. Existing `World.Scenes.Create` remains available. See the Russian SDK guide for a region-copy preview example.
