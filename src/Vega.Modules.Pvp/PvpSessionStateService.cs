using System.Diagnostics.CodeAnalysis;
using TShockNG.Contracts.Players;
using Vega.Contracts.Extensions;

namespace Vega.Modules.Pvp;

public sealed record ProjectileWeaponAttribution(
    int ProjectileIdentity,
    int WeaponItemType,
    DateTimeOffset RecordedAt);

public sealed record PvpDeathWindow(DateTimeOffset DiedAt, TimeSpan Duration)
{
	public bool IsActive(DateTimeOffset now) => now >= DiedAt && now - DiedAt <= Duration;
}

/// <summary>
/// Owns temporary PvP-only state for one concrete player session and removes it on disconnect.
/// </summary>
public sealed class PvpSessionStateService : IDisposable
{
    private static readonly ExtensionStateKey<ProjectileAttributionState> ProjectileAttributionKey =
        new("pvp.projectile-weapon-attribution");
    private static readonly ExtensionStateKey<PvpDeathWindow> DeathWindowKey = new("pvp.death-window");
    private static readonly ExtensionStateKey<bool> SpectatingKey = new("pvp.spectating");

    private readonly object sync = new();
    private readonly IExtensionStateStore state;
    private readonly IPlayerSessionLifecycle lifecycle;
    private readonly Dictionary<PlayerId, PlayerSessionId> sessions = [];
    private bool disposed;

    public PvpSessionStateService(IExtensionStateStore state, IPlayerSessionLifecycle lifecycle)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        lifecycle.Opened += OnOpened;
        lifecycle.Closed += OnClosed;
    }

    public void RecordProjectileWeapon(
        PlayerId playerId,
        int projectileIdentity,
        int weaponItemType,
        DateTimeOffset timestamp) =>
        ScopeFor(playerId)
            .GetOrCreate(ProjectileAttributionKey, static () => new ProjectileAttributionState())
            .Set(new ProjectileWeaponAttribution(projectileIdentity, weaponItemType, timestamp));

    public bool TryGetProjectileWeapon(
        PlayerId playerId,
        int projectileIdentity,
        out ProjectileWeaponAttribution? attribution)
    {
        attribution = null;
        return TryScopeFor(playerId, out IExtensionStateScope? scope) &&
            scope.TryGet(ProjectileAttributionKey, out ProjectileAttributionState? projectiles) &&
            projectiles is not null &&
            projectiles.TryGet(projectileIdentity, out attribution);
    }

    public bool RemoveProjectileWeapon(PlayerId playerId, int projectileIdentity) =>
        TryScopeFor(playerId, out IExtensionStateScope? scope) &&
        scope.TryGet(ProjectileAttributionKey, out ProjectileAttributionState? projectiles) &&
        projectiles is not null &&
        projectiles.Remove(projectileIdentity);

	public void RecordDeath(PlayerId playerId, DateTimeOffset timestamp, TimeSpan duration) =>
		ScopeFor(playerId).Set(
			DeathWindowKey,
			duration > TimeSpan.Zero
				? new PvpDeathWindow(timestamp, duration)
				: throw new ArgumentOutOfRangeException(nameof(duration)));

    public bool IsInsideDeathWindow(PlayerId playerId, DateTimeOffset now) =>
        TryScopeFor(playerId, out IExtensionStateScope? scope) &&
        scope.TryGet(DeathWindowKey, out PvpDeathWindow? window) &&
        window is not null &&
        window.IsActive(now);

    public void SetSpectating(PlayerId playerId, bool spectating) =>
        ScopeFor(playerId).Set(SpectatingKey, spectating);

    public bool IsSpectating(PlayerId playerId) =>
        TryScopeFor(playerId, out IExtensionStateScope? scope) &&
        scope.TryGet(SpectatingKey, out bool spectating) &&
        spectating;

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifecycle.Opened -= OnOpened;
            lifecycle.Closed -= OnClosed;
            foreach (PlayerSessionId sessionId in sessions.Values)
            {
                state.RemoveScope(ScopeId(sessionId));
            }

            sessions.Clear();
        }
    }

    private void OnOpened(PlayerSessionOpened opened)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.Remove(opened.PlayerId, out PlayerSessionId previousSession))
            {
                state.RemoveScope(ScopeId(previousSession));
            }

            sessions[opened.PlayerId] = opened.SessionId;
            IExtensionStateScope scope = state.CreateScope(ScopeId(opened.SessionId));
            scope.Set(SpectatingKey, false);
        }
    }

    private void OnClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
			if (disposed ||
				!sessions.TryGetValue(closed.PlayerId, out PlayerSessionId current) ||
				current != closed.SessionId)
			{
				return;
			}

			sessions.Remove(closed.PlayerId);
			state.RemoveScope(ScopeId(closed.SessionId));
        }
    }

    private IExtensionStateScope ScopeFor(PlayerId playerId) =>
        TryScopeFor(playerId, out IExtensionStateScope? scope)
            ? scope
            : throw new InvalidOperationException($"Player slot {playerId.Slot} has no active PvP session scope.");

    private bool TryScopeFor(
        PlayerId playerId,
        [NotNullWhen(true)] out IExtensionStateScope? scope)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.TryGetValue(playerId, out PlayerSessionId sessionId) &&
                state.TryGetScope(ScopeId(sessionId), out scope) &&
                scope is not null)
            {
                return true;
            }

            scope = null;
            return false;
        }
    }

    private static ExtensionStateScopeId ScopeId(PlayerSessionId sessionId) =>
        new("player-session", sessionId.ToString());

    private sealed class ProjectileAttributionState
    {
        private readonly object sync = new();
        private readonly Dictionary<int, ProjectileWeaponAttribution> projectiles = [];

        public void Set(ProjectileWeaponAttribution attribution)
        {
            lock (sync)
            {
                projectiles[attribution.ProjectileIdentity] = attribution;
            }
        }

        public bool TryGet(int projectileIdentity, out ProjectileWeaponAttribution? attribution)
        {
            lock (sync)
            {
                return projectiles.TryGetValue(projectileIdentity, out attribution);
            }
        }

        public bool Remove(int projectileIdentity)
        {
            lock (sync)
            {
                return projectiles.Remove(projectileIdentity);
            }
        }
    }
}
