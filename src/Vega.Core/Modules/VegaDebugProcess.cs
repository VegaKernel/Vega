using Vega.Contracts.Operations;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

/// <summary>Process singleton used by hot-loaded plugin scopes to obtain a name-scoped DEBUG context.</summary>
public static class VegaDebugProcess
{
    private static readonly object Sync = new();
    private static VegaDebugRegistry? registry;
    private static bool configured;

    public static void EnsureConfigured(IOperationsLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (Volatile.Read(ref configured))
        {
            return;
        }

        lock (Sync)
        {
            if (configured)
            {
                return;
            }

            VegaDebugOptions options = VegaDebugOptionsStore.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "debug-api.json"),
                logger);
            registry = new VegaDebugRegistry(options);
            configured = true;
            logger.Log(
                OperationsLogLevel.Information,
                "debug-api",
                $"DEBUG API is {(options.Enabled ? "enabled" : "disabled")}; " +
                $"trusted plugins: {(options.AllowedPlugins.Length == 0 ? "none" : string.Join(", ", options.AllowedPlugins))}.");
        }
    }

    public static void RegisterRoot(string name, object root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(root);
        Volatile.Read(ref registry)?.Register(name, root);
    }

    public static void Configure(VegaDebugRegistry? value)
    {
        lock (Sync)
        {
            registry = value;
            configured = value is not null;
        }
    }

    public static IVegaDebugContext CreateContext(string pluginName) =>
        Volatile.Read(ref registry)?.CreateContext(pluginName) ?? VegaDebug.Disabled;
}
