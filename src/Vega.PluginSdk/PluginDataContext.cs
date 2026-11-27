using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Vega.Contracts.Modules;

namespace Vega.PluginSdk;

public sealed record PluginDataRecord<T>(string Key, long Revision, T? Value);

/// <summary>Durable transactional documents scoped to this plugin in the configured Vega database.</summary>
public interface IPluginData
{
    ValueTask<PluginDocument?> ReadAsync(string collection, string key, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PluginDocument>> ListAsync(string collection, string? afterKey = null, int limit = 100, CancellationToken cancellationToken = default);
    /// <summary>Atomically commits up to 32 mutations. False means at least one expected revision no longer matches.</summary>
    ValueTask<bool> CommitAsync(IReadOnlyList<PluginDocumentMutation> mutations, CancellationToken cancellationToken = default);
}

public static class PluginDataExtensions
{
    public static async ValueTask<PluginDataRecord<T>?> ReadAsync<T>(this IPluginData data, string collection, string key,
        JsonTypeInfo<T> type, CancellationToken cancellationToken = default)
    {
        PluginDocument? document = await data.ReadAsync(collection, key, cancellationToken).ConfigureAwait(false);
        return document is null ? null : new(document.Key, document.Revision, JsonSerializer.Deserialize(document.Json, type));
    }

    public static PluginDocumentMutation Set<T>(string collection, string key, long expectedRevision, T value, JsonTypeInfo<T> type) =>
        new(collection, key, expectedRevision, JsonSerializer.Serialize(value, type));

    public static PluginDocumentMutation Delete(string collection, string key, long expectedRevision) =>
        new(collection, key, expectedRevision, null);
}
