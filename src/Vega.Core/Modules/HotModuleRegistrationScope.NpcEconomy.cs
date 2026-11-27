using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    public IPluginNpcEconomy NpcEconomy { get; private set; } = null!;

    private void InitializeNpcEconomy(string pluginName, PluginNpcEconomyRegistry? registry) => NpcEconomy = new PluginNpcEconomy(this, pluginName, registry);

    private sealed class PluginNpcEconomy(HotModuleRegistrationScope owner, string pluginName, PluginNpcEconomyRegistry? registry) : IPluginNpcEconomy
    {
        public IReadOnlyList<NpcEconomyProviderAddress> Snapshot() => registry?.Snapshot() ?? [];

        public void Register(string name, int version, INpcEconomyProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (registry is null) throw new NotSupportedException("Named NPC economy providers are unavailable in this host.");
            var address = new NpcEconomyProviderAddress(pluginName, name, version);
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                if (!owner.executionGuard.AcceptsCallbacks) throw new InvalidOperationException("Plugin activation is retiring.");
                owner.Track(registry.Register(address, new GuardedProvider(owner, provider, name), () => owner.executionGuard.AcceptsCallbacks));
            }
        }
    }

    private sealed class GuardedProvider(HotModuleRegistrationScope owner, INpcEconomyProvider provider, string name) : INpcEconomyProvider
    {
        public async ValueTask<NpcEconomyBalanceResult> ReadBalanceAsync(NpcAccountScope scope, string currencyId, CancellationToken cancellationToken = default) =>
            Validate(await InvokeAsync("balance", token => provider.ReadBalanceAsync(scope, currencyId, token), new NpcEconomyBalanceResult(NpcEconomyStatus.Unavailable), cancellationToken).ConfigureAwait(false));

        public async ValueTask<NpcEconomyBalanceResult> CreditAsync(NpcAccountScope scope, NpcEconomyPrice credit, Guid operationId, CancellationToken cancellationToken = default) =>
            Validate(await InvokeAsync("credit", token => provider.CreditAsync(scope, credit, operationId, token), new NpcEconomyBalanceResult(NpcEconomyStatus.Unavailable), cancellationToken).ConfigureAwait(false));

        public async ValueTask<NpcEconomyReservationResult> ReserveDebitAsync(NpcAccountScope scope, NpcEconomyPrice price, Guid operationId, CancellationToken cancellationToken = default)
        {
            NpcEconomyReservationResult result = await InvokeAsync("reserve", token => provider.ReserveDebitAsync(scope, price, operationId, token),
                new NpcEconomyReservationResult(NpcEconomyStatus.Unavailable, operationId), cancellationToken).ConfigureAwait(false);
            if (result is null || !Enum.IsDefined(result.Status) || result.RemainingBalance is < 0 or > NpcEconomyPrice.MaximumBalance ||
                result.OperationId == Guid.Empty || (result.Status is NpcEconomyStatus.Applied or NpcEconomyStatus.AlreadyApplied && result.OperationId != operationId))
                throw Uncertain("reserve returned an invalid receipt");
            return result;
        }

        public ValueTask<bool> FinalizeDebitAsync(NpcAccountScope scope, Guid operationId, bool committed, CancellationToken cancellationToken = default) =>
            InvokeAsync("finalize", token => provider.FinalizeDebitAsync(scope, operationId, committed, token), false, cancellationToken);

        private async ValueTask<T> InvokeAsync<T>(string operation, Func<CancellationToken, ValueTask<T>> callback, T unavailable, CancellationToken cancellationToken)
        {
            bool entered = false;
            try
            {
                return await owner.RunScopedAsync(async token =>
                {
                    var result = await owner.executionGuard.InvokeServiceAsync($"npc-economy:{name}:{operation}", ct =>
                    {
                        entered = true;
                        return callback(ct);
                    }, token).ConfigureAwait(false);
                    if (result.Accepted) return result.Value!;
                    if (!entered) return unavailable;
                    // A user callback may already have durably mutated its ledger before throwing.
                    // Do NOT turn that into Unavailable (which means no reservation was made).
                    throw Uncertain(operation);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (!entered) { return unavailable; }
            catch (PluginScopeBusyException) when (!entered) { return unavailable; }
            catch (OperationCanceledException) when (!entered && !cancellationToken.IsCancellationRequested) { return unavailable; }
        }

        private static NpcEconomyBalanceResult Validate(NpcEconomyBalanceResult result)
        {
            if (result is null || !Enum.IsDefined(result.Status) || result.Balance is < 0 or > NpcEconomyPrice.MaximumBalance || result.PendingOperationId == Guid.Empty)
                throw Uncertain("invalid balance receipt");
            return result;
        }

        private static InvalidOperationException Uncertain(string operation) => new($"Named NPC economy provider outcome is uncertain: {operation}.");
    }
}
