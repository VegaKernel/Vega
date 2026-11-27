using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Vega.Contracts.Modules;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    public IPluginServices Services { get; private set; } = null!;
    public IPluginData Data { get; private set; } = null!;
    private void InitializePluginServices(string pluginName, PluginServiceRegistry? registry, IPluginDocumentStore? store)
    {
        Services = new PluginServices(this, pluginName, registry ?? new PluginServiceRegistry());
        Data = new PluginData(this, pluginName, store);
    }

    private sealed class PluginData(HotModuleRegistrationScope owner, string pluginName, IPluginDocumentStore? store) : IPluginData
    {
        private IPluginDocumentStore Store => store ?? throw new NotSupportedException("Plugin database storage is unavailable in this host.");
        public ValueTask<PluginDocument?> ReadAsync(string collection, string key, CancellationToken cancellationToken)
        {
            CheckCollection(collection);
            return owner.RunScopedAsync(token => Store.ReadAsync(pluginName, collection, key, token), cancellationToken);
        }
        public ValueTask<IReadOnlyList<PluginDocument>> ListAsync(string collection, string? afterKey, int limit, CancellationToken cancellationToken)
        {
            CheckCollection(collection);
            return owner.RunScopedAsync(token => Store.ListAsync(pluginName, collection, afterKey, limit, token), cancellationToken);
        }
        public ValueTask<bool> CommitAsync(IReadOnlyList<PluginDocumentMutation> mutations, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(mutations);
            if (mutations.Count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(mutations));
            PluginDocumentMutation[] detached = mutations.ToArray();
            foreach (var mutation in detached)
            {
                ArgumentNullException.ThrowIfNull(mutation);
                CheckCollection(mutation.Collection);
            }
            return owner.RunScopedAsync(token => Store.CommitAsync(pluginName, detached, token), cancellationToken);
        }

        private static void CheckCollection(string collection)
        {
            if (string.Equals(collection, ItemInstanceCollection, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("This collection is reserved for typed custom item ownership.", nameof(collection));
        }
    }

    private sealed class PluginServices(HotModuleRegistrationScope owner, string pluginName, PluginServiceRegistry registry) : IPluginServices
    {
        private const int MaximumPayload = 64 * 1024;

        public IReadOnlyList<PluginServiceAddress> Snapshot() => registry.Snapshot();

        public void Register<TRequest, TResponse>(string name, int version, JsonTypeInfo<TRequest> requestType,
            JsonTypeInfo<TResponse> responseType, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
        {
            ArgumentNullException.ThrowIfNull(requestType);
            ArgumentNullException.ThrowIfNull(responseType);
            ArgumentNullException.ThrowIfNull(handler);
            var address = new PluginServiceAddress(pluginName, name, version);
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(owner.disposed, owner);
                if (!owner.executionGuard.AcceptsCallbacks) throw new InvalidOperationException("Plugin activation is retiring.");
                owner.Track(registry.Register(address, () => owner.executionGuard.AcceptsCallbacks, Invoke));
            }

            async ValueTask<PluginServiceRegistry.Response> Invoke(byte[] payload, CancellationToken callerToken)
            {
                try
                {
                    return await owner.RunScopedAsync(async token =>
                    {
                        // Invalid requests are not provider faults and never execute user code.
                        TRequest? request;
                        try { request = JsonSerializer.Deserialize(payload, requestType); }
                        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
                        { return new PluginServiceRegistry.Response(PluginServiceStatus.Failed); }
                        var result = await owner.executionGuard.InvokeServiceAsync($"service:{name}:v{version}", async ct =>
                        {
                            TResponse response = await handler(request!, ct).ConfigureAwait(false);
                            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, responseType);
                            if (bytes.Length > MaximumPayload) throw new InvalidOperationException("Plugin service response exceeds 64 KiB.");
                            return bytes;
                        }, token).ConfigureAwait(false);
                        return result.Accepted ? new(PluginServiceStatus.Success, result.Value) : new(PluginServiceStatus.Failed);
                    }, callerToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { return new(PluginServiceStatus.Unavailable); }
                catch (PluginScopeBusyException) { return new(PluginServiceStatus.Unavailable); }
                catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return new(PluginServiceStatus.Unavailable); }
            }
        }

        public ValueTask<PluginServiceResult<TResponse>> CallAsync<TRequest, TResponse>(PluginServiceAddress address, TRequest request,
            JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(requestType);
            ArgumentNullException.ThrowIfNull(responseType);
            return owner.RunScopedAsync(async token =>
            {
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, requestType);
                if (payload.Length > MaximumPayload) throw new ArgumentException("Plugin service request exceeds 64 KiB.", nameof(request));
                PluginServiceRegistry.Response response = await registry.InvokeAsync(address, payload, token).ConfigureAwait(false);
                if (response.Status != PluginServiceStatus.Success || response.Payload is null) return new PluginServiceResult<TResponse>(response.Status, default);
                try { return new(PluginServiceStatus.Success, JsonSerializer.Deserialize(response.Payload, responseType)); }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
                { return new(PluginServiceStatus.Failed, default); }
            }, cancellationToken);
        }
    }
}
