using Vega.Contracts.Worlds;
using Vega.Contracts.Regions;

namespace Vega.PluginSdk;

/// <summary>Read-only, world-local gameplay notifications owned by the plugin lifetime.</summary>
public interface IPluginWorldEvents
{
    /// <summary>Runs after native update work on the owning game thread. Keep callbacks short.</summary>
    void OnTickCompleted(Action<WorldTickCompletedEvent> handler);
    void OnHardmodeChanged(Action<WorldHardmodeChangedEvent> handler) => throw new NotSupportedException("World progression observations are unavailable in this host.");
    /// <summary>Read-only native save result, delivered on the owning game thread. Off-thread save notifications use bounded dispatch.</summary>
    void OnSaveCompleted(Action<WorldSaveCompletedEvent> handler);
    /// <summary>Net life and mana changes at native tick completion; not individual hits or verified heals.</summary>
    void OnPlayerVitalsChanged(Action<PlayerVitalsChangedEvent> handler) => throw new NotSupportedException("Player-state observations are unavailable in this host.");
    /// <summary>Observed alive-to-dead transition for the exact session.</summary>
    void OnPlayerDeathObserved(Action<PlayerDeathObservedEvent> handler) => throw new NotSupportedException("Player-state observations are unavailable in this host.");
    /// <summary>Observed dead-to-alive transition for the exact session.</summary>
    void OnPlayerRespawnObserved(Action<PlayerRespawnObservedEvent> handler) => throw new NotSupportedException("Player-state observations are unavailable in this host.");
    void OnPlayerDeathCountersChanged(Action<PlayerDeathCountersChangedEvent> handler) => throw new NotSupportedException("Player-state observations are unavailable in this host.");
    void OnPlayerPvpChanged(Action<PlayerPvpChangedEvent> handler) => throw new NotSupportedException("Player-state observations are unavailable in this host.");
    void OnPlayerBuffsChanged(Action<PlayerBuffsChangedEvent> handler) => throw new NotSupportedException("Player-state observations are unavailable in this host.");
    /// <summary>Observe an individual positive native Hurt completion, with explicit caller provenance.</summary>
    void OnPlayerHurtCompleted(Action<PlayerHurtCompletedEvent> handler) => throw new NotSupportedException();
    /// <summary>Observe a validated player interaction intent before native processing (not a committed edit).</summary>
    void OnTileInteractionRequested(Action<PlayerTileInteractionEvent> handler) => throw new NotSupportedException();
    /// <summary>Detached actual changed tile cells within the observed native interaction bounds; not a complete world-edit journal.</summary>
    void OnPlayerTileChangesCommitted(Action<PlayerTileChangesCommittedEvent> handler) => throw new NotSupportedException();
    /// <summary>Additional restriction after built-in checks. Returning false denies; true does not override any built-in protection.</summary>
    void AllowTileInteraction(Func<PlayerTileInteractionEvent, bool> allow) => throw new NotSupportedException();
    /// <summary>Observe validated player hostility/team requests, not committed changes.</summary>
    void OnPlayerPvpChangeRequested(Action<PlayerPvpChangeRequestedEvent> handler) => throw new NotSupportedException();
    /// <summary>Additional deny-only restriction on a player's requested hostility or team change.</summary>
    void AllowPlayerPvpChange(Func<PlayerPvpChangeRequestedEvent, bool> allow) => throw new NotSupportedException();
    /// <summary>After a player session enters any containing region (not just the top region).</summary>
    void OnRegionEntered(Action<RegionEnteredEvent> handler) => throw new NotSupportedException("Region observations are unavailable in this host.");
    /// <summary>After a player session leaves a region, including disconnect, deletion and resize.</summary>
    void OnRegionLeft(Action<RegionLeftEvent> handler) => throw new NotSupportedException("Region observations are unavailable in this host.");
    /// <summary>Committed local region changes, deferred to the game thread. Re-read on revision conflict.</summary>
    void OnRegionChanged(Action<RegionChangedEvent> handler) => throw new NotSupportedException("Region observations are unavailable in this host.");
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
