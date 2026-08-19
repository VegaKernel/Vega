namespace TShockNG.Contracts.Networking;

/// <summary>
/// Creates IP metadata providers for a concrete runtime configuration.
/// </summary>
public interface IIpMetadataProviderFactory
{
    IIpMetadataProvider Create(string? countryDatabasePath, string? asnDatabasePath);
}
