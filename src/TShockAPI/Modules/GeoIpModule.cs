using System;
using System.IO;
using MaxMind;

namespace TShockAPI.Modules;

public sealed class GeoIpModule : Module
{
    public override void Initialise()
    {
        if (!TShock.Config.Settings.EnableGeoIP)
            return;

        var countryDatabasePath = Path.Combine(TShock.SavePath, "GeoLite2-Country.mmdb");
        var asnDatabasePath = Path.Combine(TShock.SavePath, "GeoLite2-ASN.mmdb");

        try
        {
            var provider = new GeoIPCountry(countryDatabasePath, asnDatabasePath);
            if (!provider.IsAvailable)
            {
                TShock.Geo?.Dispose();
                TShock.Geo = null;
                provider.Dispose();
                TShock.Log.ConsoleWarn(GetString(
                    "GeoIP is enabled, but no MMDB database was found. Expected {0} and/or {1}.",
                    countryDatabasePath,
                    asnDatabasePath));
                return;
            }

            TShock.Geo?.Dispose();
            TShock.Geo = provider;
        }
        catch (Exception ex)
        {
            TShock.Geo?.Dispose();
            TShock.Geo = null;
            TShock.Log.ConsoleWarn(GetString("GeoIP MMDB initialization failed: {0}", ex.Message));
        }
    }

    public override void Dispose()
    {
        // TShock disposes Geo before ModuleManager, so only clear the shared reference here.
        TShock.Geo = null;
    }
}
