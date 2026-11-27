namespace Vega.Contracts.Modules;

/// <summary>Host-side provider-neutral document storage. The SDK binds owner identity, never plugin input.</summary>
public interface IPluginDocumentStore
{
    ValueTask<PluginDocument?> ReadAsync(string owner, string collection, string key, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PluginDocument>> ListAsync(string owner, string collection, string? afterKey, int limit, CancellationToken cancellationToken = default);
    ValueTask<bool> CommitAsync(string owner, IReadOnlyList<PluginDocumentMutation> mutations, CancellationToken cancellationToken = default);
}

public sealed record PluginDocument(string Key, long Revision, string Json);

/// <summary>ExpectedRevision zero creates a missing document; positive versions perform compare-and-swap. Null JSON deletes.</summary>
public sealed record PluginDocumentMutation(string Collection, string Key, long ExpectedRevision, string? Json);
