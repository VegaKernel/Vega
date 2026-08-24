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

The fixed context exposes five discoverable facets:

- `Commands` registers typed command handlers;
- `Players` provides immutable online snapshots, lookup, messaging, kick/teleport operations and join/leave hooks;
- `Tasks` posts to the game thread, schedules repeating game-thread work and runs async I/O with a game-thread apply callback;
- `Storage` reads and atomically writes plugin-scoped JSON beneath `data/plugins/<plugin-name>/`;
- `Log` writes structured records under the plugin's own source name.

The host owns every command, event subscription, repeating task and async operation created through these facets. Unload first cancels and joins background work, then removes registrations before releasing the collectible context. Ordinary plugins do not keep registration leases or reverse-registration lists.

## Async I/O

Use `Tasks.RunIo` only for genuine database, HTTP or file I/O. The operation runs without Terraria access; the `apply` callback is posted to the authoritative game thread:

```csharp
plugin.Tasks.RunIo(
    "load-motd",
    cancellationToken => File.ReadAllTextAsync(path, cancellationToken),
    motd => plugin.Log.Write(OperationsLogLevel.Information, motd));
```

Do not capture mutable Terraria state in the I/O callback. Cancellation, exception logging and unload joining belong to the host.

## Advanced boundary and migration

Low-level `IPacketProcessor<T>` and packet-security contracts remain separate from `IVegaPluginContext`. They require explicit pure/concurrent/buffer-lifetime reasoning and are not registered by the ordinary facade. Dynamic lifecycle specialists may implement `IHotReloadableVegaModule` directly, but then own the extra ceremony intentionally.

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
| plugin config/data file | scoped `plugin.Storage` JSON |

There is no source or binary compatibility with TShock. The mapping preserves the behavior while keeping Terraria globals and runtime internals out of ordinary plugin code.
