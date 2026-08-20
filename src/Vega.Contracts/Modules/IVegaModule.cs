using Vega.Contracts.Operations;
using Vega.Contracts.Commands;

namespace Vega.Contracts.Modules;

public sealed record VegaModuleDescriptor(
	string Name,
	string Version,
	IReadOnlyList<string> Dependencies);

public interface IVegaModule : IDisposable
{
	VegaModuleDescriptor Descriptor { get; }

	void Start();
}

public interface IVegaModuleHost : IModuleOperationsCatalog, IDisposable
{
	void Start();
}

/// <summary>
/// A dynamically loaded module that supports deterministic administrative unload.
/// </summary>
public interface IHotReloadableVegaModule : IAsyncDisposable
{
    VegaModuleDescriptor Descriptor { get; }

    ValueTask StartAsync(IHotModuleContext context, CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Tracks every host-facing registration owned by one hot-reloadable module.
/// </summary>
public interface IHotModuleContext
{
    IDisposable RegisterCommand(ICommandHandler handler);

    T Track<T>(T registration) where T : IDisposable;

    IDisposable RegisterCleanup(Action cleanup);
}

public sealed record ServerPluginOperationResult(
    bool Succeeded,
    bool RestartRequired,
    string Message);

public interface IServerPluginController : IModuleOperationsCatalog
{
    bool HotReloadEnabled { get; }

    ValueTask<ServerPluginOperationResult> ReloadAsync(
        string plugin,
        CancellationToken cancellationToken = default);

    ValueTask<ServerPluginOperationResult> UnloadAsync(
        string plugin,
        CancellationToken cancellationToken = default);
}
