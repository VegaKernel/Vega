using System.Diagnostics;
using Vega.Contracts.Operations;
using Vega.Contracts.Modules;

namespace Vega.Core.Modules;

public sealed class VegaModuleHost : IVegaModuleHost
{
	private readonly IVegaModule[] modules;
	private readonly ModuleOperationsSnapshot[] snapshots;
	private int startedCount;
	private bool disposed;

	public VegaModuleHost(IEnumerable<IVegaModule> modules)
	{
		ArgumentNullException.ThrowIfNull(modules);
		this.modules = modules.ToArray();
		snapshots = this.modules
			.Select(static module => Snapshot(module, ModuleLoadState.Registered, TimeSpan.Zero))
			.ToArray();
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (startedCount != 0)
		{
			return;
		}

		for (var index = 0; index < modules.Length; index++)
		{
			long startedAt = Stopwatch.GetTimestamp();
			try
			{
				modules[index].Start();
				startedCount++;
				snapshots[index] = Snapshot(
					modules[index],
					ModuleLoadState.Loaded,
					Stopwatch.GetElapsedTime(startedAt));
			}
			catch (Exception ex)
			{
				try
				{
					modules[index].Dispose();
				}
				catch (Exception disposeException)
				{
					ex = new AggregateException(ex, disposeException);
				}

				snapshots[index] = Snapshot(
					modules[index],
					ModuleLoadState.Failed,
					Stopwatch.GetElapsedTime(startedAt),
					ex.Message);
				DisposeStartedModules();
				throw new InvalidOperationException(
					$"Vega module '{modules[index].Descriptor.Name}' failed to start.",
					ex);
			}
		}
	}

	public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() =>
		Array.AsReadOnly(snapshots.Select(static snapshot => snapshot with
		{
			Dependencies = Array.AsReadOnly(snapshot.Dependencies.ToArray())
		}).ToArray());

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		DisposeStartedModules();
	}

	private void DisposeStartedModules()
	{
		while (startedCount > 0)
		{
			modules[--startedCount].Dispose();
		}
	}

	private static ModuleOperationsSnapshot Snapshot(
		IVegaModule module,
		ModuleLoadState state,
		TimeSpan startupDuration,
		string? failure = null) =>
		new(
			module.Descriptor.Name,
			module.Descriptor.Version,
			state,
			module.Descriptor.Dependencies,
			startupDuration,
			failure);
}
