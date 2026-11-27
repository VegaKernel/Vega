using System.Text.Json.Serialization.Metadata;

namespace Vega.PluginSdk;

/// <summary>A service contract has an owner, name and explicit wire-schema version.</summary>
public sealed record PluginServiceAddress(string Provider, string Name, int Version);

public enum PluginServiceStatus { Success, Unavailable, Failed }

public readonly record struct PluginServiceResult<T>(PluginServiceStatus Status, T? Value)
{
    public bool Succeeded => Status == PluginServiceStatus.Success;
}

/// <summary>Versioned request/reply services, isolated from collectible plugin assembly identities.</summary>
public interface IPluginServices
{
    IReadOnlyList<PluginServiceAddress> Snapshot();

    /// <summary>Publishes an endpoint for this activation. The host removes it on retirement.</summary>
    void Register<TRequest, TResponse>(string name, int version, JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler);

    /// <summary>Resolve at call time; no reference to another plugin instance survives reload.</summary>
    ValueTask<PluginServiceResult<TResponse>> CallAsync<TRequest, TResponse>(PluginServiceAddress address, TRequest request,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken = default);
}
