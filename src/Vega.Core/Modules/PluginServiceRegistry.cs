using Vega.PluginSdk;

namespace Vega.Core.Modules;

/// <summary>Host-owned wire endpoints. No consumer receives a delegate or object from another load context.</summary>
internal sealed class PluginServiceRegistry
{
    internal readonly record struct Response(PluginServiceStatus Status, byte[]? Payload = null);
    private sealed record Endpoint(PluginServiceAddress Address, Func<bool> IsActive,
        Func<byte[], CancellationToken, ValueTask<Response>> Invoke);
    private readonly object sync = new();
    private readonly Dictionary<(string Provider, string Name, int Version), Endpoint> endpoints = [];

    public IDisposable Register(PluginServiceAddress address, Func<bool> isActive, Func<byte[], CancellationToken, ValueTask<Response>> invoke)
    {
        var key = Key(address);
        var endpoint = new Endpoint(address, isActive, invoke);
        lock (sync)
        {
            if (!endpoints.TryAdd(key, endpoint)) throw new InvalidOperationException($"Plugin service {address.Provider}/{address.Name} v{address.Version} is already registered.");
        }
        return new Registration(() =>
        {
            lock (sync)
            {
                if (endpoints.TryGetValue(key, out Endpoint? current) && ReferenceEquals(current, endpoint)) endpoints.Remove(key);
            }
        });
    }

    public IReadOnlyList<PluginServiceAddress> Snapshot()
    {
        lock (sync) return Array.AsReadOnly(endpoints.Values.Where(e => e.IsActive()).Select(e => e.Address)
            .OrderBy(e => e.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Version).ToArray());
    }

    public ValueTask<Response> InvokeAsync(PluginServiceAddress address, byte[] request, CancellationToken token)
    {
        var key = Key(address);
        Endpoint? endpoint;
        lock (sync) endpoints.TryGetValue(key, out endpoint);
        return endpoint is not null && endpoint.IsActive() ? endpoint.Invoke(request, token) : ValueTask.FromResult(new Response(PluginServiceStatus.Unavailable));
    }

    private static (string Provider, string Name, int Version) Key(PluginServiceAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(address.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(address.Name);
        if (address.Provider.Length > 255 || address.Name.Length > 96 || address.Version < 1 ||
            address.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Invalid plugin service address.", nameof(address));
        return (address.Provider.ToUpperInvariant(), address.Name.ToUpperInvariant(), address.Version);
    }

    private sealed class Registration(Action release) : IDisposable
    {
        private Action? cleanup = release;
        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
