using TShockNG.Contracts.Networking;

namespace TShockNG.Core.GeoIP;

public sealed class MmdbIpMetadataProviderFactory : IIpMetadataProviderFactory
{
    public IIpMetadataProvider Create(string? countryDatabasePath, string? asnDatabasePath) =>
        new MmdbIpMetadataProvider(countryDatabasePath, asnDatabasePath);
}
