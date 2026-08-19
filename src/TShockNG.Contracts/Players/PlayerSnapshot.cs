namespace TShockNG.Contracts.Players;

public sealed record PlayerSnapshot(
    PlayerId Id,
    string Name,
    string IpAddress,
    bool IsLoggedIn,
    string? AccountName,
    string? GroupName);
