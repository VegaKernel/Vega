using Vega.Contracts.Items;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    // CancelAsync marks its own token synchronously, but propagation to a linked token
    // is an asynchronous callback. Track the actual service tokens and cancel each
    // directly at retirement, before awaiting general activation cancellation.
    private readonly Dictionary<CancellationTokenSource, Task?> pendingActionTokens = [];

    private Task BeginRetirementCancellationLocked()
    {
        if (retirementCancellation is not null) return retirementCancellation;
        var cancellations = new List<Task>(pendingActionTokens.Count + 1);
        foreach (CancellationTokenSource source in pendingActionTokens.Keys.ToArray())
        {
            Task cancellation = source.CancelAsync();
            pendingActionTokens[source] = cancellation;
            cancellations.Add(cancellation);
        }
        cancellations.Add(lifetime.CancelAsync());
        return retirementCancellation = Task.WhenAll(cancellations);
    }

    private sealed class PluginPlayerActions(HotModuleRegistrationScope owner, IPlayerDirectory? directory,
        WorldId? worldId, IPlayerVitalOperations? vitals, IPlayerItemDelivery? items) : IPluginPlayerActions
    {
        public bool VitalsAvailable => directory is not null && vitals is not null;
        public bool ItemDeliveryAvailable => directory is not null && items is not null;

        public ValueTask<PlayerOperationResult> HealAsync(PlayerSnapshot player, int amount, CancellationToken cancellationToken = default) =>
            InvokeAsync(player, VitalsAvailable, token => vitals!.HealAsync(player, amount, token), cancellationToken);
        public ValueTask<PlayerOperationResult> BuffAsync(PlayerSnapshot player, string buff, int seconds, CancellationToken cancellationToken = default) =>
            InvokeAsync(player, VitalsAvailable, token => vitals!.BuffAsync(player, buff, seconds, token), cancellationToken);
        public ValueTask<PlayerOperationResult> KillAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
            InvokeAsync(player, VitalsAvailable, token => vitals!.KillAsync(player, token), cancellationToken);
        public ValueTask<PlayerOperationResult> SlapAsync(PlayerSnapshot player, int damage, CancellationToken cancellationToken = default) =>
            InvokeAsync(player, VitalsAvailable, token => vitals!.SlapAsync(player, damage, token), cancellationToken);
        public ValueTask<PlayerOperationResult> RespawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
            InvokeAsync(player, VitalsAvailable, token => vitals!.RespawnAsync(player, token), cancellationToken);
        public ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string item, int stack = 1, int prefix = 0,
            CancellationToken cancellationToken = default) =>
            InvokeAsync(player, ItemDeliveryAvailable, token => items!.GiveAsync(player, item, stack, prefix, token), cancellationToken);

        private ValueTask<PlayerOperationResult> InvokeAsync(PlayerSnapshot player, bool supported,
            Func<CancellationToken, ValueTask<PlayerOperationResult>> action, CancellationToken callerToken)
        {
            ArgumentNullException.ThrowIfNull(player);
            TaskCompletionSource<PlayerOperationResult> completion;
            CancellationTokenSource linked;
            lock (owner.sync)
            {
                if (owner.disposed || !owner.executionGuard.AcceptsCallbacks || callerToken.IsCancellationRequested ||
                    owner.lifetime.IsCancellationRequested)
                    return ValueTask.FromResult(PlayerOperationResult.Cancelled());
                if (!supported) return ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
                linked = callerToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(callerToken)
                    : new CancellationTokenSource();
                owner.pendingActionTokens.Add(linked, null);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                // The activation's drain sees the operation before any service work starts;
                // service dispatch and native callbacks never execute under this activation lock.
                owner.TrackTask(completion.Task);
            }
            _ = ExecuteAsync(player, action, linked, completion);
            return new(completion.Task);
        }

        private async Task ExecuteAsync(PlayerSnapshot player, Func<CancellationToken, ValueTask<PlayerOperationResult>> action,
            CancellationTokenSource linked, TaskCompletionSource<PlayerOperationResult> completion)
        {
            PlayerOperationResult result;
            using (linked)
            {
                try
                {
                    if (linked.IsCancellationRequested || !owner.executionGuard.AcceptsCallbacks)
                        result = PlayerOperationResult.Cancelled();
                    else if (player.SessionId is not { } session || (worldId is { } hosted && player.WorldId != hosted) ||
                        !directory!.TryGet(player.Id, out PlayerSnapshot? current) || current is null ||
                        current.SessionId != session || current.WorldId != player.WorldId)
                        result = PlayerOperationResult.NotFound();
                    else
                        // Native service consumes the exact snapshot and rechecks the session after game-thread dispatch.
                        result = await action(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    result = PlayerOperationResult.Cancelled();
                }
                catch (Exception ex)
                {
                    result = PlayerOperationResult.Failed("player.action_failed");
                    try { owner.logger.Log(OperationsLogLevel.Error, owner.logSource, "Player action failed.", ex); }
                    catch (Exception) { /* Logging must not strand the tracked action during retirement. */ }
                }

                Task? cancellation;
                lock (owner.sync) owner.pendingActionTokens.Remove(linked, out cancellation);
                if (cancellation is not null)
                {
                    try { await cancellation.ConfigureAwait(false); }
                    catch (Exception) { /* Retirement observes the cancellation task's failure. */ }
                }
            }
            // An operation already issued by the native service remains Applied. Do not
            // replace its authoritative result just because retirement has since started.
            completion.TrySetResult(result);
        }
    }
}
