using System.Diagnostics;
using System.Reflection;
using Multiplicity.Packets.Views;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Localization;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Commands.Players;
using Vega.Core.Commands.System;
using Vega.Core.Authorization;
using Vega.Core.Bans;
using Vega.Core.Configuration;
using Vega.Core.Localization;
using Vega.Core.Modules;
using Vega.Core.Operations;
using Vega.Core.Updates;
using Vega.Core.Worlds;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;
using Vega.Cluster.Transport;
using Vega.Core.Events;
using Vega.Core.Extensions;
using Vega.Core.Networking;
using Vega.Modules.AutoWorldCleaner;
using Vega.Modules.CustomItems;
using Vega.Modules.Pvp;
using Vega.Operations;
using Vega.Operations.Actions;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Operations.ReadModels;
using Vega.Persistence;
using Vega.TerminalUI;
using Vega.Terraria;
using Vega.Terraria.Items;
using Vega.Terraria.Networking;
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
    VegaServerConfigurationState serverConfiguration;
    try
    {
        serverConfiguration = new VegaServerConfigurationState(
            Path.Combine(AppContext.BaseDirectory, "config", "server.json"),
            args);
    }
    catch (Exception ex) when (
        ex is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    OperationsUiOptions uiOptions;
    try
    {
        uiOptions = OperationsUiModeParser.Parse(serverConfiguration.Current.Arguments);
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
    string configuredCulture = serverConfiguration.Current.Values["defaultCulture"].DisplayValue;
    var localization = new LocalizationService(
        configuredCulture.StartsWith('<') ? "en-US" : configuredCulture);
    using IDisposable coreRussianCatalog = RegisterCoreRussianCatalog(localization);

    // Explicit composition root: no assembly scanning or reflection-based handler discovery.
    IEventPipeline<WorldHardmodeStarting> hardmodeStarting = new EventPipeline<WorldHardmodeStarting>(
        Array.Empty<IEventHandler<WorldHardmodeStarting>>(),
        Array.Empty<IEventMonitor<WorldHardmodeStarting>>());

    using var gameThreadDispatcher = new OtapiGameThreadDispatcher();
    var connectionMetadata = new TerrariaConnectionMetadataRegistry();
    using var playerBoundary = new TerrariaPlayerBoundary(gameThreadDispatcher, connectionMetadata);
    using var playerAuthorization = new PlayerSessionAuthorizationService(playerBoundary);
    using var updateChecker = new GitHubReleaseUpdateChecker();
    using var extensionState = new ExtensionStateStore();
    using var operationsState = new TerrariaOperationsState();
    var pvpModule = new PvpModule(extensionState, playerBoundary, playerBoundary, playerBoundary);
    var customItemsModule = new CustomItemsModule(new TerrariaCustomItemDelivery(gameThreadDispatcher));
    ICommandHandler[] commandHandlers =
    [
        new ServerInfoCommand(new ProcessHostMetricsReader()),
        new TickRateCommand(operationsState),
        new CheckUpdatesCommand(updateChecker),
        new KickCommand(playerBoundary, playerBoundary, playerAuthorization),
        new PlayingCommand(playerBoundary),
        new LanguageCommand(localization)
    ];
    var commandDispatcher = new CommandDispatcher(commandHandlers);
    using IDisposable helpCommandRegistration = commandDispatcher.Register(new HelpCommand(commandDispatcher));
    using IDisposable aliasesCommandRegistration = commandDispatcher.Register(new AliasesCommand(commandDispatcher));
    using IDisposable configCommandRegistration = commandDispatcher.Register(
        new ConfigurationCommand(serverConfiguration));
    var lifecycle = new RuntimeLifecycleTracker(processStartedAt);
    var networkMetrics = new NetworkMetricsCounter();
    var securitySignals = new BoundedSecuritySignalStore();
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
        AutoWorldCleanerOptions cleanerOptions = AutoWorldCleanerOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "auto-world-cleaner.json"),
            logger);
        var worldEntities = new TerrariaWorldEntityMaintenance(gameThreadDispatcher);
        var autoWorldCleaner = new AutoWorldCleanerModule(
            cleanerOptions,
            worldEntities,
            lifecycle,
            gameThreadDispatcher,
            playerBoundary,
            logger);
        using var moduleHost = new VegaModuleHost([pvpModule, customItemsModule, autoWorldCleaner]);
        try
        {
            moduleHost.Start();
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Critical, "modules", "Vega module startup failed.", ex);
            return 1;
        }

        IVegaDatabase database;
        try
        {
            VegaDatabaseOptions databaseOptions = VegaDatabaseOptionsStore.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "database.json"));
            database = VegaDatabaseFactory.Create(databaseOptions, AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            logger.Log(
                OperationsLogLevel.Critical,
                "persistence",
                "Vega database configuration is invalid.",
                ex);
            return 1;
        }

        GameplayEnforcementOptions gameplayOptions = GameplayEnforcementOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "security.json"),
            logger);
        using var gameplayEnforcement = new GameplayEnforcementPolicy(
            gameplayOptions,
            playerAuthorization,
            playerBoundary,
            playerBoundary);
        using var packetRateLimits = new PacketRateLimitPolicy(playerBoundary);
        var playerPacketPolicies = new PacketProcessorPipeline<PacketView>(
        [
            packetRateLimits,
            gameplayEnforcement,
            new RegionBuildAuthorizationPolicy(database.Regions, playerBoundary, playerAuthorization),
            new NpcCoinPickupAuthorityPolicy(),
            new WorldItemSyncSanityPolicy(
                TerrariaPacketSecurityLimits.ItemTypeCount,
                TerrariaPacketSecurityLimits.PrefixCount),
            new ProjectileSpawnSanityPolicy(
                TerrariaPacketSecurityLimits.ProjectileCount,
                TerrariaPacketSecurityLimits.ProjectileTypeCount),
            new PlayerDeathDamageAuthorityPolicy(),
            new PlayerStateSanityPolicy(
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight,
                static () => TerrariaPacketSecurityLimits.InventorySlotCount),
            new WorldMutationSanityPolicy(
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight,
                static () => TerrariaPacketSecurityLimits.ChestCount,
                static () => TerrariaPacketSecurityLimits.InventorySlotCount,
                TerrariaPacketSecurityLimits.TileTypeCount,
                TerrariaPacketSecurityLimits.WallTypeCount,
                TerrariaPacketSecurityLimits.ItemTypeCount,
                TerrariaPacketSecurityLimits.PrefixCount),
            new PlayerPacketOwnershipPolicy(),
            pvpModule.PacketProcessor
        ]);
        using IDisposable worldCleanCommandRegistration = commandDispatcher.Register(
            new AutoWorldCleanerCommand(autoWorldCleaner));

        var banService = new GlobalBanService(database.GlobalBans);
        using IDisposable banCommandRegistration = commandDispatcher.Register(
            new BanCommand(playerBoundary, playerBoundary, banService));
        using IDisposable accountCommandRegistration = commandDispatcher.Register(
            new AccountCommand(database.Accounts, database.Authorization));
        using IDisposable groupCommandRegistration = commandDispatcher.Register(
            new GroupCommand(database.Authorization));
        using IDisposable regionCommandRegistration = commandDispatcher.Register(
            new RegionCommand(database.Regions));
        using var banEnforcer = new PersistentBanEnforcer(
            banService,
            playerBoundary,
            playerBoundary,
            playerBoundary,
            logger);
        using var worldImages = new TerrariaWorldImageService(logger, lifecycle, worldImageOptions.Enabled);
        WorldMaintenanceOptions worldMaintenanceOptions = WorldMaintenanceOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "world-maintenance.json"),
            logger);
        using var worldMaintenance = new TerrariaWorldMaintenanceService(
            worldMaintenanceOptions,
            logger);
        var hostMetrics = new ProcessHostMetricsReader();
        var runtimeMetrics = new SampledRuntimeMetricsReader(hostMetrics);
        WorldClockOptions worldClockOptions = WorldClockOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "world-clock.json"),
            logger);
        var worldClock = new MonotonicWorldClock(worldClockOptions);
        var staticModules = new StaticModuleOperationsCatalog(CreateModuleSnapshots(moduleHost));
        ServerPluginHotReloadOptions hotReloadOptions = ServerPluginHotReloadOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "modules.json"),
            logger);
        using var serverPlugins = new ServerPluginHotReloadHost(
            Path.Combine(AppContext.BaseDirectory, "ServerPlugins"),
            hotReloadOptions,
            commandDispatcher,
            logger);
        serverPlugins.Start();
        using IDisposable pluginCommandRegistration = commandDispatcher.Register(
            new ServerPluginCommand(serverPlugins));
        var modules = new CompositeModuleOperationsCatalog(
            staticModules,
            new HotReloadableModuleOperationsCatalog(serverPlugins));
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
            worldImages,
            serverPlugins,
            worldClock,
            autoWorldCleaner,
            securitySignals: securitySignals);
        var operationsCommands = new OperationsCommandService(
            commandDispatcher,
            localization,
            localization);
        using var startupProgress = new TerrariaStartupProgress();
        using var terminalUi = new TerminalUiHost(
            snapshots,
            operationsCommands,
            logger,
            startupProgress,
            startupProgress,
            commandCatalog: commandDispatcher);
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
            "persistence",
            $"Database provider: {database.Provider}; initialization is required before world loading.");
        logger.Log(
            OperationsLogLevel.Information,
            "world-image",
            $"World image mode: {(worldImageOptions.Enabled ? "on" : "off")}; " +
            $"direct_image={worldImageOptions.DirectImageRequested.ToString().ToLowerInvariant()}.");
        logger.Log(
            OperationsLogLevel.Information,
            "world-maintenance",
            $"Autosave: {(worldMaintenanceOptions.AutoSave.Enabled ? "on" : "off")} " +
            $"every {worldMaintenanceOptions.AutoSave.IntervalMinutes} min.");
        logger.Log(
            OperationsLogLevel.Information,
            "auto-world-cleaner",
            $"AutoWorldCleaner: {(cleanerOptions.Enabled ? "on" : "off")} " +
            $"every {cleanerOptions.IntervalMinutes} min; " +
            $"items={cleanerOptions.Items.Enabled.ToString().ToLowerInvariant()}, " +
            $"projectiles={cleanerOptions.Projectiles.Enabled.ToString().ToLowerInvariant()}.");

        return TerrariaServerHost.Run(
            serverArgs,
            lifecycle,
            hardmodeStarting,
            gameThreadDispatcher,
            playerBoundary,
            connectionMetadata,
            operationsState,
            networkMetrics,
            securitySignals,
            logger,
            worldImages,
            worldMaintenance,
            worldClock,
            [database],
            playerPacketPolicies,
            commandDispatcher,
            playerAuthorization,
            localization,
            localization);
    }
}

IDisposable RegisterCoreRussianCatalog(ILocalizationService localization) =>
    localization.RegisterCatalog(
        "tshock",
        "ru-RU",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command.lang.current"] = "Текущий язык: {0}. Используйте /lang list, /lang <культура> или /lang reset.",
            ["command.lang.list"] = "Доступные языки: {0}.",
            ["command.lang.reset"] = "Язык сброшен на серверный по умолчанию: {0}.",
            ["command.lang.changed"] = "Язык изменён на {0}.",
            ["command.lang.unavailable"] = "Язык '{0}' недоступен. Доступные языки: {1}.",
            ["command.lang.usage"] = "Использование: lang | lang list | lang <культура> | lang reset.",
            ["command.permission_denied"] = "Недостаточно прав для этой команды.",
            ["command.not_found"] = "Неизвестная команда."
        });

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
        ["Vega.Cluster", "Vega.Core", "Vega.Modules.AutoWorldCleaner", "Vega.Modules.Pvp", "Vega.Operations", "Vega.Persistence", "Vega.Terraria", "Vega.TerminalUI"]),
    Module("Vega.Core", typeof(CommandDispatcher).Assembly, ["Vega.Contracts"]),
    Module("Vega.Cluster", typeof(UnavailableClusterClient).Assembly, ["Vega.Contracts"]),
    Module("Vega.Operations", typeof(OperationsSnapshotProvider).Assembly, ["Vega.Contracts"]),
    Module("Vega.Persistence", typeof(IVegaDatabase).Assembly, ["Vega.Contracts", "System.Data.Common"]),
    Module("Vega.Terraria", typeof(TerrariaServerHost).Assembly, ["Vega.Contracts", "OTAPI", "Multiplicity"]),
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

sealed class HotReloadableModuleOperationsCatalog(IModuleOperationsCatalog inner) : IModuleOperationsCatalog
{
    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => Array.AsReadOnly(inner
        .SnapshotModules()
        .Select(static module => module with { IsHotReloadable = true })
        .ToArray());
}
