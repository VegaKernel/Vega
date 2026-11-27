# First Vega plugin

An ordinary plugin needs one NuGet package, one base class and no knowledge of Terraria, TerrariaRuntime, packet buffers, worker lanes or collectible load contexts.

## Create and build

From a Vega checkout, install the maintained template and create a project:

```powershell
dotnet new install ./templates/vega-plugin
dotnet new vegaplugin --name MyPlugin
dotnet restore MyPlugin/MyPlugin.csproj
dotnet build MyPlugin/MyPlugin.csproj --configuration Release
```

The generated project references the single `Vega` package at the repository's current prerelease baseline. Update that version together with the server/package version when moving the template to another release. Copy only `MyPlugin.dll` to the server's top-level `ServerPlugins/` directory. Enable administrative hot reload in `config/modules.json`, or restart the server after deployment when hot reload remains disabled.

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
        plugin.Commands.Register(new HelloCommand());
        plugin.Players.OnJoined(player =>
            plugin.Players.Message(player.PlayerId, "Hello from MyPlugin!"));
    }
}
```

The fixed context exposes these discoverable facets:

- `Commands` registers typed command handlers;
- `Players` provides immutable online snapshots, lookup, messaging, kick/teleport operations, join/leave/world-entry/pre-reset hooks and optimistic inventory access;
- `World` exposes the active world through immutable environment, tile, chest and NPC snapshots plus plugin-owned scenes;
- `WorldEvents` registers world-local gameplay notifications and narrowly scoped NPC transformation/loot policies;
- `Tasks` posts to the game thread, schedules repeating game-thread work and runs async I/O with a game-thread apply callback;
- `Resources` owns explicitly transferred disposable or asynchronous resources and cleanup actions for this activation;
- `Configuration` owns typed, validated and reloadable operator settings under `config/plugins/<plugin-name>/`;
- `Storage` stores arbitrary plugin-owned JSON beneath `data/plugins/<plugin-name>/`;
- `Log` writes structured records under the plugin's own source name;
- `Sandboxes` discovers managed worlds and, on the control-plane process, exposes sandbox lifecycle operations;
- `Debug` is a separately gated process-internal diagnostic surface for explicitly trusted tooling plugins.

The host owns every command, event subscription, repeating task and async operation created through these facets. Unload first cancels and joins background work, then removes registrations before releasing the collectible context. Ordinary plugins do not keep registration leases or reverse-registration lists.

`Players.OnEnteredWorld` emits the native game-thread world-entry snapshot with its exact session ID.
`Players.OnClosing` runs before the native character is reset, while the old session still exists;
`OnLeft` remains the subsequent session removal notification. These callbacks are notifications, not
cancellation policies, and all use the same per-plugin fault guard and automatic detachment as join/leave.

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
plugin.WorldEvents.AllowNpcTransform(e => e.RequestedType != restrictedType);
plugin.WorldEvents.AllowNpcLoot(e => e.Stack <= configuredStackLimit);
```

The other notifications are `OnNpcCreating` (native allocation request, not
confirmation), `OnNpcTransforming` (approved prospective transformation),
`OnNpcLootDropped` (actual allocated world item), `OnProjectileSynchronized`
and `OnProjectileRemoved` (native ingress notifications, not a complete
projectile lifetime census). All carry detached values and the current world
identity. NPC native generation is distinct from the optimistic mutation
revision in `NpcHandle`; capture a current handle through `World.Npcs` before
requesting a later mutation.

Notifications and decisions run synchronously at the native world-thread
boundary, only when subscribed. Keep callbacks short and post follow-up
mutations through `Tasks.Post` rather than re-entering an in-progress native
operation. A policy returning `false` vetoes the requested operation; a faulting
policy is isolated and does not veto. The existing per-plugin circuit breaker
and automatic registration cleanup cover all eight new delegate surfaces.

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

Do not capture mutable Terraria state in the I/O callback. Cancellation, exception logging and unload joining belong to the host.

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
