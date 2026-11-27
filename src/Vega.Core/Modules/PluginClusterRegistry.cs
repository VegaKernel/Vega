using Vega.Contracts.Cluster;

namespace Vega.Core.Modules;

/// <summary>Host-only registration for plugin event topics. Never keeps CLR payload types in the transport.</summary>
public sealed class PluginClusterRegistry : IClusterEventHandler<PluginClusterNotification>
{
    private sealed record Endpoint(Func<bool> Active, Func<ClusterEnvelope<PluginClusterNotification>, CancellationToken, ValueTask<bool>> Invoke);
    private readonly object sync = new();
    private readonly Dictionary<(string Plugin, string Topic, int Version), Endpoint> endpoints = [];

    internal IDisposable Register(string plugin, string topic, int version, Func<bool> active,
        Func<ClusterEnvelope<PluginClusterNotification>, CancellationToken, ValueTask<bool>> invoke)
    {
        var key = Key(plugin, topic, version);
        var endpoint = new Endpoint(active, invoke);
        lock (sync)
            if (!endpoints.TryAdd(key, endpoint)) throw new InvalidOperationException("Plugin cluster topic already registered.");
        return new Lease(() =>
        {
            lock (sync)
                if (endpoints.TryGetValue(key, out var current) && ReferenceEquals(current, endpoint)) endpoints.Remove(key);
        });
    }

    public async ValueTask HandleAsync(ClusterEnvelope<PluginClusterNotification> envelope, CancellationToken cancellationToken)
    {
        if (!envelope.Message.IsValid) throw new ArgumentException("Invalid plugin cluster notification.");
        var key = Key(envelope.Message.Plugin, envelope.Message.Topic, envelope.Message.Version);
        Endpoint? endpoint;
        lock (sync) endpoints.TryGetValue(key, out endpoint);
        if (endpoint is null || !endpoint.Active() || !await endpoint.Invoke(envelope, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Plugin cluster topic is unavailable or handler rejected the notification.");
    }

    internal static (string Plugin, string Topic, int Version) Key(string plugin, string topic, int version)
    {
        if (!PluginClusterNotification.ValidPluginName(plugin) || !PluginClusterNotification.ValidName(topic, 96) || version <= 0)
            throw new ArgumentException("Invalid plugin cluster address.");
        return (plugin.ToUpperInvariant(), topic.ToUpperInvariant(), version);
    }

    private sealed class Lease(Action cleanup) : IDisposable
    {
        private Action? release = cleanup;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
