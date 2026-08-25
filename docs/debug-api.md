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

Ordinary plugin features such as `Configuration` and `Sandboxes` are not DEBUG-only. See [plugin-sdk.md](plugin-sdk.md).

## Runtime and object inspection

The read-only baseline exposes:

- CoreCLR/process, GC and ThreadPool snapshot;
- loaded assemblies and their `AssemblyLoadContext` state;
- operating-system process threads;
- named live roots registered by Vega;
- bounded public field/property inspection.

The plugin host registers command/player/game-thread/plugin-scope roots available at the plugin boundary. Registered plugin configuration is exposed through a dedicated typed diagnostics registry rather than arbitrary filesystem access.

Additional unsafe capabilities are independent switches:

- `allowNonPublicMembers` permits inspection and path traversal through non-public members;
- `allowMethodInvoke` permits reflection invocation on a registered root or an object reachable from it;
- `allowStateMutation` permits assigning fields and properties through JSON-bound values and modifying registered plugin configurations;
- `allowGarbageCollection` permits forced GC for unload/leak diagnostics.

These switches are intentionally independent. Enabling the DEBUG API does not implicitly enable reflection mutation or invocation.

## Bounded network trace

A trusted DEBUG client can start a temporary trace with `StartNetworkTrace`.

```csharp
plugin.Debug.StartNetworkTrace(new VegaDebugNetworkTraceOptions(
    DurationSeconds: 15,
    MaxEvents: 5000,
    Slot: 7,
    PacketIds: [17, 20]));
```

The trace can collect:

- inbound packet id, size, player slot and timestamp;
- outbound byte counts;
- execution time for every processor in the Vega packet policy pipeline;
- processor call count, total/average/max duration and non-allow decisions.

Tracing subscribes to `DiagnosticSource` only while the bounded session is active. It automatically stops after the configured duration, which is clamped to five minutes, and packet storage is bounded. Events beyond the configured capacity are counted as dropped rather than growing memory without limit.

When no trace is active, packet diagnostics only perform an `IsEnabled` check and do not allocate diagnostic payloads or start timers.

Outbound instrumentation currently records byte counts but does not infer packet ids when the native send hook does not expose them. Processor timing is aggregated by processor and packet-view type; it is not a full CLR sampling profiler or a per-packet flame graph.

## Managed plugin configuration diagnostics

Every ordinary plugin can create its own typed configuration with `plugin.Configuration.Use<T>`. DEBUG tooling can inspect registered configurations across plugins:

```csharp
IReadOnlyList<VegaDebugPluginConfigurationSnapshot> configs =
    plugin.Debug.SnapshotPluginConfigurations();
```

A DEBUG client can read, JSON merge-patch, replace or reload a registered configuration. Mutating operations require `allowStateMutation=true`.

Configuration changes do not bypass plugin policy: Vega deserializes the candidate JSON into the owning plugin's actual `TConfig`, executes that plugin's validator and only then performs the same atomic save used by the plugin itself. Invalid candidates are rejected without replacing the file.

The diagnostics registration is owned by the plugin scope and is removed during unload, so the registry does not intentionally retain a collectible plugin load context.

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
