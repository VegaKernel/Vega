using Vega.Contracts.Authorization;
using Vega.Contracts.Players;

namespace Vega.Core.Authorization;

public sealed class PlayerSessionAuthorizationService :
	IPlayerAuthorizationService,
	IDisposable
{
	private readonly object sync = new();
	private readonly IPlayerSessionLifecycle lifecycle;
	private readonly Dictionary<PlayerId, SessionPermissions> permissions = [];
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
			if (!permissions.TryGetValue(playerId, out SessionPermissions? session) ||
				session.Denied.Contains(permission) || session.Denied.Contains("*"))
			{
				return false;
			}

			if (session.Granted.Contains(permission) || session.Granted.Contains("*"))
			{
				return true;
			}

			return session.Authorization is not null && session.GroupName is not null &&
				(IsGroupMarker(permission, session.GroupName, session.Authorization) ||
				 session.Authorization.HasPermission(session.GroupName, permission));
		}
	}

	public void Replace(PlayerId playerId, IEnumerable<string> grantedPermissions)
		=> Replace(playerId, grantedPermissions, []);

	public void Replace(
		PlayerId playerId,
		IEnumerable<string> grantedPermissions,
		IEnumerable<string> deniedPermissions)
	{
		ArgumentNullException.ThrowIfNull(grantedPermissions);
		ArgumentNullException.ThrowIfNull(deniedPermissions);
		var granted = new HashSet<string>(
			grantedPermissions.Where(static permission => !string.IsNullOrWhiteSpace(permission)),
			StringComparer.OrdinalIgnoreCase);
		var denied = new HashSet<string>(
			deniedPermissions.Where(static permission => !string.IsNullOrWhiteSpace(permission)),
			StringComparer.OrdinalIgnoreCase);

		lock (sync)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			if (granted.Count == 0 && denied.Count == 0)
				permissions.Remove(playerId);
			else
				permissions[playerId] = new SessionPermissions(granted, denied, null, null);
		}
	}

	public void ReplaceAccountGroup(
		PlayerId playerId,
		string groupName,
		IAuthorizationService authorization)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
		ArgumentNullException.ThrowIfNull(authorization);
		lock (sync)
		{
			ObjectDisposedException.ThrowIf(disposed, this);
			permissions[playerId] = new SessionPermissions(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				groupName,
				authorization);
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

	private static bool IsGroupMarker(
		string permission,
		string groupName,
		IAuthorizationService authorization)
	{
		const string prefix = "vega.group.";
		if (!permission.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		string sought = permission[prefix.Length..];
		IReadOnlyDictionary<string, GroupSnapshot> groups = authorization.SnapshotGroups()
			.ToDictionary(static group => group.Name, StringComparer.OrdinalIgnoreCase);
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string? current = groupName;
		while (current is not null && visited.Add(current) && groups.TryGetValue(current, out GroupSnapshot? group))
		{
			if (current.Equals(sought, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			current = group.ParentName;
		}

		return false;
	}

	private sealed record SessionPermissions(
		HashSet<string> Granted,
		HashSet<string> Denied,
		string? GroupName,
		IAuthorizationService? Authorization);
}
