namespace Vega.Contracts.Moderation;

public enum MuteIdentityKind
{
    PlayerName,
    Account,
    ClientUuid,
    IpAddress
}

public sealed record MuteSubject(MuteIdentityKind Kind, string Value);

public sealed record MuteIdentity(
    string PlayerName,
    string? AccountName = null,
    string? ClientUuid = null,
    string? IpAddress = null);
