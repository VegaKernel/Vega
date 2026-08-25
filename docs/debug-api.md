# DEBUG API

Vega exposes an opt-in process-internal DEBUG API to explicitly trusted hot-reloadable plugins. It is intended for diagnostic tooling such as `VegaToolsMCP`, not as a general plugin service locator.

The API is disabled by default. Configuration lives in `config/debug-api.json`:

```json
{
  "enabled": false,
  "allowedPlugins": [
    "VegaToolsMCP"
  ],
  "allowNonPublicMembers": false,
  "allowMethodInvoke": false,
  "allowStateMutation": false,
  "allowGarbageCollection": false,
  "maxMembers": 256,
  "maxStringLength": 4096
}
```

A plugin receives the API through `IVegaPluginContext.Debug`. When the API is disabled or the plugin name is not present in `allowedPlugins`, `Capabilities` is `None` and DEBUG operations reject access.

## Capabilities

The read-only baseline exposes:

- CoreCLR/process, GC and ThreadPool snapshot;
- loaded assemblies and their `AssemblyLoadContext` state;
- operating-system process threads;
- named live roots registered by Vega;
- bounded public field/property inspection.

The plugin host currently registers the command registry, player services, player lifecycle, game-thread dispatcher and the plugin scope itself when those services are available.

Additional capabilities are independent switches:

- `allowNonPublicMembers` permits inspection and path traversal through non-public members;
- `allowMethodInvoke` permits reflection invocation on a registered root or an object reachable from it;
- `allowStateMutation` permits assigning fields and properties through JSON-bound values;
- `allowGarbageCollection` permits forced GC for unload/leak diagnostics.

These switches are intentionally independent. Enabling the DEBUG API does not implicitly enable mutation or invocation.

## Plugin example

```csharp
protected override void Configure(IVegaPluginContext plugin)
{
    if ((plugin.Debug.Capabilities & VegaDebugCapabilities.Runtime) != 0)
    {
        VegaDebugRuntimeSnapshot runtime = plugin.Debug.CaptureRuntime();
        plugin.Log.Write(
            OperationsLogLevel.Information,
            $"DEBUG API connected to PID {runtime.ProcessId}.");
    }
}
```

`Inspect` traverses named roots using dot-separated member paths. `InvokeAsync` and `SetValue` accept JSON-compatible arguments so remote diagnostic adapters do not need to retain live object references.

## Security boundary

Treat `allowedPlugins` as a privileged allowlist. A plugin granted non-public invocation or mutation effectively has debugger-like authority over the registered live object graph. Do not grant these capabilities to ordinary gameplay plugins, and do not expose an MCP or HTTP adapter without its own authentication and authorization boundary.

The API deliberately does not expose an arbitrary host shell, raw process-memory writes, arbitrary IL injection, or a general `IServiceProvider` escape hatch.
