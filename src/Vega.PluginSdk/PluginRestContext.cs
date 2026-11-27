using System.Text.Json.Serialization.Metadata;
using Vega.Contracts.Operations;

namespace Vega.PluginSdk;

public sealed record PluginRestReply<T>(PluginRestStatus Status, T? Value = default);

/// <summary>Plugin-owned operations on Vega's authenticated, administrator-only loopback API.</summary>
public interface IPluginRest
{
    bool IsAvailable { get; }
    /// <summary>Publishes GET /v1/plugins/&lt;plugin&gt;/&lt;operation&gt; with no request body.</summary>
    void RegisterQuery<TResponse>(string operation, JsonTypeInfo<TResponse> responseType,
        Func<CancellationToken, ValueTask<PluginRestReply<TResponse>>> handler);

    /// <summary>Publishes POST /v1/plugins/&lt;plugin&gt;/&lt;operation&gt; with a bounded JSON object.</summary>
    void RegisterAction<TRequest, TResponse>(string operation, JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType, Func<TRequest, CancellationToken, ValueTask<PluginRestReply<TResponse>>> handler);
}
