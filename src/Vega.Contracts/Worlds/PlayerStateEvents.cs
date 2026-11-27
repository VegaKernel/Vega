using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

/// <summary>Detached server-observed state at the end of a native tick, for one exact connected player session.</summary>
/// <remarks>Terraria may synchronize some of these values from the client. This snapshot is not proof of a hit, attacker's identity, or combat authorization.</remarks>
public readonly record struct PlayerStateEventSnapshot(WorldId WorldId, PlayerId PlayerId, PlayerSessionId SessionId,
    DateTimeOffset ObservedAt, int Life, int MaxLife, int Mana, int MaxMana, bool IsDead, bool IsHostile, int Team,
    int DeathsPve, int DeathsPvp);

/// <summary>Net vital changes between consecutive native ticks, not individual damage/heal events.</summary>
public readonly record struct PlayerVitalsChangedEvent(PlayerStateEventSnapshot Before, PlayerStateEventSnapshot After)
{
    public int LifeDelta => After.Life - Before.Life;
    public int ManaDelta => After.Mana - Before.Mana;
}

/// <summary>Classification inferred only from same-tick observed counter increments.</summary>
public enum PlayerObservedDeathKind { Unknown, Pve, Pvp }

/// <summary>Observed alive-to-dead transition; no attack attribution is provided.</summary>
public readonly record struct PlayerDeathObservedEvent(PlayerStateEventSnapshot Before, PlayerStateEventSnapshot After, PlayerObservedDeathKind Kind);
/// <summary>Observed dead-to-alive transition, rather than a native respawn request.</summary>
public readonly record struct PlayerRespawnObservedEvent(PlayerStateEventSnapshot Before, PlayerStateEventSnapshot After);
/// <summary>Observed native counter changes; may occur independently of death transitions and include counter resets.</summary>
public readonly record struct PlayerDeathCountersChangedEvent(PlayerStateEventSnapshot Before, PlayerStateEventSnapshot After);
/// <summary>Observed PvP enabled/disabled or team changes, not a PvP damage notification.</summary>
public readonly record struct PlayerPvpChangedEvent(PlayerStateEventSnapshot Before, PlayerStateEventSnapshot After);

public enum PlayerPvpChangeKind { Hostility, Team }

/// <summary>Validated current-session PvP/team request before native handling (not a committed stance change).
/// Position and current stance are captured from the exact live native player on the game thread.</summary>
public readonly record struct PlayerPvpChangeRequestedEvent(WorldId WorldId, PlayerId PlayerId, PlayerSessionId SessionId,
    PlayerPosition Position, PlayerPvpChangeKind Kind, bool IsHostile, int Team,
    bool? RequestedHostile = null, int? RequestedTeam = null);

/// <summary>Detached sorted sets of native buff types. Timer refreshes, countdowns and slot reordering are not membership changes.</summary>
public readonly record struct PlayerBuffsChangedEvent(PlayerStateEventSnapshot Player, IReadOnlyList<int> Before, IReadOnlyList<int> After);

/// <summary>Native execution provenance is distinct from any source declared in a death reason.</summary>
public enum PlayerHurtSourceKind { Unverified, NativeMeleePlayer, NativeProjectile }

/// <summary>Positive completed native Hurt invocation for one exact world/session. NativeDamage is the method's
/// return, not necessarily the victim's actual life reduction. Pvp and requested damage are native arguments;
/// they do not establish a verified attacker. A melee player is attached only inside an exact native melee call.
/// A projectile owner is native metadata, not authenticated player attribution.</summary>
public readonly record struct PlayerHurtCompletedEvent(WorldId WorldId, PlayerId Victim, PlayerSessionId VictimSession,
    bool Pvp, bool Critical, int RequestedDamage, double NativeDamage, int LifeBefore, int LifeAfter,
    PlayerHurtSourceKind SourceKind, PlayerId? NativeAttacker = null, PlayerSessionId? NativeAttackerSession = null,
    ProjectileEventSnapshot? NativeProjectile = null, int? DeclaredPlayerSlot = null);
