namespace Vega.Contracts.Bans;

public sealed record BanSnapshot(
    string Identifier,
    string Reason,
    string? BanningUser,
    DateTimeOffset? ExpiresUtc);
