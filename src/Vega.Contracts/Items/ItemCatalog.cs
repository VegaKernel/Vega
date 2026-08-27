namespace Vega.Contracts.Items;

public sealed record ItemDefinition(
    int Type,
    string Name,
    int MaxStack,
    bool OnlyNeedOneInInventory = false,
    bool IsAccessory = false);

public sealed record ItemPrefixDefinition(
    byte Id,
    string Name);

public enum ItemResolutionStatus
{
    Found,
    NotFound,
    Ambiguous
}

public sealed record ItemResolutionResult(
    ItemResolutionStatus Status,
    ItemDefinition? Item = null,
    IReadOnlyList<ItemDefinition>? Matches = null)
{
    public bool IsFound => Status == ItemResolutionStatus.Found && Item is not null;

    public IReadOnlyList<ItemDefinition> Candidates => Matches ?? [];
}

public sealed record ItemPrefixResolutionResult(
    ItemResolutionStatus Status,
    ItemPrefixDefinition? Prefix = null,
    IReadOnlyList<ItemPrefixDefinition>? Matches = null)
{
    public bool IsFound => Status == ItemResolutionStatus.Found && Prefix is not null;

    public IReadOnlyList<ItemPrefixDefinition> Candidates => Matches ?? [];
}

/// <summary>
/// Runtime item catalog used by commands and tooling. Implementations own engine-specific naming and ID lookup.
/// </summary>
public interface IItemCatalog
{
    ItemResolutionResult Resolve(string query);

    ItemPrefixResolutionResult ResolvePrefix(string query, ItemDefinition item);
}
