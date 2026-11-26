using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Read-only, world-local gameplay notifications owned by the plugin lifetime.</summary>
public interface IPluginWorldEvents
{
    void OnNpcCreating(Action<NpcCreatingEvent> handler);
    void OnNpcKilled(Action<NpcKilledEvent> handler);
    void OnNpcTransforming(Action<NpcTransformingEvent> handler);
    void OnNpcLootDropped(Action<NpcLootDroppedEvent> handler);
    void OnProjectileSynchronized(Action<ProjectileSynchronizedEvent> handler);
    void OnProjectileRemoved(Action<ProjectileRemovedEvent> handler);
    void OnNpcActorInteracted(Action<NpcActorInteractedEvent> handler);
    void OnNpcActorPurchased(Action<NpcActorPurchasedEvent> handler);
    /// <summary>Return false to prevent the requested native transformation. Faulting callbacks do not veto.</summary>
    void AllowNpcTransform(Func<NpcTransformingEvent, bool> allow);
    /// <summary>Return false to suppress a requested NPC loot drop. Faulting callbacks do not veto.</summary>
    void AllowNpcLoot(Func<NpcLootCreatingEvent, bool> allow);
}
