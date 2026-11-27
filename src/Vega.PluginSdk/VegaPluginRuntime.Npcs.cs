using System.Runtime.CompilerServices;
using System.Collections.Frozen;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

internal static partial class VegaPluginRuntime
{
    private sealed class PluginNpcs(IVegaPluginContext context) : IPluginWorldNpcs, IDisposable
    {
        private readonly object actorSync = new();
        private readonly HashSet<OwnedActor> actors = [];
        private bool tracked;
        private bool disposed;

        public async ValueTask<NpcActorCreationResult> CreateActorAsync(NpcActorDefinition definition, PlayerPosition position,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (current.NpcActors is null || context is not IHotModuleContext owner)
                return new(NpcOperationStatus.DispatcherUnavailable, ErrorCode: "npc.actors_unavailable");
            var result = await current.NpcActors.CreateAsync(current.WorldId, definition, position, cancellationToken).ConfigureAwait(false);
            if (result.Actor is { } actor)
            {
                try
                {
                    lock (actorSync)
                    {
                        ObjectDisposedException.ThrowIf(disposed, this);
                        // One aggregate registration per plugin, not one retained registration per retired NPC.
                        if (!tracked) { owner.Track(this); tracked = true; }
                        actors.RemoveWhere(static lease => lease.IsRetired);
                        var lease = new OwnedActor(this, actor);
                        actors.Add(lease);
                        return result with { Actor = lease };
                    }
                }
                catch { actor.Dispose(); throw; }
            }
            return result;
        }

        public void Dispose()
        {
            OwnedActor[] owned;
            lock (actorSync)
            {
                if (disposed) return;
                disposed = true;
                owned = actors.ToArray();
                actors.Clear();
            }
            foreach (var actor in owned) actor.Dispose();
        }

        private sealed class OwnedActor(PluginNpcs owner, INpcActor actor) : INpcActor
        {
            private INpcActor? inner = actor;
            public NpcActorId Id { get; } = actor.Id;
            public bool IsRetired => Volatile.Read(ref inner)?.IsRetired != false;
            public ValueTask<NpcActorSnapshot?> CaptureAsync(CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.CaptureAsync(cancellationToken) ?? ValueTask.FromResult<NpcActorSnapshot?>(null);
            public ValueTask<bool> RenameAsync(string name, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.RenameAsync(name, cancellationToken) ?? ValueTask.FromResult(false);
            public ValueTask<bool> TeleportAsync(PlayerPosition position, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.TeleportAsync(position, cancellationToken) ?? ValueTask.FromResult(false);
            public ValueTask<bool> SetIntentAsync(NpcActorIntent intent, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.SetIntentAsync(intent, cancellationToken) ?? ValueTask.FromResult(false);
            public ValueTask<bool> SetShopAsync(NpcShopCatalog? shop, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.SetShopAsync(shop, cancellationToken) ?? ValueTask.FromResult(false);
            public ValueTask<NpcShopPresentationStatus> PresentShopAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.PresentShopAsync(player, cancellationToken) ?? ValueTask.FromResult(NpcShopPresentationStatus.ActorGone);
            public ValueTask<NpcShopPurchaseResult> PurchaseAsync(PlayerSnapshot player, string offerId, long expectedShopRevision, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.PurchaseAsync(player, offerId, expectedShopRevision, cancellationToken) ??
                    ValueTask.FromResult(new NpcShopPurchaseResult(NpcShopPurchaseStatus.ActorGone));
            public ValueTask<NpcShopPurchaseResult> PurchaseCurrencyAsync(PlayerSnapshot player, string offerId, long expectedShopRevision,
                Guid operationId, CancellationToken cancellationToken = default) => Volatile.Read(ref inner)?.PurchaseCurrencyAsync(player, offerId,
                    expectedShopRevision, operationId, cancellationToken) ?? ValueTask.FromResult(new NpcShopPurchaseResult(NpcShopPurchaseStatus.ActorGone));
            public ValueTask<bool> SetDialogueAsync(NpcDialogueDefinition? dialogue, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.SetDialogueAsync(dialogue, cancellationToken) ?? ValueTask.FromResult(false);
            public ValueTask<NpcDialogueResult> BeginDialogueAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.BeginDialogueAsync(player, cancellationToken) ?? ValueTask.FromResult(new NpcDialogueResult(NpcDialogueStatus.ActorGone));
            public ValueTask<NpcDialogueResult> ChooseDialogueAsync(PlayerSnapshot player, long expectedViewRevision, string choiceId, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.ChooseDialogueAsync(player, expectedViewRevision, choiceId, cancellationToken) ??
                    ValueTask.FromResult(new NpcDialogueResult(NpcDialogueStatus.ActorGone));
            public ValueTask<bool> SetQuestsAsync(NpcQuestCatalog? quests, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.SetQuestsAsync(quests, cancellationToken) ?? ValueTask.FromResult(false);
            public ValueTask<IReadOnlyList<NpcQuestView>> GetQuestsAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.GetQuestsAsync(player, cancellationToken) ?? ValueTask.FromResult<IReadOnlyList<NpcQuestView>>([]);
            public ValueTask<NpcQuestResult> AcceptQuestAsync(PlayerSnapshot player, string questId, long expectedCatalogRevision, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.AcceptQuestAsync(player, questId, expectedCatalogRevision, cancellationToken) ??
                    ValueTask.FromResult(new NpcQuestResult(NpcQuestStatus.ActorGone));
            public ValueTask<NpcQuestResult> TurnInQuestAsync(PlayerSnapshot player, string questId, long expectedCatalogRevision, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.TurnInQuestAsync(player, questId, expectedCatalogRevision, cancellationToken) ??
                    ValueTask.FromResult(new NpcQuestResult(NpcQuestStatus.ActorGone));
            public ValueTask<NpcEconomyBalanceResult> GetCurrencyBalanceAsync(PlayerSnapshot player, string currencyId, CancellationToken cancellationToken = default) =>
                Volatile.Read(ref inner)?.GetCurrencyBalanceAsync(player, currencyId, cancellationToken) ??
                    ValueTask.FromResult(new NpcEconomyBalanceResult(NpcEconomyStatus.Unavailable));
            public ValueTask<NpcEconomyBalanceResult> CreditCurrencyAsync(PlayerSnapshot player, NpcEconomyPrice amount, Guid operationId,
                CancellationToken cancellationToken = default) => Volatile.Read(ref inner)?.CreditCurrencyAsync(player, amount, operationId, cancellationToken) ??
                    ValueTask.FromResult(new NpcEconomyBalanceResult(NpcEconomyStatus.Unavailable));
            public void Dispose()
            {
                INpcActor? current = Interlocked.Exchange(ref inner, null);
                if (current is null) return;
                lock (owner.actorSync) owner.actors.Remove(this);
                current.Dispose();
            }
        }

        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Npcs?.SnapshotAsync(current.WorldId, cancellationToken) ??
                ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>([]);
        }

        public ValueTask<NpcSnapshot?> CaptureAsync(
            NpcHandle handle,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (handle.WorldId != current.WorldId)
            {
                return ValueTask.FromResult<NpcSnapshot?>(null);
            }

            return current.Npcs?.CaptureAsync(handle, cancellationToken) ??
                ValueTask.FromResult<NpcSnapshot?>(null);
        }

        public ValueTask<NpcOperationResult> SpawnAsync(
            int type,
            PlayerPosition position,
            int? targetPlayerSlot = null,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Npcs?.SpawnAsync(
                new NpcSpawnRequest(current.WorldId, type, position, Actor(context), targetPlayerSlot),
                cancellationToken) ?? Unavailable();
        }

        public ValueTask<NpcOperationResult> TeleportAsync(
            NpcHandle expected,
            PlayerPosition position,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (expected.WorldId != current.WorldId)
            {
                return ValueTask.FromResult(new NpcOperationResult(
                    NpcOperationStatus.Invalid,
                    ErrorCode: "npc.wrong_world"));
            }

            return current.Npcs?.TeleportAsync(
                new NpcTeleportRequest(expected, position, Actor(context)),
                cancellationToken) ?? Unavailable();
        }

        public ValueTask<NpcOperationResult> DespawnAsync(
            NpcHandle expected,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (expected.WorldId != current.WorldId)
            {
                return ValueTask.FromResult(new NpcOperationResult(
                    NpcOperationStatus.Invalid,
                    ErrorCode: "npc.wrong_world"));
            }

            return current.Npcs?.DespawnAsync(
                new NpcDespawnRequest(expected, Actor(context)),
                cancellationToken) ?? Unavailable();
        }

        private static ValueTask<NpcOperationResult> Unavailable() => ValueTask.FromResult(
            new NpcOperationResult(
                NpcOperationStatus.DispatcherUnavailable,
                ErrorCode: "npc.unavailable"));
    }
}
