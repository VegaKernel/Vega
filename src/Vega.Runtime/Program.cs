using System.Diagnostics;
using System.Reflection;
using Multiplicity.Packets.Views;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Commands.Players;
using Vega.Core.Commands.System;
using Vega.Core.Authorization;
using Vega.Core.Modules;
using Vega.Core.Operations;
using Vega.Core.Updates;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;
using Vega.Cluster.Transport;
using Vega.Core.Events;
using Vega.Core.Extensions;
using Vega.Core.Networking;
using Vega.Modules.CustomItems;
using Vega.Modules.Pvp;
using Vega.Operations;
using Vega.Operations.Actions;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Operations.ReadModels;
using Vega.TerminalUI;
using Vega.Terraria;
using Vega.Terraria.Items;
using Vega.Terraria.Players;
using Vega.Terraria.Operations;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;

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
string[] assemblyDirectories =
[
    Path.Combine(AppContext.BaseDirectory, "libs"),
    Path.Combine(AppContext.BaseDirectory, "ServerPlugins")
];
string[] assemblyExtensions = [".dll", ".exe"];
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += ResolveFromRuntimeDirectories;

return Start();

Assembly? ResolveFromRuntimeDirectories(System.Runtime.Loader.AssemblyLoadContext context, AssemblyName assemblyName)
{
    if (assemblyName.Name is null)
    {
        return null;
    }

    if (assemblyCache.TryGetValue(assemblyName.Name, out var cached))
    {
        return cached;
    }

    foreach (string directory in assemblyDirectories)
    {
        foreach (string extension in assemblyExtensions)
        {
            string path = Path.Combine(directory, assemblyName.Name + extension);
            if (!File.Exists(path))
            {
                continue;
            }

            Assembly assembly = context.LoadFromAssemblyPath(path);
            assemblyCache[assemblyName.Name] = assembly;
            return assembly;
        }
    }

    return null;
}

int Start()
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

    WorldImageOptions worldImageOptions;
    try
    {
        worldImageOptions = WorldImageOptionsParser.Parse(uiOptions.ServerArguments);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    string[] serverArgs = worldImageOptions.ServerArguments.ToArray();

    // Explicit composition root: no assembly scanning or reflection-based handler discovery.
    IEventPipeline<WorldHardmodeStarting> hardmodeStarting = new EventPipeline<WorldHardmodeStarting>(
        Array.Empty<IEventHandler<WorldHardmodeStarting>>(),
        Array.Empty<IEventMonitor<WorldHardmodeStarting>>());

    using var gameThreadDispatcher = new OtapiGameThreadDispatcher();
    using var playerBoundary = new TerrariaPlayerBoundary(gameThreadDispatcher);
    using var playerAuthorization = new PlayerSessionAuthorizationService(playerBoundary);
    using var updateChecker = new GitHubReleaseUpdateChecker();
    using var extensionState = new ExtensionStateStore();
    using var operationsState = new TerrariaOperationsState();
    var pvpModule = new PvpModule(extensionState, playerBoundary, playerBoundary, playerBoundary);
    var customItemsModule = new CustomItemsModule(new TerrariaCustomItemDelivery(gameThreadDispatcher));
    using var moduleHost = new VegaModuleHost([pvpModule, customItemsModule]);
    try
    {
        moduleHost.Start();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Vega module startup failed: {ex}");
        return 1;
    }

    ICommandHandler[] commandHandlers =
    [
        new ServerInfoCommand(new ProcessHostMetricsReader()),
        new TickRateCommand(operationsState),
        new CheckUpdatesCommand(updateChecker),
        new KickCommand(playerBoundary, playerBoundary, playerAuthorization),
        new PlayingCommand(playerBoundary)
    ];
    ICommandDispatcher commandDispatcher = new CommandDispatcher(commandHandlers);
    var playerPacketPolicies = new PacketProcessorPipeline<PacketView>(
    [
        new NpcCoinPickupAuthorityPolicy(),
        new PlayerPacketOwnershipPolicy(),
        pvpModule.PacketProcessor
    ]);
    var lifecycle = new RuntimeLifecycleTracker(processStartedAt);
    var networkMetrics = new NetworkMetricsCounter();
    var logStore = new BoundedOperationsLogStore();
    var consoleSink = new ConsoleOperationsLogSink();
    JsonFileOperationsLogSink? fileSink = TryCreateFileSink();
    var sinks = new List<IOperationsLogSink> { logStore };
    if (uiOptions.EffectiveMode == OperationsUiMode.Plain)
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
        using var worldImages = new TerrariaWorldImageService(logger, lifecycle, worldImageOptions.Enabled);
        var hostMetrics = new ProcessHostMetricsReader();
        var runtimeMetrics = new SampledRuntimeMetricsReader(hostMetrics);
        var modules = new StaticModuleOperationsCatalog(CreateModuleSnapshots(moduleHost));
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
            networkMetrics,
            worldImages);
        var operationsCommands = new OperationsCommandService(commandDispatcher);
        using var startupProgress = new TerrariaStartupProgress();
        using var terminalUi = new TerminalUiHost(
            snapshots,
            operationsCommands,
            logger,
            startupProgress,
            startupProgress);
        startupProgress.Attach(() => terminalUi.IsRunning);
        terminalUi.Started += startupProgress.AcquireConsoleOwnership;
        terminalUi.Stopped += startupProgress.ReleaseConsoleOwnership;
        using var terminalUiReadiness = new TerminalUiReadinessGate(
            lifecycle,
            uiOptions.EffectiveMode,
            terminalUi.TryStart);
        logger.Log(
            OperationsLogLevel.Information,
            "operations",
            $"Operations mode: {uiOptions.EffectiveMode}; bounded log capacity: {logStore.Capacity}.");
        logger.Log(
            OperationsLogLevel.Information,
            "cluster",
            $"Cluster transport: {UnavailableClusterClient.Instance.GetType().Name}; standalone operation remains enabled.");
        logger.Log(
            OperationsLogLevel.Information,
            "world-image",
            $"World image mode: {(worldImageOptions.Enabled ? "on" : "off")}; default remains off pending broader production evidence.");

        return TerrariaServerHost.Run(
            serverArgs,
            lifecycle,
            hardmodeStarting,
            gameThreadDispatcher,
            playerBoundary,
            operationsState,
            networkMetrics,
            logger,
            worldImages,
            playerPacketPolicies,
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

IReadOnlyList<ModuleOperationsSnapshot> CreateModuleSnapshots(VegaModuleHost moduleHost) =>
[
    Module(
        "Vega.Runtime",
        Assembly.GetExecutingAssembly(),
        ["Vega.Cluster", "Vega.Core", "Vega.Modules.Pvp", "Vega.Operations", "Vega.Terraria", "Vega.TerminalUI"]),
    Module("Vega.Core", typeof(CommandDispatcher).Assembly, ["Vega.Contracts"]),
    Module("Vega.Cluster", typeof(UnavailableClusterClient).Assembly, ["Vega.Contracts"]),
    Module("Vega.Operations", typeof(OperationsSnapshotProvider).Assembly, ["Vega.Contracts"]),
    Module("Vega.Terraria", typeof(TerrariaServerHost).Assembly, ["Vega.Contracts", "OTAPI", "TZ.Multiplicity"]),
    Module("Vega.TerminalUI", typeof(TerminalUiHost).Assembly, ["Vega.Contracts", "Terminal.Gui"]),
    .. moduleHost.SnapshotModules()
];

ModuleOperationsSnapshot Module(string name, Assembly assembly, IReadOnlyList<string> dependencies) =>
    new(
        name,
        assembly.GetName().Version?.ToString() ?? "unknown",
        ModuleLoadState.Loaded,
        dependencies,
        TimeSpan.Zero);
