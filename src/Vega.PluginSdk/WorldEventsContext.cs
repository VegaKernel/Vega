using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Read-only, world-local gameplay notifications owned by the plugin lifetime.</summary>
public interface IPluginWorldEvents
{
    /// <summary>Runs after native update work on the owning game thread. Keep callbacks short.</summary>
    void OnTickCompleted(Action<WorldTickCompletedEvent> handler);
    /// <summary>Read-only native save result, delivered on the owning game thread. Off-thread save notifications use bounded dispatch.</summary>
    void OnSaveCompleted(Action<WorldSaveCompletedEvent> handler);
    void OnNpcCreating(Action<NpcCreatingEvent> handler);
    void OnNpcKilled(Action<NpcKilledEvent> handler);
    /// <summary>Observes successful positive native strike returns. No cancellation or damage override is available.</summary>
    void OnNpcStrikeCompleted(Action<NpcStrikeCompletedEvent> handler);
    /// <summary>Observes the same native completion with detached caller context; does not certify attacker identity.</summary>
    void OnNpcStrikeCompletedWithSource(Action<NpcStrikeCompletedWithSourceEvent> handler) =>
        throw new NotSupportedException("Source-aware native strike observations are unavailable in this host.");
    void OnNpcTransforming(Action<NpcTransformingEvent> handler);
    void OnNpcLootDropped(Action<NpcLootDroppedEvent> handler);
    void OnProjectileSynchronized(Action<ProjectileSynchronizedEvent> handler);
    void OnProjectileRemoved(Action<ProjectileRemovedEvent> handler);
    void OnNpcActorInteracted(Action<NpcActorInteractedEvent> handler);
    void OnNpcActorPurchased(Action<NpcActorPurchasedEvent> handler);
    void OnNpcActorDialogueChoice(Action<NpcActorDialogueChoiceEvent> handler);
    void OnNpcActorQuest(Action<NpcActorQuestEvent> handler);
    void OnNpcActorThink(Action<NpcActorThinkEvent> handler);
    /// <summary>Return false to prevent the requested native transformation. Faulting callbacks do not veto.</summary>
    void AllowNpcTransform(Func<NpcTransformingEvent, bool> allow);
    /// <summary>Return false to suppress a requested NPC loot drop. Faulting callbacks do not veto.</summary>
    void AllowNpcLoot(Func<NpcLootCreatingEvent, bool> allow);
}
