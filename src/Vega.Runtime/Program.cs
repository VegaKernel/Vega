using System.Diagnostics;
using System.Reflection;
using TShockNG.Contracts.Authorization;
using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Operations;
using TShockNG.Core.Commands;
using TShockNG.Core.Commands.Administration;
using TShockNG.Core.Commands.Players;
using TShockNG.Core.Commands.System;
using TShockNG.Core.Operations;
using TShockPluginManager;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;
using Vega.Cluster.Transport;
using Vega.Core.Events;
using Vega.Core.Extensions;
using Vega.Core.Networking;
using Vega.Modules.Pvp;
using Vega.Operations;
using Vega.Operations.Actions;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Operations.ReadModels;
using Vega.TerminalUI;
using Vega.Terraria;
using Vega.Terraria.Authorization;
using Vega.Terraria.Players;
using Vega.Terraria.Operations;
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
    OperationsUiOptions uiOptions;
    try
    {
        uiOptions = OperationsUiModeParser.Parse(args);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    string[] serverArgs = uiOptions.ServerArguments.ToArray();
    if (serverArgs.Length > 0 && string.Equals(serverArgs[0], "plugins", StringComparison.OrdinalIgnoreCase))
    {
        await NugetCLI.Main(serverArgs.Skip(1).ToList());
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
    using var extensionState = new ExtensionStateStore();
    using var pvpSessionState = new PvpSessionStateService(extensionState, playerBoundary);
    using var operationsState = new TerrariaOperationsState(gameThreadDispatcher);

    ICommandHandler[] commandHandlers =
    [
        new ServerInfoCommand(new ProcessHostMetricsReader()),
        new CheckUpdatesCommand(updateChecker),
        new KickCommand(playerBoundary, playerBoundary, playerAuthorization),
        new PlayingCommand(playerBoundary)
    ];
    ICommandDispatcher commandDispatcher = new CommandDispatcher(commandHandlers);
    var playerOwnershipSecurity = new PlayerPacketOwnershipPolicy();
    var lifecycle = new RuntimeLifecycleTracker(processStartedAt);
    var networkMetrics = new NetworkMetricsCounter();
    var logStore = new BoundedOperationsLogStore();
    var consoleSink = new ConsoleOperationsLogSink();
    JsonFileOperationsLogSink? fileSink = TryCreateFileSink();
    var sinks = new List<IOperationsLogSink> { logStore };
    if (uiOptions.EffectiveMode != OperationsUiMode.Headless)
    {
        sinks.Add(consoleSink);
    }

    if (fileSink is not null)
    {
        sinks.Add(fileSink);
    }

    using (fileSink)
    {
        var logger = new StructuredOperationsLogger(sinks);
        var hostMetrics = new ProcessHostMetricsReader();
        var runtimeMetrics = new SampledRuntimeMetricsReader(hostMetrics);
        var modules = new StaticModuleOperationsCatalog(CreateModuleSnapshots());
        var snapshots = new OperationsSnapshotProvider(
            "Vega",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            DateTimeOffset.UtcNow,
            lifecycle,
            playerBoundary,
            operationsState,
            modules,
            logStore,
            runtimeMetrics,
            networkMetrics);
        var operationsCommands = new OperationsCommandService(commandDispatcher);
        using var terminalUi = new TerminalUiHost(snapshots, operationsCommands, logger);
        terminalUi.TryStart(uiOptions.EffectiveMode);
        logger.Log(
            OperationsLogLevel.Information,
            "operations",
            $"Operations mode: {uiOptions.EffectiveMode}; bounded log capacity: {logStore.Capacity}.");
        logger.Log(
            OperationsLogLevel.Information,
            "cluster",
            $"Cluster transport: {UnavailableClusterClient.Instance.GetType().Name}; standalone operation remains enabled.");

        return TerrariaServerHost.Run(
            serverArgs,
            lifecycle,
            hardmodeStarting,
            gameThreadDispatcher,
            playerBoundary,
            operationsState,
            networkMetrics,
            logger,
            playerOwnershipSecurity,
            commandDispatcher,
            playerAuthorization);
    }
}

JsonFileOperationsLogSink? TryCreateFileSink()
{
    try
    {
        return new JsonFileOperationsLogSink(Path.Combine("logs", "vega-operations.jsonl"));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Vega file logging is unavailable; startup continues: {ex.Message}");
        return null;
    }
}

IReadOnlyList<ModuleOperationsSnapshot> CreateModuleSnapshots() =>
[
    Module(
        "Vega.Runtime",
        Assembly.GetExecutingAssembly(),
        ["Vega.Cluster", "Vega.Core", "Vega.Modules.Pvp", "Vega.Operations", "Vega.Terraria", "Vega.TerminalUI"]),
    Module("Vega.Core", typeof(CommandDispatcher).Assembly, ["Vega.Contracts"]),
    Module("Vega.Cluster", typeof(UnavailableClusterClient).Assembly, ["Vega.Contracts"]),
    Module("Vega.Modules.Pvp", typeof(PvpSessionStateService).Assembly, ["Vega.Contracts"]),
    Module("Vega.Operations", typeof(OperationsSnapshotProvider).Assembly, ["Vega.Contracts"]),
    Module("Vega.Terraria", typeof(TerrariaServerHost).Assembly, ["Vega.Contracts", "OTAPI", "TZ.Multiplicity"]),
    Module("Vega.TerminalUI", typeof(TerminalUiHost).Assembly, ["Vega.Contracts", "Terminal.Gui"]),
    new ModuleOperationsSnapshot(
        "Legacy.TShockAPI",
        "compatibility",
        ModuleLoadState.Registered,
        ["Legacy.TSAPI"],
        TimeSpan.Zero)
];

ModuleOperationsSnapshot Module(string name, Assembly assembly, IReadOnlyList<string> dependencies) =>
    new(
        name,
        assembly.GetName().Version?.ToString() ?? "unknown",
        ModuleLoadState.Loaded,
        dependencies,
        TimeSpan.Zero);
