using Vega.Contracts.Modules;

namespace Vega.PluginSdk;

/// <summary>
/// Minimal entry point for an ordinary hot-reloadable Vega plugin.
/// Registration lifetime and unload cleanup are owned by the host.
/// </summary>
public abstract class VegaPlugin : IHotReloadableVegaModule
{
    public abstract VegaModuleDescriptor Descriptor { get; }

    protected abstract void Configure(IVegaPluginContext plugin);

    protected virtual ValueTask OnStartedAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnStoppingAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask DisposePluginAsync() => ValueTask.CompletedTask;

    async ValueTask IHotReloadableVegaModule.StartAsync(
        IHotModuleContext context,
        CancellationToken cancellationToken)
    {
        if (context is not IVegaPluginContext plugin)
        {
            throw new InvalidOperationException(
                "This Vega host does not provide the public plugin SDK context.");
        }

        Configure(plugin);
        await OnStartedAsync(cancellationToken).ConfigureAwait(false);
    }

    ValueTask IHotReloadableVegaModule.StopAsync(CancellationToken cancellationToken) =>
        OnStoppingAsync(cancellationToken);

    ValueTask IAsyncDisposable.DisposeAsync() => DisposePluginAsync();
}
