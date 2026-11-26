using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

/// <summary>Native generation identity, not an optimistic mutation revision.</summary>
public readonly record struct NpcEventSnapshot(WorldId WorldId, int Slot, int Generation, int Type,
    PlayerPosition Position, int Life, bool Friendly, bool Boss);

/// <summary>Observed allocation request, before the native NPC is created; not a successful-spawn notification.</summary>
public readonly record struct NpcCreatingEvent(WorldId WorldId, int Slot, int Generation, int Type, PlayerPosition RequestedPosition);
public readonly record struct NpcKilledEvent(NpcEventSnapshot Npc);
public readonly record struct NpcTransformingEvent(NpcEventSnapshot Npc, int RequestedType);
public readonly record struct NpcLootCreatingEvent(NpcEventSnapshot Npc, int ItemType, int Stack, int Prefix,
    PlayerPosition RequestedPosition);
public readonly record struct NpcLootDroppedEvent(NpcEventSnapshot Npc, int ItemSlot, int ItemType, int Stack, byte Prefix);

/// <summary>Snapshot of a projectile at the native ingress synchronization/removal boundary.</summary>
public readonly record struct ProjectileEventSnapshot(WorldId WorldId, int Slot, int Key, int Type, int Owner,
    PlayerPosition Position);
public readonly record struct ProjectileSynchronizedEvent(PlayerId Sender, ProjectileEventSnapshot Projectile);
public readonly record struct ProjectileRemovedEvent(PlayerId Sender, ProjectileEventSnapshot Projectile);

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
