namespace Vega.Contracts.Networking;

public interface IIpMetadataProvider
{
    bool TryLookup(string ipAddress, out IpMetadata metadata);
}
