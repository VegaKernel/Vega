#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Net;
using TShockNG.Contracts.Networking;
using TShockAPI;

namespace MaxMind;

/// <summary>
/// Transitional TShock-facing adapter over the IP metadata contract.
/// </summary>
public sealed class GeoIPCountry : IIpMetadataProvider, IDisposable
{
    private readonly IIpMetadataProvider provider;

    public bool IsAvailable => provider.IsAvailable;

    public GeoIPCountry(string unusedPath)
        : this(
            Path.Combine(TShock.SavePath, "GeoLite2-Country.mmdb"),
            Path.Combine(TShock.SavePath, "GeoLite2-ASN.mmdb"))
    {
    }

    public GeoIPCountry(string? countryDatabasePath, string? asnDatabasePath)
    {
        var factory = TShockRuntimeServices.IpMetadataProviderFactory ??
            throw new InvalidOperationException("The IP metadata provider was not configured by the launcher.");
        provider = factory.Create(countryDatabasePath, asnDatabasePath);
    }

    public bool TryLookup(string ipAddress, out IpMetadata metadata) =>
        provider.TryLookup(ipAddress, out metadata);

    public string? TryGetCountryCode(IPAddress ipAddress) =>
        TryLookup(ipAddress.ToString(), out var metadata) ? metadata.CountryCode : null;

    public static string? GetCountryNameByCode(string countryCode)
    {
        try
        {
            return new RegionInfo(countryCode).EnglishName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose() => (provider as IDisposable)?.Dispose();
}
