using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Vega.Contracts.Operations;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    public IPluginRest Rest { get; private set; } = null!;
    private void InitializePluginRest(string pluginName, PluginRestRegistry? registry) => Rest = new PluginRest(this, pluginName, registry);

    private sealed class PluginRest(HotModuleRegistrationScope owner, string pluginName, PluginRestRegistry? registry) : IPluginRest
    {
        public bool IsAvailable => registry is not null;
        public void RegisterQuery<TResponse>(string operation, JsonTypeInfo<TResponse> responseType,
            Func<CancellationToken, ValueTask<PluginRestReply<TResponse>>> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(responseType);
            Register(operation, PluginRestMethod.Query, (_, token) => owner.InvokeRestAsync(operation, handler, responseType, token));
        }

        public void RegisterAction<TRequest, TResponse>(string operation, JsonTypeInfo<TRequest> requestType,
            JsonTypeInfo<TResponse> responseType, Func<TRequest, CancellationToken, ValueTask<PluginRestReply<TResponse>>> handler)
        {
            ArgumentNullException.ThrowIfNull(requestType);
            ArgumentNullException.ThrowIfNull(responseType);
            ArgumentNullException.ThrowIfNull(handler);
            Register(operation, PluginRestMethod.Action, (json, cancellationToken) => owner.RunRestOperationAsync(operation, async token =>
            {
                TRequest? request;
                try { request = JsonSerializer.Deserialize(json, requestType); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                { return new PluginRestResponse(PluginRestStatus.BadRequest); }
                if (request is null) return new PluginRestResponse(PluginRestStatus.BadRequest);
                return await owner.GuardRestReplyAsync(operation, ct => handler(request, ct), responseType, token).ConfigureAwait(false);
            }, cancellationToken));
        }

        private void Register(string operation, PluginRestMethod method, Func<byte[], CancellationToken, ValueTask<PluginRestResponse>> invoke)
        {
            if (registry is null) throw new NotSupportedException("Plugin REST operations are unavailable in this host.");
            owner.RegisterOwned(() => registry.Register(pluginName, operation, method, () => owner.executionGuard.AcceptsCallbacks, invoke));
        }
    }

    private ValueTask<PluginRestResponse> InvokeRestAsync<T>(string operation, Func<CancellationToken, ValueTask<PluginRestReply<T>>> handler,
        JsonTypeInfo<T> type, CancellationToken token) => RunRestOperationAsync(operation, ct => GuardRestReplyAsync(operation, handler, type, ct), token);

    private async ValueTask<PluginRestResponse> GuardRestReplyAsync<T>(string operation, Func<CancellationToken, ValueTask<PluginRestReply<T>>> handler,
        JsonTypeInfo<T> type, CancellationToken token)
    {
        bool entered = false;
        var result = await executionGuard.InvokeServiceAsync($"rest:{operation}", async ct =>
        {
            entered = true;
            PluginRestReply<T> reply = await handler(ct).ConfigureAwait(false);
            if (reply is null || !Enum.IsDefined(reply.Status)) throw new InvalidOperationException("Invalid plugin REST reply status.");
            if (reply.Status != PluginRestStatus.Success) return new PluginRestResponse(reply.Status);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(reply.Value!, type);
            if (payload.Length > PluginRestRegistry.MaximumResponseBytes) throw new InvalidOperationException("Plugin REST response exceeds 64 KiB.");
            return new PluginRestResponse(PluginRestStatus.Success, payload);
        }, token).ConfigureAwait(false);
        return result.Accepted ? result.Value! : new(entered ? PluginRestStatus.Failed : PluginRestStatus.Unavailable);
    }

    private async ValueTask<PluginRestResponse> RunRestOperationAsync(string operation, Func<CancellationToken, ValueTask<PluginRestResponse>> callback,
        CancellationToken callerToken)
    {
        try { return await RunScopedAsync(callback, callerToken).ConfigureAwait(false); }
        catch (PluginScopeBusyException) { return new(PluginRestStatus.Unavailable); }
        catch (ObjectDisposedException) { return new(PluginRestStatus.Unavailable); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return new(PluginRestStatus.Unavailable); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            executionGuard.Record($"rest:{operation}", ex);
            return new(PluginRestStatus.Failed);
        }
    }
}
