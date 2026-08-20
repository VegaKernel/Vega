using System.Diagnostics;
using System.Reflection;
using TShockNG.Contracts.Authorization;
using TShockNG.Contracts.Commands;
using TShockNG.Core.Commands;
using TShockNG.Core.Commands.Administration;
using TShockNG.Core.Commands.Players;
using TShockNG.Core.Commands.System;
using TShockNG.Core.Operations;
using TShockPluginManager;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Core.Networking;
using Vega.Terraria;
using Vega.Terraria.Authorization;
using Vega.Terraria.Players;
using Vega.Terraria.Threading;
using Vega.Terraria.Updates;

var processStartedAt = Stopwatch.GetTimestamp();

if (File.Exists("TerrariaServer.exe"))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("A TerrariaServer.exe file was found in the current working directory.");
    Console.Error.WriteLine("Vega must run from its own server directory, not from a Terraria client or legacy server installation.");
    Console.ResetColor();
    return 1;
}

var assemblyCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += ResolveFromBin;

return await StartAsync();

Assembly? ResolveFromBin(System.Runtime.Loader.AssemblyLoadContext context, AssemblyName assemblyName)
{
    if (assemblyName.Name is null)
    {
        return null;
    }

    if (assemblyCache.TryGetValue(assemblyName.Name, out var cached))
    {
        return cached;
    }

    var path = Path.Combine(AppContext.BaseDirectory, "bin", assemblyName.Name + ".dll");
    Assembly? assembly = null;

    if (File.Exists(path))
    {
        assembly = context.LoadFromAssemblyPath(path);
    }
    else
    {
        path = Path.ChangeExtension(path, ".exe");
        if (File.Exists(path))
        {
            assembly = context.LoadFromAssemblyPath(path);
        }
    }

    if (assembly is not null)
    {
        assemblyCache[assemblyName.Name] = assembly;
    }

    return assembly;
}

async Task<int> StartAsync()
{
    if (args.Length > 0 && string.Equals(args[0], "plugins", StringComparison.OrdinalIgnoreCase))
    {
        await NugetCLI.Main(args.Skip(1).ToList());
        return 0;
    }

    // Explicit composition root: no assembly scanning or reflection-based handler discovery.
    IEventPipeline<WorldHardmodeStarting> hardmodeStarting = new EventPipeline<WorldHardmodeStarting>(
        Array.Empty<IEventHandler<WorldHardmodeStarting>>(),
        Array.Empty<IEventMonitor<WorldHardmodeStarting>>());

    IPlayerAuthorizationService playerAuthorization = new LegacyTShockPlayerAuthorizationService();
    var updateChecker = new LegacyTShockUpdateChecker();
    using var gameThreadDispatcher = new OtapiGameThreadDispatcher();
    using var playerBoundary = new TerrariaPlayerBoundary(gameThreadDispatcher);

    ICommandHandler[] commandHandlers =
    [
        new ServerInfoCommand(new ProcessHostMetricsReader()),
        new CheckUpdatesCommand(updateChecker),
        new KickCommand(playerBoundary, playerBoundary, playerAuthorization),
        new PlayingCommand(playerBoundary)
    ];
    ICommandDispatcher commandDispatcher = new CommandDispatcher(commandHandlers);
    var playerHpSecurity = new PlayerHpOwnershipPolicy();

    return TerrariaServerHost.Run(
        args,
        processStartedAt,
        hardmodeStarting,
        gameThreadDispatcher,
        playerBoundary,
        playerHpSecurity,
        commandDispatcher,
        playerAuthorization);
}
