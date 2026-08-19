namespace TShockNG.Contracts.Networking;

public interface IIpMetadataProvider
{
    bool IsAvailable { get; }

    bool TryLookup(string ipAddress, out IpMetadata metadata);
}
