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
- `Players` provides immutable online snapshots, lookup, messaging, kick/teleport operations and join/leave hooks;
- `Tasks` posts to the game thread, schedules repeating game-thread work and runs async I/O with a game-thread apply callback;
- `Configuration` owns typed, validated and reloadable operator settings under `config/plugins/<plugin-name>/`;
- `Storage` stores arbitrary plugin-owned JSON beneath `data/plugins/<plugin-name>/`;
- `Log` writes structured records under the plugin's own source name;
- `Sandboxes` discovers managed worlds and, on the control-plane process, exposes sandbox lifecycle operations;
- `Debug` is a separately gated process-internal diagnostic surface for explicitly trusted tooling plugins.

The host owns every command, event subscription, repeating task and async operation created through these facets. Unload first cancels and joins background work, then removes registrations before releasing the collectible context. Ordinary plugins do not keep registration leases or reverse-registration lists.

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

`samples/Vega.AdvancedPacketPlugin` shows the separate typed validator shape and states its concurrency/buffer rules in code. It is deliberately not referenced by the ordinary template or usability sample.

Typical TShock patterns map by behavior, not API shape:

| TShock-style intent | Vega golden path |
| --- | --- |
| plugin base plus `Initialize` | `VegaPlugin` plus `Configure` |
| add/remove chat command | `plugin.Commands.Register`; host removes it |
| player greet hook | `plugin.Players.OnJoined` plus `Message` |
| `TSPlayer` lookup/mutation | immutable snapshot lookup plus narrow player operation |
| timer and manual dispose | `plugin.Tasks.Every`; host cancels it |
| background I/O then main-thread callback | `plugin.Tasks.RunIo` |
| plugin configuration | `plugin.Configuration.Use<T>` |
| arbitrary plugin data | scoped `plugin.Storage` JSON |
| managed world lifecycle | `plugin.Sandboxes` |

There is no source or binary compatibility with TShock. The mapping preserves the behavior while keeping Terraria globals and runtime internals out of ordinary plugin code.
