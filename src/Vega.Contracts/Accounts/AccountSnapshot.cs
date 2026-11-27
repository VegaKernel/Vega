namespace Vega.Contracts.Accounts;

public sealed record AccountSnapshot(
    AccountId Id,
    string Name,
    string GroupName,
    DateTimeOffset? LastSeenUtc)
{
    public string? Culture { get; init; }

    public long Revision { get; init; }
    public DateTimeOffset? RegisteredUtc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string? LastKnownIp { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string? LastKnownUuid { get; init; }
}
