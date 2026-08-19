#nullable enable

using System;
using TShockNG.Contracts.Runtime;

namespace TShockAPI.Integration;

/// <summary>
/// One-way composition handoff from TShockLauncher into the TSAPI-created TShock integration layer.
/// This is not a general-purpose service locator for domain code.
/// </summary>
public static class TShockRuntimeServices
{
    public static ServerServices? Current { get; private set; }

    public static void Configure(ServerServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Current = services;
    }

    public static ServerServices Require() =>
        Current ?? throw new InvalidOperationException("TShock runtime services have not been configured by the launcher.");
}
