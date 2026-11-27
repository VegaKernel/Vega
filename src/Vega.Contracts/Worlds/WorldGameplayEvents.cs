using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

/// <summary>Native generation identity, not an optimistic mutation revision.</summary>
public readonly record struct NpcEventSnapshot(WorldId WorldId, int Slot, int Generation, int Type,
    PlayerPosition Position, int Life, bool Friendly, bool Boss);

/// <summary>Observed allocation request, before the native NPC is created; not a successful-spawn notification.</summary>
public readonly record struct NpcCreatingEvent(WorldId WorldId, int Slot, int Generation, int Type, PlayerPosition RequestedPosition);
public readonly record struct NpcKilledEvent(NpcEventSnapshot Npc);
/// <summary>Native completed strike with the NPC snapshot after the native finally block.
/// NativeDamage is StrikeNPC's returned damage, not necessarily the exact life decrement (e.g. shared-life NPCs).</summary>
public readonly record struct NpcStrikeCompletedEvent(NpcEventSnapshot Npc, int RequestedDamage, int NativeDamage, bool Critical);
/// <summary>Observed native caller category; not authoritative attack attribution.</summary>
public enum NpcStrikeSourceKind { Unknown, Player, Projectile, Other }

/// <summary>Detached native caller context. PlayerId and SessionId are populated together only for an exact
/// active local player session. Projectile owner is observed native metadata, never resolved to a session.
/// A caller context does not establish who caused, authorized or owns the damage.</summary>
public readonly record struct NpcStrikeSourceSnapshot(NpcStrikeSourceKind Kind, PlayerId? PlayerId = null,
    PlayerSessionId? SessionId = null, ProjectileEventSnapshot? Projectile = null);

public readonly record struct NpcStrikeCompletedWithSourceEvent(NpcStrikeCompletedEvent Strike, NpcStrikeSourceSnapshot Source);
public readonly record struct NpcTransformingEvent(NpcEventSnapshot Npc, int RequestedType);
public readonly record struct NpcLootCreatingEvent(NpcEventSnapshot Npc, int ItemType, int Stack, int Prefix,
    PlayerPosition RequestedPosition);
public readonly record struct NpcLootDroppedEvent(NpcEventSnapshot Npc, int ItemSlot, int ItemType, int Stack, byte Prefix);

/// <summary>Snapshot of a projectile at the native ingress synchronization/removal boundary.</summary>
public readonly record struct ProjectileEventSnapshot(WorldId WorldId, int Slot, int Key, int Type, int Owner,
    PlayerPosition Position);
public readonly record struct ProjectileSynchronizedEvent(PlayerId Sender, ProjectileEventSnapshot Projectile);
public readonly record struct ProjectileRemovedEvent(PlayerId Sender, ProjectileEventSnapshot Projectile);

/// <summary>Elapsed native dedicated update work observed at its completion hook, before server pacing sleep.</summary>
public readonly record struct WorldTickCompletedEvent(WorldId WorldId, TimeSpan Duration);

/// <summary>Observed native save result; not a before-save policy or a guarantee of backup completion.
/// FailureType contains only the exception type name when available, never a mutable exception or stack trace.</summary>
public readonly record struct WorldSaveCompletedEvent(WorldId WorldId, bool Succeeded, string? FailureType);

/// <summary>Host-owned event transport; plugins receive only the curated registration facade.</summary>
public interface IWorldGameplayEvents
{
    IDisposable Subscribe<T>(Action<T> handler) where T : struct;
    bool HasSubscribers<T>() where T : struct;
    void Publish<T>(T value) where T : struct;
    IDisposable SubscribePolicy<T>(Func<T, bool> allow) where T : struct;
    bool HasPolicies<T>() where T : struct;
    bool Allows<T>(T value) where T : struct;
}
