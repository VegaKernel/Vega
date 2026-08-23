using System.Diagnostics;
using Vega.Contracts.Operations;
using Vega.Contracts.Modules;

namespace Vega.Core.Modules;

public sealed class VegaModuleHost : IVegaModuleHost
{
	private readonly IVegaModule[] modules;
	private readonly ModuleOperationsSnapshot[] snapshots;
	private readonly bool[] moduleDisposed;
	private int startedCount;
	private bool disposed;

	public VegaModuleHost(IEnumerable<IVegaModule> modules)
	{
		ArgumentNullException.ThrowIfNull(modules);
		this.modules = Order(modules.ToArray());
		moduleDisposed = new bool[this.modules.Length];
		snapshots = this.modules
			.Select(static module => Snapshot(module, ModuleLoadState.Registered, TimeSpan.Zero))
			.ToArray();
	}

	public async Task StartAsync(TimeSpan perModuleTimeout, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (perModuleTimeout <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(perModuleTimeout));
		}

		if (startedCount != 0)
		{
			return;
		}

		for (var index = 0; index < modules.Length; index++)
		{
			long startedAt = Stopwatch.GetTimestamp();
			try
			{
				await Task.Run(modules[index].Start, CancellationToken.None)
					.WaitAsync(perModuleTimeout, cancellationToken)
					.ConfigureAwait(false);
				startedCount++;
				snapshots[index] = Snapshot(modules[index], ModuleLoadState.Loaded,
					Stopwatch.GetElapsedTime(startedAt));
			}
			catch (Exception ex)
			{
				try
				{
					DisposeModule(index);
				}
				catch (Exception disposeException)
				{
					ex = new AggregateException(ex, disposeException);
				}

				snapshots[index] = Snapshot(modules[index], ModuleLoadState.Failed,
					Stopwatch.GetElapsedTime(startedAt), ex.Message);
				if (DisposeAllModules() is { } cleanupException)
				{
					ex = new AggregateException(ex, cleanupException);
				}
				disposed = true;
				throw new InvalidOperationException(
					$"Vega module '{modules[index].Descriptor.Name}' failed to start.", ex);
			}
		}
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
					DisposeModule(index);
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
				if (DisposeAllModules() is { } cleanupException)
				{
					ex = new AggregateException(ex, cleanupException);
				}
				disposed = true;
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

	public IReadOnlyDictionary<string, ModuleRestartState> CaptureRestartState()
	{
		var states = new Dictionary<string, ModuleRestartState>(StringComparer.Ordinal);
		foreach (IModuleRestartStateParticipant participant in modules.OfType<IModuleRestartStateParticipant>())
		{
			ModuleRestartState? state = participant.CaptureRestartState();
			if (state is null)
			{
				continue;
			}

			if (string.IsNullOrWhiteSpace(state.Key) || state.Version < 1 || state.Payload.Length > 1024 * 1024 ||
				!string.Equals(state.Key, participant.RestartStateKey, StringComparison.Ordinal) ||
				state.Version != participant.RestartStateVersion ||
				!states.TryAdd(state.Key, state))
			{
				throw new InvalidOperationException("A module supplied invalid, duplicate or oversized restart state.");
			}
		}

		return states;
	}

	public void RestoreRestartState(IReadOnlyDictionary<string, ModuleRestartState> states)
	{
		foreach (IModuleRestartStateParticipant participant in modules.OfType<IModuleRestartStateParticipant>())
		{
			if (states.TryGetValue(participant.RestartStateKey, out ModuleRestartState? state) &&
				participant.RestartStateVersion == state.Version)
			{
				participant.RestoreRestartState(state);
			}
		}
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		if (DisposeAllModules() is { } exception)
		{
			throw exception;
		}
	}

	private Exception? DisposeAllModules()
	{
		List<Exception>? exceptions = null;
		for (int index = modules.Length - 1; index >= 0; index--)
		{
			try
			{
				DisposeModule(index);
			}
			catch (Exception exception)
			{
				(exceptions ??= []).Add(exception);
			}
		}

		startedCount = 0;
		return exceptions switch
		{
			null => null,
			[var exception] => exception,
			_ => new AggregateException(exceptions)
		};
	}

	private void DisposeModule(int index)
	{
		if (moduleDisposed[index]) return;
		moduleDisposed[index] = true;
		modules[index].Dispose();
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

	private static IVegaModule[] Order(IVegaModule[] candidates)
	{
		var byName = new Dictionary<string, IVegaModule>(StringComparer.OrdinalIgnoreCase);
		foreach (IVegaModule module in candidates)
		{
			if (!byName.TryAdd(module.Descriptor.Name, module))
			{
				throw new InvalidOperationException($"Duplicate Vega module '{module.Descriptor.Name}'.");
			}
		}

		var remaining = new List<IVegaModule>(candidates);
		var ordered = new List<IVegaModule>(candidates.Length);
		while (remaining.Count > 0)
		{
			int index = remaining.FindIndex(module => module.Descriptor.Dependencies
				.Where(byName.ContainsKey)
				.All(dependency => ordered.Any(started =>
					string.Equals(started.Descriptor.Name, dependency, StringComparison.OrdinalIgnoreCase))));
			if (index < 0)
			{
				throw new InvalidOperationException(
					$"Vega module dependency cycle: {string.Join(", ", remaining.Select(static module => module.Descriptor.Name))}.");
			}

			ordered.Add(remaining[index]);
			remaining.RemoveAt(index);
		}

		return ordered.ToArray();
	}
}
