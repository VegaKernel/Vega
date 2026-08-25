using Vega.PluginSdk;

namespace Vega.Core.Modules;

/// <summary>Process singleton used by hot-loaded plugin scopes to obtain a name-scoped DEBUG context.</summary>
public static class VegaDebugProcess
{
    private static VegaDebugRegistry? registry;

    public static void Configure(VegaDebugRegistry? value) => Volatile.Write(ref registry, value);

    public static IVegaDebugContext CreateContext(string pluginName) =>
        Volatile.Read(ref registry)?.CreateContext(pluginName) ?? VegaDebug.Disabled;
}
