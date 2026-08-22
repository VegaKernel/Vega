namespace Vega.Contracts.Accounts;

public sealed record AccountSnapshot(
    AccountId Id,
    string Name,
    string GroupName,
    DateTimeOffset? LastSeenUtc)
{
    public string? Culture { get; init; }

    public long Revision { get; init; }
}
