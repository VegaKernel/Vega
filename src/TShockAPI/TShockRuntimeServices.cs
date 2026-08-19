#nullable enable

using System;
using TShockNG.Contracts.Hosting;
using TShockNG.Contracts.Networking;

namespace TShockAPI;

/// <summary>
/// Composition boundary populated by the launcher before TSAPI starts loading plugins.
/// </summary>
public static class TShockRuntimeServices
{
    public static IHttpServerFactory? HttpServerFactory { get; private set; }
    public static IIpMetadataProviderFactory? IpMetadataProviderFactory { get; private set; }

    public static void Configure(
        IHttpServerFactory httpServerFactory,
        IIpMetadataProviderFactory ipMetadataProviderFactory)
    {
        ArgumentNullException.ThrowIfNull(httpServerFactory);
        ArgumentNullException.ThrowIfNull(ipMetadataProviderFactory);

        HttpServerFactory = httpServerFactory;
        IpMetadataProviderFactory = ipMetadataProviderFactory;
    }

    internal static IHttpServerFactory RequireHttpServerFactory() =>
        HttpServerFactory ?? throw new InvalidOperationException(
            "TShock runtime services were not configured by the launcher.");
}
