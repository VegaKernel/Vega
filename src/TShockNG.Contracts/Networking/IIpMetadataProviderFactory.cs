namespace TShockNG.Contracts.Networking;

public interface IIpMetadataProviderFactory
{
    IIpMetadataProvider Create(string? countryDatabasePath, string? asnDatabasePath);
}
