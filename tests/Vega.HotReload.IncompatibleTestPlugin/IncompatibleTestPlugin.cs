using Vega.Contracts.Modules;
using Vega.PluginSdk;

[assembly: VegaPluginApiGeneration(int.MaxValue)]

namespace Vega.HotReload.IncompatibleTestPlugin;

public sealed class IncompatibleTestPlugin : VegaPlugin
{
    public IncompatibleTestPlugin() => throw new InvalidOperationException("Incompatible plugin constructor must not run.");
    public override VegaModuleDescriptor Descriptor { get; } = new("Vega.Test.IncompatiblePlugin", "1.0.0", []);
    protected override void Configure(IVegaPluginContext plugin) => throw new NotSupportedException();
}
