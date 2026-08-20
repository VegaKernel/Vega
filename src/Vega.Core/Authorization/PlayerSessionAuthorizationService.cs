using Vega.Contracts.Authorization;
using Vega.Contracts.Players;

namespace Vega.Core.Authorization;

public sealed class PlayerSessionAuthorizationService :
	IPlayerAuthorizationService,
	IDisposable
{
	private readonly object sync = new();
	private readonly IPlayerSessionLifecycle lifecycle;
	private readonly Dictionary<PlayerId, HashSet<string>> permissions = [];
	private bool disposed;

	public PlayerSessionAuthorizationService(IPlayerSessionLifecycle lifecycle)
	{
		this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
		lifecycle.Closed += OnClosed;
	}

	public bool HasPermission(PlayerId playerId, string permission)
	{
		if (string.IsNullOrWhiteSpace(permission))
			return false;

		lock (sync)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			return permissions.TryGetValue(playerId, out HashSet<string>? granted) &&
				(granted.Contains(permission) || granted.Contains("*"));
		}
	}

	public void Replace(PlayerId playerId, IEnumerable<string> grantedPermissions)
	{
		ArgumentNullException.ThrowIfNull(grantedPermissions);
		var replacement = new HashSet<string>(
			grantedPermissions.Where(static permission => !string.IsNullOrWhiteSpace(permission)),
			StringComparer.OrdinalIgnoreCase);

		lock (sync)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			if (replacement.Count == 0)
				permissions.Remove(playerId);
			else
				permissions[playerId] = replacement;
		}
	}

	public void Dispose()
	{
		lock (sync)
		{
			if (disposed) return;
			disposed = true;
			lifecycle.Closed -= OnClosed;
			permissions.Clear();
		}
	}

	private void OnClosed(PlayerSessionClosed closed)
	{
		lock (sync)
		{
			if (!disposed) permissions.Remove(closed.PlayerId);
		}
	}
}
