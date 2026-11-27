using Vega.Contracts.Worlds;

namespace Vega.Core.Modules;

/// <summary>Routes named providers without exposing their objects to NPC actors; explicit addresses never fall back.</summary>
public sealed class PluginNpcEconomyRegistry(INpcEconomyProvider builtIn) : INpcEconomyProvider
{
    private sealed record Registration(NpcEconomyProviderAddress Address, INpcEconomyProvider Provider, Func<bool> Active);
    private readonly object sync = new();
    private readonly Dictionary<(string, string, int), Registration> providers = [];
    public bool SupportsNamedProviders => true;

    internal IDisposable Register(NpcEconomyProviderAddress address, INpcEconomyProvider provider, Func<bool> active)
    {
        if (!address.IsValid) throw new ArgumentException("Invalid economy provider address.", nameof(address));
        var key = Key(address);
        var registration = new Registration(address, provider, active);
        lock (sync)
        {
            if (!providers.TryAdd(key, registration)) throw new InvalidOperationException("This economy provider is already registered.");
        }
        return new Lease(() =>
        {
            lock (sync)
                if (providers.TryGetValue(key, out var current) && ReferenceEquals(current, registration)) providers.Remove(key);
        });
    }

    internal IReadOnlyList<NpcEconomyProviderAddress> Snapshot()
    {
        lock (sync) return Array.AsReadOnly(providers.Values.Where(p => p.Active()).Select(p => p.Address)
            .OrderBy(p => p.Plugin, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version).ToArray());
    }

    public ValueTask<NpcEconomyBalanceResult> ReadBalanceAsync(NpcAccountScope scope, string currencyId, CancellationToken cancellationToken = default) =>
        Resolve(scope)?.ReadBalanceAsync(scope with { EconomyProvider = null }, currencyId, cancellationToken) ?? ValueTask.FromResult(new NpcEconomyBalanceResult(NpcEconomyStatus.Unavailable));

    public ValueTask<NpcEconomyBalanceResult> CreditAsync(NpcAccountScope scope, NpcEconomyPrice credit, Guid operationId, CancellationToken cancellationToken = default) =>
        Resolve(scope)?.CreditAsync(scope with { EconomyProvider = null }, credit, operationId, cancellationToken) ?? ValueTask.FromResult(new NpcEconomyBalanceResult(NpcEconomyStatus.Unavailable));

    public ValueTask<NpcEconomyReservationResult> ReserveDebitAsync(NpcAccountScope scope, NpcEconomyPrice price, Guid operationId, CancellationToken cancellationToken = default) =>
        Resolve(scope)?.ReserveDebitAsync(scope with { EconomyProvider = null }, price, operationId, cancellationToken) ?? ValueTask.FromResult(new NpcEconomyReservationResult(NpcEconomyStatus.Unavailable, operationId));

    public ValueTask<bool> FinalizeDebitAsync(NpcAccountScope scope, Guid operationId, bool committed, CancellationToken cancellationToken = default) =>
        Resolve(scope)?.FinalizeDebitAsync(scope with { EconomyProvider = null }, operationId, committed, cancellationToken) ?? ValueTask.FromResult(false);

    private INpcEconomyProvider? Resolve(NpcAccountScope scope)
    {
        if (!scope.IsValid) return null;
        if (scope.EconomyProvider is not { } address) return builtIn;
        lock (sync) return providers.TryGetValue(Key(address), out var active) && active.Active() ? active.Provider : null;
    }

    private static (string, string, int) Key(NpcEconomyProviderAddress address) =>
        (address.Plugin.ToUpperInvariant(), address.Name.ToUpperInvariant(), address.Version);

    private sealed class Lease(Action remove) : IDisposable
    {
        private Action? dispose = remove;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
