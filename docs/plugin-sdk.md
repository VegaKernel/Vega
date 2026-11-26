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
- `Players` provides immutable online snapshots, lookup, messaging, kick/teleport operations, join/leave hooks and optimistic inventory access;
- `World` exposes the active world through immutable environment, tile, chest and NPC snapshots plus plugin-owned scenes;
- `Tasks` posts to the game thread, schedules repeating game-thread work and runs async I/O with a game-thread apply callback;
- `Configuration` owns typed, validated and reloadable operator settings under `config/plugins/<plugin-name>/`;
- `Storage` stores arbitrary plugin-owned JSON beneath `data/plugins/<plugin-name>/`;
- `Log` writes structured records under the plugin's own source name;
- `Sandboxes` discovers managed worlds and, on the control-plane process, exposes sandbox lifecycle operations;
- `Debug` is a separately gated process-internal diagnostic surface for explicitly trusted tooling plugins.

The host owns every command, event subscription, repeating task and async operation created through these facets. Unload first cancels and joins background work, then removes registrations before releasing the collectible context. Ordinary plugins do not keep registration leases or reverse-registration lists.

## World capabilities

`plugin.World` is the ordinary gameplay boundary. It never returns `Terraria.Main`, `Terraria.NPC`, `Terraria.Chest` or mutable tile references. Read operations return immutable snapshots; mutations are dispatched by the host to the authoritative game thread.

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

Ordinary managed exceptions from commands, player lifecycle callbacks, posted game-thread actions and asynchronous SDK I/O are attributed to the owning plugin, logged with the original exception, and isolated from the shared dispatcher. A per-activation breaker defaults to five faults within 60 seconds (`faultThreshold`, `faultWindowSeconds` in `config/modules.json`). It quarantines new guarded callbacks, cancels owned work, waits for already running callbacks/tasks within the unload deadline, and uses the existing hot-unload path to detach registrations and verify collectible context reclamation. If the pre-drain cannot finish, the plugin stays quarantined and Operations reports restart required. Ordinary healthy callbacks remain available. Operations/TUI exposes the fault count and latest operation/failure. `AssemblyLoadContext` is lifecycle isolation, not a hostile-code security sandbox; fatal CLR/native failures, non-cooperative plugin stop/disposal and corrupted authoritative state require process supervision and last-known-good persistence.

## Plugin-facing FakeProvider

`context.World.FakeProvider` is a first-class SDK capability. It accepts immutable `SceneTileArea` data, creates global/personal `IPluginTileProvider` leases, and exposes `Move`, `Replace`, `SetLayer`, `SetTop`, `SetEnabled`, `SetObservers`, `Draw`, and `Dispose`. Personal providers accept `SceneSessionObserver.From(playerSnapshot)` or `.From(playerSessionOpened)` so previous connections cannot reappear on a reused slot. A single tracked owner releases all active providers and visibility layers on hot unload/reload; early disposal releases transient previews immediately. Names are descriptive, not cross-plugin lookup keys. `CreateVisibilityLayer` binds player observers and assignments to active sessions. NPC/projectile slots are native IDs and callers must remove assignments when their entity despawns. This is scene visibility, not full independent simulation or automatic entity resync. `CreatePersonal(..., fillOutsideWithAir: true)` and `SetFillOutsideWithAir(true)` enable a personal air background with cooperative refresh of loaded sections. This is visual only: canonical section data is still transmitted. Virtual entity/chest storage is not implemented. Existing `World.Scenes.Create` remains available. See the Russian SDK guide for a region-copy preview example.
