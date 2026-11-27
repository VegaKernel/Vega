using Vega.Contracts.Accounts;
using Vega.Contracts.Items;
using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>An account and its immutable registration generation, not a player slot.</summary>
public readonly record struct CustomItemOwner(AccountId AccountId, DateTimeOffset RegisteredUtc);

/// <summary>Durable server-owned virtual item. It does not identify or create a physical Terraria item.</summary>
public sealed record CustomItemInstance(
    Guid Id,
    CustomItemOwner Owner,
    int DefinitionVersion,
    CustomItemRecipe Definition,
    DateTimeOffset IssuedUtc,
    DateTimeOffset UpdatedUtc,
    bool Revoked = false)
{
    [System.Text.Json.Serialization.JsonRequired]
    public int FormatVersion { get; init; } = 1;
}

public sealed record CustomItemInstanceSnapshot(CustomItemInstance Instance, long Revision);

public enum CustomItemInstanceStatus
{
    Applied,
    AlreadyIssued,
    NotAuthenticated,
    DefinitionNotFound,
    Conflict,
    NotFound,
    NotOwner,
    Revoked,
    Unavailable
}

public readonly record struct CustomItemInstanceResult(CustomItemInstanceStatus Status, CustomItemInstanceSnapshot? Snapshot = null)
{
    public bool Succeeded => Status is CustomItemInstanceStatus.Applied or CustomItemInstanceStatus.AlreadyIssued;
}

/// <summary>
/// A plugin-scoped persistent ownership ledger. No native inventory/SSC materialization, stat override,
/// or item duplication is performed by these operations. Mutations use compare-and-swap revisions.
/// </summary>
public interface IPluginCustomItemInstances
{
    bool IsAvailable { get; }

    /// <summary>Issue one virtual instance to an exact authenticated account. Reuse the same nonempty ID when retrying an uncertain result.</summary>
    ValueTask<CustomItemInstanceResult> IssueAsync(PlayerSnapshot owner, Guid id, string recipeName, int definitionVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Read one record owned by this plugin, including its permanent revocation marker.</summary>
    ValueTask<CustomItemInstanceSnapshot?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Read an active record only when the exact authenticated player still owns it.</summary>
    ValueTask<CustomItemInstanceSnapshot?> FindOwnedAsync(PlayerSnapshot owner, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Bounded plugin-wide pagination in ascending canonical instance-ID order, including revoked records.</summary>
    ValueTask<IReadOnlyList<CustomItemInstanceSnapshot>> ListAsync(Guid? afterId = null, int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically change one virtual instance's owner, conditional on its revision and both exact authenticated sessions.</summary>
    ValueTask<CustomItemInstanceResult> TransferAsync(Guid id, long expectedRevision, PlayerSnapshot from, PlayerSnapshot to,
        CancellationToken cancellationToken = default);

    /// <summary>Retire a virtual instance by revision. Its ID can never be issued again through the typed API.</summary>
    ValueTask<CustomItemInstanceResult> RevokeAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
}
