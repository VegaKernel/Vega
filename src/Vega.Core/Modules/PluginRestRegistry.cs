using Vega.Contracts.Operations;

namespace Vega.Core.Modules;

/// <summary>Owns routable operator operations for the current plugin activations.</summary>
public sealed class PluginRestRegistry : IPluginRestRouter
{
    public const int MaximumResponseBytes = 64 * 1024;
    public const int MaximumRequestBytes = 4 * 1024;
    private sealed record Endpoint(Func<bool> Active, Func<byte[], CancellationToken, ValueTask<PluginRestResponse>> Invoke);
    private readonly object sync = new();
    private readonly Dictionary<(string Owner, string Operation, PluginRestMethod Method), Endpoint> endpoints = [];

    internal IDisposable Register(string plugin, string operation, PluginRestMethod method, Func<bool> active,
        Func<byte[], CancellationToken, ValueTask<PluginRestResponse>> invoke)
    {
        if (!ValidSegment(plugin) || !ValidSegment(operation) || !Enum.IsDefined(method)) throw new ArgumentException("Invalid plugin REST route.");
        var key = Key(plugin, operation, method);
        var endpoint = new Endpoint(active, invoke);
        lock (sync)
            if (!endpoints.TryAdd(key, endpoint)) throw new InvalidOperationException("Plugin REST operation already registered.");
        return new Lease(() =>
        {
            lock (sync)
                if (endpoints.TryGetValue(key, out var current) && ReferenceEquals(current, endpoint)) endpoints.Remove(key);
        });
    }

    public ValueTask<PluginRestResponse> DispatchAsync(string plugin, string operation, PluginRestMethod method,
        byte[] requestJson, CancellationToken cancellationToken = default)
    {
        if (!ValidSegment(plugin) || !ValidSegment(operation) || !Enum.IsDefined(method)) return Reply(PluginRestStatus.NotFound);
        ArgumentNullException.ThrowIfNull(requestJson);
        if (requestJson.Length > MaximumRequestBytes || (method == PluginRestMethod.Query && requestJson.Length != 0) ||
            (method == PluginRestMethod.Action && requestJson.Length == 0)) return Reply(PluginRestStatus.BadRequest);
        Endpoint? endpoint;
        lock (sync) endpoints.TryGetValue(Key(plugin, operation, method), out endpoint);
        if (endpoint is null) return Reply(PluginRestStatus.NotFound);
        return endpoint.Active() ? endpoint.Invoke(requestJson, cancellationToken) : Reply(PluginRestStatus.Unavailable);
    }

    private static bool ValidSegment(string value) => !string.IsNullOrEmpty(value) && value.Length <= 96 && value is not ("." or "..") &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static (string, string, PluginRestMethod) Key(string plugin, string operation, PluginRestMethod method) =>
        (plugin.ToUpperInvariant(), operation.ToUpperInvariant(), method);

    private static ValueTask<PluginRestResponse> Reply(PluginRestStatus status) => ValueTask.FromResult(new PluginRestResponse(status));

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? cleanup = release;
        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
