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
- `Players` provides immutable online snapshots, lookup, messaging, kick/teleport/heal/kill/buff/respawn operations, join/leave hooks and optimistic inventory access;
- `World` exposes the active world through immutable environment, tile, chest and NPC snapshots plus plugin-owned scenes;
- `Tasks` posts to the game thread, schedules repeating game-thread work and runs async I/O with a game-thread apply callback;
- `Configuration` owns typed, validated and reloadable operator settings under `config/plugins/<plugin-name>/`;
- `Storage` stores arbitrary plugin-owned JSON beneath `data/plugins/<plugin-name>/`;
- `Log` writes structured records under the plugin's own source name;
- `Sandboxes` discovers managed worlds and, on the control-plane process, exposes sandbox lifecycle operations;
- `Debug` is a separately gated process-internal diagnostic surface for explicitly trusted tooling plugins.

The host owns every command, event subscription, repeating task and async operation created through these facets. Unload first cancels and joins background work, then removes registrations before releasing the collectible context. Ordinary plugins do not keep registration leases or reverse-registration lists.

## Logging

Every ordinary plugin has access to the shared Vega operations logger through `plugin.Log`:

```csharp
plugin.Log.Write(
    OperationsLogLevel.Information,
    "Cache rebuilt successfully.");
```

The SDK deliberately exposes the scoped `IPluginLog` facade instead of the raw host logger. Vega fixes the source to `plugin:<PluginName>`, so a plugin cannot impersonate `runtime`, `packet-security`, `chat` or another subsystem. The plugin owns the message contents and must not place credentials or other secrets in them.

Plugin records use the same logging transport as the server. The bounded in-memory store used by operations/TUI is updated immediately, while potentially blocking sinks such as console and JSON files are delivered through the bounded background queue. A slow sink therefore does not turn a plugin log call into game-thread I/O, and queue overflow drops queued-sink records rather than blocking the server.

## Player capabilities

`plugin.Players` exposes immutable `PlayerSnapshot` values for reads and narrow authoritative operations for mutations. Ordinary plugins do not receive `Terraria.Player`, `Main.player` or another mutable Terraria object graph.

```csharp
PlayerSnapshot? target = plugin.Players.Online
    .FirstOrDefault(player => player.Name == "Alice");

if (target is not null)
{
    await plugin.Players.HealAsync(target.Id);
    await plugin.Players.ApplyBuffAsync(target.Id, buffType: 2, durationTicks: 600);
}
```

Supported authoritative operations are:

- `MessageAsync`;
- `KickAsync`;
- `TeleportAsync`;
- `HealAsync`, where a null amount restores current maximum life;
- `KillAsync`, with an explicit auditable death reason;
- `ApplyBuffAsync`, with a validated Terraria buff type and duration in ticks;
- `RespawnAsync` for a dead player.

The host marshals these mutations to the authoritative game thread. The same `IPlayerOperations` implementation is used by built-in administration commands, Plugin SDK and higher-level tooling, so plugins do not need a parallel direct-mutation path.

Inventory is intentionally separate. It uses an optimistic snapshot/revision contract because replacing item slots safely has different stale-state semantics than a one-shot player action.

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
| `TSPlayer` lookup/mutation | immutable snapshot lookup plus narrow `Message/Kick/Teleport/Heal/Kill/Buff/Respawn` operations |
| player inventory edit | `plugin.Players.Inventory` snapshot + optimistic mutation |
| world/NPC/chest edit | `plugin.World` immutable snapshots + guarded mutations |
| timer and manual dispose | `plugin.Tasks.Every`; host cancels it |
| background I/O then main-thread callback | `plugin.Tasks.RunIo` |
| plugin configuration | `plugin.Configuration.Use<T>` |
| arbitrary plugin data | scoped `plugin.Storage` JSON |
| managed world lifecycle | `plugin.Sandboxes` |

There is no source or binary compatibility with TShock. The mapping preserves the behavior while keeping Terraria globals and runtime internals out of ordinary plugin code.
