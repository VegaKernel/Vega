using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginWorldEvents(HotModuleRegistrationScope owner, IWorldGameplayEvents? events) : IPluginWorldEvents
    {
        public void OnTickCompleted(Action<WorldTickCompletedEvent> handler) => Register("world.tick.completed", handler);
        public void OnHardmodeChanged(Action<WorldHardmodeChangedEvent> handler) => Register("world.hardmode.changed", handler);
        public void OnSaveCompleted(Action<WorldSaveCompletedEvent> handler) => Register("world.save.completed", handler);
        public void OnPlayerVitalsChanged(Action<PlayerVitalsChangedEvent> handler) => Register("world.player.vitals.changed", handler);
        public void OnPlayerDeathObserved(Action<PlayerDeathObservedEvent> handler) => Register("world.player.death.observed", handler);
        public void OnPlayerRespawnObserved(Action<PlayerRespawnObservedEvent> handler) => Register("world.player.respawn.observed", handler);
        public void OnPlayerDeathCountersChanged(Action<PlayerDeathCountersChangedEvent> handler) => Register("world.player.death-counters.changed", handler);
        public void OnPlayerPvpChanged(Action<PlayerPvpChangedEvent> handler) => Register("world.player.pvp.changed", handler);
        public void OnPlayerBuffsChanged(Action<PlayerBuffsChangedEvent> handler) => Register("world.player.buffs.changed", handler);
        public void OnPlayerHurtCompleted(Action<PlayerHurtCompletedEvent> handler) => Register("world.player.hurt.completed", handler);
        public void OnTileInteractionRequested(Action<PlayerTileInteractionEvent> handler) => Register("world.tile.requested", handler);
        public void OnPlayerTileChangesCommitted(Action<PlayerTileChangesCommittedEvent> handler) => Register("world.tile.changes.committed", handler);
        public void AllowTileInteraction(Func<PlayerTileInteractionEvent, bool> allow) => RegisterPolicy("world.tile.policy", allow);
        public void OnPlayerPvpChangeRequested(Action<PlayerPvpChangeRequestedEvent> handler) => Register("world.player.pvp.requested", handler);
        public void AllowPlayerPvpChange(Func<PlayerPvpChangeRequestedEvent, bool> allow) => RegisterPolicy("world.player.pvp.policy", allow);
        public void OnRegionEntered(Action<Vega.Contracts.Regions.RegionEnteredEvent> handler) => Register("world.region.entered", handler);
        public void OnRegionLeft(Action<Vega.Contracts.Regions.RegionLeftEvent> handler) => Register("world.region.left", handler);
        public void OnRegionChanged(Action<Vega.Contracts.Regions.RegionChangedEvent> handler) => Register("world.region.changed", handler);
        public void OnNpcCreating(Action<NpcCreatingEvent> handler) => Register("world.npc.creating", handler);
        public void OnNpcKilled(Action<NpcKilledEvent> handler) => Register("world.npc.killed", handler);
        public void OnNpcStrikeCompleted(Action<NpcStrikeCompletedEvent> handler) => Register("world.npc.strike-completed", handler);
        public void OnNpcStrikeCompletedWithSource(Action<NpcStrikeCompletedWithSourceEvent> handler) => Register("world.npc.strike-completed.source", handler);
        public void OnNpcTransforming(Action<NpcTransformingEvent> handler) => Register("world.npc.transforming", handler);
        public void OnNpcLootDropped(Action<NpcLootDroppedEvent> handler) => Register("world.npc.loot", handler);
        public void OnProjectileSynchronized(Action<ProjectileSynchronizedEvent> handler) => Register("world.projectile.sync", handler);
        public void OnProjectileRemoved(Action<ProjectileRemovedEvent> handler) => Register("world.projectile.removed", handler);
        public void OnNpcActorInteracted(Action<NpcActorInteractedEvent> handler) => Register("world.npc.actor.interacted", handler);
        public void OnNpcActorPurchased(Action<NpcActorPurchasedEvent> handler) => Register("world.npc.actor.purchased", handler);
        public void OnNpcActorDialogueChoice(Action<NpcActorDialogueChoiceEvent> handler) => Register("world.npc.actor.dialogue", handler);
        public void OnNpcActorQuest(Action<NpcActorQuestEvent> handler) => Register("world.npc.actor.quest", handler);
        public void OnNpcActorThink(Action<NpcActorThinkEvent> handler) => Register("world.npc.actor.think", handler);
        public void AllowNpcTransform(Func<NpcTransformingEvent, bool> allow) => RegisterPolicy("world.npc.transform.policy", allow);
        public void AllowNpcLoot(Func<NpcLootCreatingEvent, bool> allow) => RegisterPolicy("world.npc.loot.policy", allow);

        private void RegisterPolicy<T>(string operation, Func<T, bool> allow) where T : struct
        {
            ArgumentNullException.ThrowIfNull(allow);
            if (events is null) throw new NotSupportedException("World gameplay events are unavailable in this host.");
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                owner.Track(events.SubscribePolicy<T>(value => owner.lifetime.IsCancellationRequested || owner.executionGuard.Allows(operation, allow, value)));
            }
        }

        private void Register<T>(string operation, Action<T> handler) where T : struct
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (events is null) throw new NotSupportedException("World gameplay events are unavailable in this host.");
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                var registration = events.Subscribe<T>(value =>
                {
                    if (!owner.lifetime.IsCancellationRequested) owner.executionGuard.TryInvoke(operation, handler, value);
                });
                owner.Track(registration);
            }
        }
    }
}
