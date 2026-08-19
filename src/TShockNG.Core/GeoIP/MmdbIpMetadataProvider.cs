#nullable enable

using System;
using System.IO;
using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using TShockNG.Contracts.Networking;

namespace TShockNG.Core.GeoIP;

public sealed class MmdbIpMetadataProvider : IIpMetadataProvider, IDisposable
{
    private readonly DatabaseReader? countryReader;
    private readonly DatabaseReader? asnReader;

    public bool IsAvailable => countryReader is not null || asnReader is not null;

    public MmdbIpMetadataProvider(string? countryDatabasePath, string? asnDatabasePath)
    {
        DatabaseReader? country = null;
        try
        {
            country = OpenIfPresent(countryDatabasePath, "-Country");
            countryReader = country;
            asnReader = OpenIfPresent(asnDatabasePath, "-ASN");
        }
        catch
        {
            country?.Dispose();
            throw;
        }
    }

    public bool TryLookup(string ipAddress, out IpMetadata metadata)
    {
        metadata = default!;

        if (!IPAddress.TryParse(ipAddress, out var address))
            return false;

        string? country = null;
        string? countryCode = null;
        string? continent = null;
        long? asn = null;
        string? organization = null;
        var found = false;

        if (countryReader is not null && countryReader.TryCountry(address, out var countryResponse))
        {
            country = countryResponse.Country.Name;
            countryCode = countryResponse.Country.IsoCode;
            continent = countryResponse.Continent.Name ?? countryResponse.Continent.Code;
            found = true;
        }

        if (asnReader is not null && asnReader.TryAsn(address, out var asnResponse))
        {
            asn = asnResponse.AutonomousSystemNumber;
            organization = asnResponse.AutonomousSystemOrganization;
            found = true;
        }

        if (!found)
            return false;

        metadata = new IpMetadata(country, countryCode, continent, asn, organization);
        return true;
    }

    public void Dispose()
    {
        countryReader?.Dispose();
        asnReader?.Dispose();
    }

    private static DatabaseReader? OpenIfPresent(string? path, string expectedDatabaseSuffix)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var reader = new DatabaseReader(path, FileAccessMode.Memory);
        if (reader.Metadata.DatabaseType.EndsWith(expectedDatabaseSuffix, StringComparison.Ordinal))
            return reader;

        var databaseType = reader.Metadata.DatabaseType;
        reader.Dispose();
        throw new InvalidDataException(
            $"MMDB database '{path}' has type '{databaseType}', expected a database ending in '{expectedDatabaseSuffix}'.");
    }
}
