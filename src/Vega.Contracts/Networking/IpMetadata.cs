namespace Vega.Contracts.Networking;

public sealed record IpMetadata(
    string? Country,
    string? CountryCode,
    string? Continent,
    long? Asn,
    string? Organization);
