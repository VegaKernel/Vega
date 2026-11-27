using Vega.Contracts.Accounts;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed partial class TerrariaNpcActorService
{
    private readonly record struct ScopedAccount(NpcAccountScope Scope, PlayerAccountBinding Binding);
    private sealed record QuestOperation(ScopedAccount Account, NpcQuestDefinition Quest, long Revision);
    private sealed record QuestPreparation(NpcQuestResult? Failure, QuestOperation? Operation = null);
    private sealed record EconomyOperation(ScopedAccount Account, NpcShopOffer Offer, long Revision);
    private sealed record EconomyPreparation(NpcShopPurchaseResult? Result, EconomyOperation? Operation = null);

    private bool TryAccount(Actor actor, PlayerSnapshot player, string? name, bool conversation, out ScopedAccount scoped)
    {
        scoped = default;
        if (name is null || !IsCurrent(actor) || (conversation ? !TryConversation(actor, player, out _) : !HasLiveSession(player)) ||
            accounts?.TryGetBinding(player, out var binding) != true) return false;
        var scope = new NpcAccountScope(ownedWorld, name, binding.AccountId);
        if (!scope.IsValid) return false;
        scoped = new(scope, binding);
        return true;
    }

    private bool SameAccount(Actor actor, PlayerSnapshot player, ScopedAccount expected, bool conversation) =>
        TryAccount(actor, player, expected.Scope.Scope, conversation, out var current) && current == expected;

    // Providers may perform synchronous work before returning a ValueTask. Always invoke them
    // from a worker, even when the initial game-thread capture completed synchronously.
    private static async Task<T> OffThread<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        CancellationToken effective = deadline.Token;
        Task<T> work = Task.Run(async () => await action(effective).ConfigureAwait(false));
        try { return await work.WaitAsync(effective).ConfigureAwait(false); }
        catch
        {
            // A non-cooperative provider may still settle later. Do not let it block the
            // actor caller indefinitely or allow an unobserved background exception.
            if (work.IsFaulted) _ = work.Exception;
            else if (!work.IsCompleted) _ = work.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private QuestPreparation PreparePersistentQuest(Actor actor, PlayerSnapshot player, string questId, long revision)
    {
        NpcQuestStatus? status = !IsCurrent(actor) ? NpcQuestStatus.ActorGone :
            !TryConversation(actor, player, out _) ? NpcQuestStatus.NotInteracting :
            actor.Interactions.QuestRevision != revision ? NpcQuestStatus.CatalogChanged : null;
        if (status is { } failure) return new(new(failure, actor.Interactions.QuestRevision));
        if (actor.Interactions.FindQuest(questId) is not { } quest) return new(new(NpcQuestStatus.UnknownQuest, revision));
        if (!TryAccount(actor, player, actor.Definition.PersistentQuestScope, true, out var account))
            return new(new(NpcQuestStatus.AccountRequired, revision));
        return new(null, new(account, quest, revision));
    }

    private static NpcQuestView QuestView(NpcQuestDefinition quest, NpcQuestProgress progress) => new(quest, progress.State)
    { PendingOperationId = progress.State == NpcQuestState.CompletionPending ? progress.OperationId : null };
    private static NpcQuestResult QuestResult(NpcQuestStatus status, QuestOperation operation, NpcQuestProgress progress,
        NpcShopPurchaseStatus? inventory = null, Guid? operationId = null) => new(status, operation.Revision, QuestView(operation.Quest, progress), inventory)
    { OperationId = operationId ?? progress.OperationId };

    private async ValueTask<IReadOnlyList<NpcQuestView>> GetPersistentQuestsAsync(Actor actor, PlayerSnapshot player, CancellationToken token)
    {
        var context = await Dispatch(() => TryAccount(actor, player, actor.Definition.PersistentQuestScope, true, out var account) &&
                actor.Interactions.Quests is { } catalog ? (Catalog: catalog, Account: account, Revision: actor.Interactions.QuestRevision) : default,
            default, token).ConfigureAwait(false);
        if (context.Catalog is null) return [];
        try
        {
            var progress = await OffThread(ct => persistentQuests!.ReadAsync(context.Account.Scope, ct), token).ConfigureAwait(false);
            return await Dispatch<IReadOnlyList<NpcQuestView>>(() =>
                actor.Interactions.QuestRevision == context.Revision && SameAccount(actor, player, context.Account, true)
                    ? Array.AsReadOnly(context.Catalog.Quests.Select(quest => QuestView(quest, progress.GetValueOrDefault(quest.Id) ?? new(NpcQuestState.Available))).ToArray()) : [],
                [], token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogSafe("Persistent quest read failed.", ex); return []; }
    }

    private async ValueTask<NpcQuestResult> AcceptPersistentQuestAsync(Actor actor, PlayerSnapshot player, string questId, long revision, CancellationToken token)
    {
        var prepared = await Dispatch(() => PreparePersistentQuest(actor, player, questId, revision),
            new QuestPreparation(new(NpcQuestStatus.Unavailable)), token).ConfigureAwait(false);
        if (prepared.Operation is not { } operation) return prepared.Failure!;
        try
        {
            var accepted = await OffThread(ct => persistentQuests!.AcceptAsync(operation.Account.Scope, questId, operation.Quest.Repeatable, ct), token).ConfigureAwait(false);
            if (accepted.Status == NpcQuestStatus.Applied)
                await PublishAccountQuestAsync(actor, player, operation, NpcQuestState.Accepted).ConfigureAwait(false);
            return QuestResult(accepted.Status, operation, accepted.Progress);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogSafe("Persistent quest acceptance failed.", ex); return new(NpcQuestStatus.PersistenceUnavailable, revision); }
    }

    private async ValueTask<NpcQuestResult> TurnInPersistentQuestAsync(Actor actor, PlayerSnapshot player, string questId, long revision, CancellationToken token)
    {
        var prepared = await Dispatch(() => PreparePersistentQuest(actor, player, questId, revision),
            new QuestPreparation(new(NpcQuestStatus.Unavailable)), token).ConfigureAwait(false);
        if (prepared.Operation is not { } operation) return prepared.Failure!;
        Guid id = Guid.NewGuid();
        var pending = new NpcQuestProgress(NpcQuestState.CompletionPending, id);
        NpcQuestResult Recovery() => QuestResult(NpcQuestStatus.RecoveryRequired, operation, pending, operationId: id);
        NpcQuestProgressUpdate reserved;
        try
        {
            reserved = await OffThread(ct => persistentQuests!.ReserveTurnInAsync(operation.Account.Scope, questId, id, ct), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed transport may have durably reserved before reporting the error. Do not
            // issue items or silently retry the same operation with a new reservation.
            LogSafe($"Quest reservation outcome is uncertain (operation {id}).", ex);
            return Recovery();
        }
        if (reserved.Status != NpcQuestStatus.Applied) return QuestResult(reserved.Status, operation, reserved.Progress);
        if (reserved.Progress != pending) return Recovery();

        NpcShopPurchaseStatus inventoryStatus;
        try
        {
            // Once reserved, resolve with a non-cancelable completion. A caller cancel or
            // plugin unload cannot cause us to forget a completed native mutation.
            inventoryStatus = await Dispatch(() =>
            {
                if (actor.Interactions.QuestRevision != revision || !SameAccount(actor, player, operation.Account, true) ||
                    !TryConversation(actor, player, out var live)) return NpcShopPurchaseStatus.InventoryChanged;
                var quest = operation.Quest;
                var offer = new NpcShopOffer(quest.Id, quest.Reward.ItemType, checked((short)quest.Reward.Amount), 0, quest.Reward.Prefix)
                    { ItemPrice = quest.Required };
                return accounts!.TryRunWithBinding(operation.Account.Binding, () => Exchange(actor, player, live, offer,
                        () => actor.Interactions.QuestRevision == revision && SameAccount(actor, player, operation.Account, true), static () => { }),
                    out var result) ? result : NpcShopPurchaseStatus.AccountRequired;
            }, NpcShopPurchaseStatus.Unavailable, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { LogSafe($"Native quest outcome is uncertain (operation {id}).", ex); return Recovery(); }

        bool granted = inventoryStatus == NpcShopPurchaseStatus.Committed;
        if (granted && !await CheckpointGrantAsync(player, operation.Account, id).ConfigureAwait(false)) return Recovery();
        try
        {
            if (!await OffThread(ct => persistentQuests!.FinalizeTurnInAsync(operation.Account.Scope, questId, id, granted, ct)).ConfigureAwait(false))
                return Recovery();
        }
        catch (Exception ex) { LogSafe($"Quest finalization is uncertain (operation {id}).", ex); return Recovery(); }
        if (!granted) return QuestResult(NpcQuestStatus.InventoryRejected, operation, new(NpcQuestState.Accepted), inventoryStatus, id);
        await PublishAccountQuestAsync(actor, player, operation, NpcQuestState.Completed).ConfigureAwait(false);
        return QuestResult(NpcQuestStatus.Applied, operation, new(NpcQuestState.Completed, id));
    }

    private async ValueTask PublishAccountQuestAsync(Actor actor, PlayerSnapshot player, QuestOperation operation, NpcQuestState state)
    {
        try
        {
            _ = await Dispatch(() =>
            {
                if (!SameAccount(actor, player, operation.Account, false)) return false;
                PublishSafe(new NpcActorQuestEvent(actor.Id, player, operation.Quest.Id, state), "Persistent quest observer failed.");
                return true;
            }, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { LogSafe("Persistent quest notification could not be dispatched.", ex); }
    }

    private EconomyPreparation PrepareEconomyPurchase(Actor actor, PlayerSnapshot player, string offerId, long revision, bool walletOnly)
    {
        EconomyPreparation Failure(NpcShopPurchaseStatus status) => new(new(status, actor.ShopRevision));
        if (!IsCurrent(actor)) return Failure(NpcShopPurchaseStatus.ActorGone);
        if (actor.Shop is not { } catalog || actor.ShopRevision != revision) return Failure(NpcShopPurchaseStatus.CatalogChanged);
        if (!TryConversation(actor, player, out _) || !actor.Native.townNPC) return Failure(NpcShopPurchaseStatus.NotInteracting);
        var offer = catalog.Offers.FirstOrDefault(value => value.Id == offerId);
        if (offer.Id is null || actor.SoldOut.Contains(offerId)) return Failure(NpcShopPurchaseStatus.OfferUnavailable);
        if (offer.EconomyPrice is null) return walletOnly ? Failure(NpcShopPurchaseStatus.InvalidOffer) : new(Purchase(actor, player, offerId, revision));
        if (!TryAccount(actor, player, actor.Definition.EconomyScope, true, out var account)) return Failure(NpcShopPurchaseStatus.AccountRequired);
        if (!HasInventoryAuthority(player)) return Failure(NpcShopPurchaseStatus.InventoryAuthorityRequired);
        return new(null, new(account, offer, revision));
    }

    private async ValueTask<NpcShopPurchaseResult> PurchaseWithEconomyAsync(Actor actor, PlayerSnapshot player, string offerId, long revision,
        Guid? requestedOperationId, CancellationToken token)
    {
        var prepared = await Dispatch(() => PrepareEconomyPurchase(actor, player, offerId, revision, requestedOperationId.HasValue),
            new EconomyPreparation(new(NpcShopPurchaseStatus.Unavailable)), token).ConfigureAwait(false);
        if (prepared.Operation is not { } operation) return prepared.Result!;
        NpcEconomyPrice price = operation.Offer.EconomyPrice!.Value;
        Guid id = requestedOperationId ?? Guid.NewGuid();
        NpcShopPurchaseResult Recovery(Guid operationId) => new(NpcShopPurchaseStatus.RecoveryRequired, revision)
            { EconomyPaid = price, OperationId = operationId };
        NpcEconomyReservationResult reservation;
        try { reservation = await OffThread(ct => economyProvider!.ReserveDebitAsync(operation.Account.Scope, price, id, ct), token).ConfigureAwait(false); }
        catch (Exception ex) { LogSafe($"Economy reservation outcome is uncertain (operation {id}).", ex); return Recovery(id); }
        if (reservation.Status is NpcEconomyStatus.RecoveryRequired or NpcEconomyStatus.AlreadyApplied)
            return Recovery(reservation.OperationId);
        if (reservation.Status != NpcEconomyStatus.Applied)
            return new(reservation.Status == NpcEconomyStatus.InsufficientFunds ? NpcShopPurchaseStatus.InsufficientFunds : NpcShopPurchaseStatus.EconomyUnavailable, revision);
        if (reservation.OperationId != id) return Recovery(id);

        (NpcShopPurchaseStatus Status, long Revision) native;
        try
        {
            native = await Dispatch(() =>
            {
                if (actor.ShopRevision != revision || actor.SoldOut.Contains(offerId) || !SameAccount(actor, player, operation.Account, true) ||
                    !TryConversation(actor, player, out var live)) return (NpcShopPurchaseStatus.InventoryChanged, actor.ShopRevision);
                var grant = operation.Offer with { EconomyPrice = null, CoinPrice = 0 };
                var status = accounts!.TryRunWithBinding(operation.Account.Binding, () => Exchange(actor, player, live, grant,
                    () => actor.ShopRevision == revision && !actor.SoldOut.Contains(offerId) && SameAccount(actor, player, operation.Account, true),
                    () => { if (grant.BuyOnce) { actor.SoldOut.Add(offerId); actor.InvalidateShopPresentation(); } }), out var result)
                    ? result : NpcShopPurchaseStatus.AccountRequired;
                return (status, actor.ShopRevision);
            }, (NpcShopPurchaseStatus.Unavailable, revision), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { LogSafe($"Native wallet purchase outcome is uncertain (operation {id}).", ex); return Recovery(id); }

        bool granted = native.Status == NpcShopPurchaseStatus.Committed;
        if (granted && !await CheckpointGrantAsync(player, operation.Account, id).ConfigureAwait(false)) return Recovery(id);
        try
        {
            if (!await OffThread(ct => economyProvider!.FinalizeDebitAsync(operation.Account.Scope, id, granted, ct)).ConfigureAwait(false))
                return Recovery(id);
        }
        catch (Exception ex) { LogSafe($"Economy finalization is uncertain (operation {id}).", ex); return Recovery(id); }
        if (!granted) return new(native.Status, native.Revision) { OperationId = id };
        var purchase = new NpcShopPurchaseResult(native.Status, native.Revision, 0, operation.Offer.ItemType, operation.Offer.Stack)
        { EconomyPaid = price, OperationId = id };
        try
        {
            _ = await Dispatch(() =>
            {
                if (!SameAccount(actor, player, operation.Account, false)) return false;
                PublishSafe(new NpcActorPurchasedEvent(actor.Id, player, offerId, purchase), "Committed wallet purchase observer failed.");
                return true;
            }, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { LogSafe("Wallet purchase notification could not be dispatched.", ex); }
        return purchase;
    }

    private async ValueTask<NpcEconomyBalanceResult> CurrencyBalanceAsync(Actor actor, PlayerSnapshot player, string currencyId, CancellationToken token)
    {
        if (!NpcAccountScope.IsValidName(currencyId)) return new(NpcEconomyStatus.InvalidRequest);
        var scoped = await Dispatch<ScopedAccount?>(() => TryAccount(actor, player, actor.Definition.EconomyScope, false, out var account) ? account : null,
            null, token).ConfigureAwait(false);
        if (scoped is not { } captured) return new(NpcEconomyStatus.AccountRequired);
        try
        {
            var balance = await OffThread(ct => economyProvider!.ReadBalanceAsync(captured.Scope, currencyId, ct), token).ConfigureAwait(false);
            return await Dispatch(() => SameAccount(actor, player, captured, false) ? balance : new(NpcEconomyStatus.AccountRequired),
                new NpcEconomyBalanceResult(NpcEconomyStatus.Unavailable), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogSafe("Wallet balance read failed.", ex); return new(NpcEconomyStatus.Unavailable); }
    }

    private async ValueTask<NpcEconomyBalanceResult> CreditCurrencyAsync(Actor actor, PlayerSnapshot player, NpcEconomyPrice credit, Guid operationId, CancellationToken token)
    {
        if (!credit.IsValid || operationId == Guid.Empty) return new(NpcEconomyStatus.InvalidRequest);
        var scoped = await Dispatch<ScopedAccount?>(() => TryAccount(actor, player, actor.Definition.EconomyScope, false, out var account) ? account : null,
            null, token).ConfigureAwait(false);
        if (scoped is not { } captured) return new(NpcEconomyStatus.AccountRequired);
        try { return await OffThread(ct => economyProvider!.CreditAsync(captured.Scope, credit, operationId, ct), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { LogSafe($"Idempotent wallet credit failed (operation {operationId}).", ex); return new(NpcEconomyStatus.Unavailable); }
    }

    private async ValueTask<bool> CheckpointGrantAsync(PlayerSnapshot player, ScopedAccount account, Guid operationId)
    {
        // Offline/native-only embedding may supply its own durability policy. The production
        // composition always supplies the exact authenticated SSC checkpoint delegate.
        if (persistInventory is null) return true;
        try
        {
            return await OffThread(ct => persistInventory(player, account.Binding, ct)).ConfigureAwait(false);
        }
        catch (Exception ex) { LogSafe($"Actor inventory checkpoint is uncertain (operation {operationId}).", ex); return false; }
    }
}
