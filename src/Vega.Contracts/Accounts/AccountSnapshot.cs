namespace TShockNG.Contracts.Accounts;

public sealed record AccountSnapshot(
    AccountId Id,
    string Name,
    string GroupName,
    DateTimeOffset? LastSeenUtc);
