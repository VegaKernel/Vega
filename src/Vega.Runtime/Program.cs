using System.Diagnostics;
using System.Reflection;
using Multiplicity.Packets.Views;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Localization;
using Vega.Contracts.Modules;
using Vega.Backups;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Commands.Players;
using Vega.Core.Commands.System;
using Vega.Core.Authorization;
using Vega.Core.Bans;
using Vega.Core.Configuration;
using Vega.Core.Localization;
using Vega.Core.Messaging;
using Vega.Core.Modules;
using Vega.Core.Operations;
using Vega.Core.Players;
using Vega.Core.Scenes;
using Vega.Core.Updates;
using Vega.Core.Worlds;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;
using Vega.Cluster.Transport;
using Vega.Core.Events;
using Vega.Core.Extensions;
using Vega.Core.Networking;
using Vega.Modules.AutoWorldCleaner;
using Vega.Modules.Chests;
using Vega.Modules.CustomItems;
using Vega.Modules.Mentions;
using Vega.Modules.Navigation;
using Vega.Modules.Restrictions;
using Vega.Contracts.Items;
using Vega.Modules.Pvp;
using Vega.Modules.Scenes;
using Vega.Modules.Wipe;
using Vega.Operations;
using Vega.Operations.Actions;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Operations.ReadModels;
using Vega.Persistence;
using Vega.PluginSdk;
using Vega.Regions;
using Vega.Sandboxes;
using Vega.TerminalUI;
using Vega.Terraria;
using Vega.Terraria.Items;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;
using Vega.Terraria.Operations;
using Vega.Terraria.Threading;
using Vega.Terraria.Scenes;
using Vega.Terraria.Worlds;
using Vega.Runtime;

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
    if (OfflineMigrationCommands.TryRun(args, AppContext.BaseDirectory) is { } migrationExitCode) return migrationExitCode;

    SandboxBackendLaunchOptions backendLaunch;
    try
    {
        backendLaunch = SandboxBackendLaunchOptions.Parse(args);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    VegaServerConfigurationState serverConfiguration;
    try
    {
        serverConfiguration = new VegaServerConfigurationState(
            Path.Combine(AppContext.BaseDirectory, "config", "server.json"),
            backendLaunch.ServerArguments);
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
    NativeTileStorageOptions tileStorageOptions;
    try
    {
        worldImageOptions = WorldImageOptionsParser.Parse(uiOptions.ServerArguments);
        tileStorageOptions = NativeTileStorageOptions.Parse(worldImageOptions.ServerArguments);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }

    string[] serverArgs = tileStorageOptions.ServerArguments.ToArray();
    using var tileStorage = tileStorageOptions.Enabled ? new NativeTileStorageScope() : null;
    TerrariaServerHost.PrepareSavePath(serverArgs);
    if (tileStorage is not null)
    {
        try
        {
            PagedNativeTileCollection canonical = tileStorage.Complete();
            Console.WriteLine($"Vega paged native tile storage: {canonical.Width}x{canonical.Height}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeInitializationException)
        {
            Console.Error.WriteLine($"Paged tile storage initialization failed: {ex.Message}");
            return 2;
        }
    }
    string configuredWorldPath = serverConfiguration.Current.Values["world"].DisplayValue;
    if (!configuredWorldPath.StartsWith('<'))
    {
        try
        {
            string? resetBackup = FileWorldResetBackend.ApplyPending(configuredWorldPath);
            if (resetBackup is not null)
            {
                Console.WriteLine($"Applied pending world reset; previous world: {resetBackup}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Pending world reset could not be applied: {ex.Message}");
            return 2;
        }
    }
    string configuredCulture = serverConfiguration.Current.Values["defaultCulture"].DisplayValue;
    var localization = new LocalizationService(
        configuredCulture.StartsWith('<') ? "en-US" : configuredCulture);
    using IDisposable coreRussianCatalog = RegisterCoreRussianCatalog(localization);

    // Explicit composition root: no assembly scanning or reflection-based handler discovery.
    IEventPipeline<WorldHardmodeStarting> hardmodeStarting = new EventPipeline<WorldHardmodeStarting>(
        Array.Empty<IEventHandler<WorldHardmodeStarting>>(),
        Array.Empty<IEventMonitor<WorldHardmodeStarting>>());

    using var gameThreadDispatcher = new TerrariaRuntimeGameThreadDispatcher();
    WorldId runtimeWorldId = backendLaunch.IsBackend ? backendLaunch.WorldId : WorldId.Primary;
    string runtimeDataRoot = backendLaunch.IsBackend
        ? Path.Combine(AppContext.BaseDirectory, "data", "sandboxes", "instances", runtimeWorldId.Value, "runtime", "data")
        : Path.Combine(AppContext.BaseDirectory, "data");
    var connectionMetadata = new TerrariaConnectionMetadataRegistry();
    using var playerBoundary = new TerrariaPlayerBoundary(gameThreadDispatcher, connectionMetadata, runtimeWorldId);
    using var playerAuthorization = new PlayerSessionAuthorizationService(playerBoundary);
    using var updateChecker = new GitHubReleaseUpdateChecker();
    using var extensionState = new ExtensionStateStore();
    using var operationsState = new TerrariaOperationsState();
    var moduleRouter = new RuntimeModuleGenerationRouter();
    ICommandHandler[] commandHandlers =
    [
        new ServerInfoCommand(new ProcessHostMetricsReader()),
        new TickRateCommand(operationsState),
        new CheckUpdatesCommand(updateChecker),
        new KickCommand(playerBoundary, playerBoundary, playerAuthorization),
        new PlayingCommand(playerBoundary)
    ];
    var commandDispatcher = new CommandDispatcher(commandHandlers);
    using IDisposable broadcastRegistration = commandDispatcher.Register(new BroadcastCommand(playerBoundary, playerBoundary));
    using IDisposable giveCommandRegistration = commandDispatcher.Register(new ItemCommand(playerBoundary, playerBoundary, giveOthers: true));
    using IDisposable itemCommandRegistration = commandDispatcher.Register(new ItemCommand(playerBoundary, playerBoundary));
    using IDisposable healCommandRegistration = commandDispatcher.Register(new HealCommand(playerBoundary, playerBoundary));
    using IDisposable buffCommandRegistration = commandDispatcher.Register(new BuffCommand(playerBoundary, playerBoundary));
    using IDisposable giveBuffCommandRegistration = commandDispatcher.Register(new BuffCommand(playerBoundary, playerBoundary, others: true));
    using IDisposable killRegistration = commandDispatcher.Register(new KillCommand(playerBoundary, playerBoundary));
    using IDisposable slapRegistration = commandDispatcher.Register(new SlapCommand(playerBoundary, playerBoundary));
    using IDisposable respawnRegistration = commandDispatcher.Register(new RespawnCommand(playerBoundary, playerBoundary));
    using IDisposable syncRegistration = commandDispatcher.Register(new SyncCommand(playerBoundary, playerBoundary));
    using IDisposable annoyRegistration = commandDispatcher.Register(new AnnoyCommand(playerBoundary, playerBoundary));
    using IDisposable fireworkRegistration = commandDispatcher.Register(new FireworkCommand(playerBoundary, playerBoundary));
    using IDisposable godModeRegistration = commandDispatcher.Register(new GodModeCommand(playerBoundary, playerBoundary));
    using IDisposable deathRegistration = commandDispatcher.Register(new PlayerDeathStatisticsCommand(playerBoundary, pvp: false, all: false));
    using IDisposable pvpDeathRegistration = commandDispatcher.Register(new PlayerDeathStatisticsCommand(playerBoundary, pvp: true, all: false));
    using IDisposable allDeathRegistration = commandDispatcher.Register(new PlayerDeathStatisticsCommand(playerBoundary, pvp: false, all: true));
    using IDisposable allPvpDeathRegistration = commandDispatcher.Register(new PlayerDeathStatisticsCommand(playerBoundary, pvp: true, all: true));
    using IDisposable motdRegistration = commandDispatcher.Register(new ServerTextCommand("motd", Path.Combine(runtimeDataRoot, "motd.txt"), Path.Combine(AppContext.BaseDirectory, "config", "motd.txt")));
    using IDisposable rulesRegistration = commandDispatcher.Register(new ServerTextCommand("rules", Path.Combine(runtimeDataRoot, "rules.txt"), Path.Combine(AppContext.BaseDirectory, "config", "rules.txt")));
    using IDisposable versionRegistration = commandDispatcher.Register(new ServerVersionCommand(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        typeof(global::Terraria.Main).Assembly.GetName().Version?.ToString() ?? "unknown"));
    using IDisposable helpCommandRegistration = commandDispatcher.Register(new HelpCommand(commandDispatcher));
    using IDisposable aliasesCommandRegistration = commandDispatcher.Register(new AliasesCommand(commandDispatcher));
    var lifecycle = new RuntimeLifecycleTracker(processStartedAt);
    var networkMetrics = new NetworkMetricsCounter();
    var securitySignals = new BoundedSecuritySignalStore();
    var logStore = new BoundedOperationsLogStore();
    using IDisposable displayLogsRegistration = commandDispatcher.Register(new DisplayLogsCommand(logStore));
    var consoleSink = new ConsoleOperationsLogSink();
    JsonFileOperationsLogSink? fileSink = TryCreateFileSink(backendLaunch);
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
        ConnectionWhitelist whitelist;
        try { whitelist = new ConnectionWhitelist(Path.Combine(AppContext.BaseDirectory, "data", "whitelist.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.Log(OperationsLogLevel.Critical, "admission", "Invalid whitelist configuration; refusing to start.", ex);
            return 2;
        }
        using IDisposable whitelistRegistration = commandDispatcher.Register(new WhitelistCommand(whitelist));
        RestrictionStore restrictions;
        try { restrictions = new RestrictionStore(Path.Combine(runtimeDataRoot, "restrictions.json"),
            TerrariaPacketSecurityLimits.ItemTypeCount, TerrariaPacketSecurityLimits.ProjectileTypeCount, TerrariaPacketSecurityLimits.TileTypeCount); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.Log(OperationsLogLevel.Critical, "restrictions", "Invalid content restriction configuration; refusing to start.", ex);
            return 2;
        }
        var restrictionPolicy = new ContentRestrictionPolicy(restrictions, playerAuthorization, playerBoundary);
        playerBoundary.ConfigureContentRestrictions(restrictionPolicy);
        using IDisposable itemBanRegistration = commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Item));
        using IDisposable projectileBanRegistration = commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Projectile));
        using IDisposable tileBanRegistration = commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Tile));
        SandboxOptions sandboxOptions = SandboxOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "sandboxes.json"));
        string sandboxDataRoot = Path.Combine(AppContext.BaseDirectory, "data", "sandboxes");
        string sandboxSscPath = backendLaunch.IsBackend && !string.IsNullOrWhiteSpace(backendLaunch.SscStorePath)
            ? backendLaunch.SscStorePath
            : Path.Combine(sandboxDataRoot, "ssc.json");
        var sandboxSscStore = new SandboxSscStore(sandboxSscPath);
        var sandboxCharacterAdapter = new TerrariaSandboxSscCharacterAdapter(gameThreadDispatcher, playerBoundary);
        using var sandboxControl = backendLaunch.IsBackend
            ? null
            : new SandboxControlService(
                AppContext.BaseDirectory, sandboxOptions, logger, sscStore: sandboxSscStore);
        ISandboxDirectory sandboxDirectory = sandboxControl is null
            ? new SandboxSharedDirectory(AppContext.BaseDirectory)
            : sandboxControl;
        var transferTickets = new SandboxTransferTicketService(Path.Combine(sandboxDataRoot, "control.key"));
        var sandboxAdmissions = new SandboxTransferAdmissionService(
            Path.Combine(sandboxDataRoot, "admissions.json"),
            transferTickets,
            sandboxSscStore);
        var sandboxRouting = new SandboxRoutingService(
            sandboxDirectory,
            transferTickets,
            new SandboxEndpoint(
                "127.0.0.1",
                int.TryParse(serverConfiguration.Current.Values["port"].DisplayValue, out int primaryPort)
                    ? primaryPort
                    : 7777),
            sandboxSscStore);
        using var sceneRenderer = new TerrariaSceneRenderSink(
            gameThreadDispatcher,
            playerBoundary,
            logger,
            runtimeWorldId);
        var scenes = new SceneRuntime(sceneRenderer);
        var visibility = new SceneVisibilityRuntime();
        void OnSceneSessionOpened(PlayerSessionOpened opened)
        {
            scenes.OpenSession(runtimeWorldId, opened);
            visibility.OpenSession(runtimeWorldId, opened);
        }
        void OnSceneSessionClosed(PlayerSessionClosed closed)
        {
            scenes.CloseSession(runtimeWorldId, closed);
            visibility.CloseSession(runtimeWorldId, closed);
        }
        playerBoundary.Opened += OnSceneSessionOpened;
        playerBoundary.Closing += OnSceneSessionClosed;
        playerBoundary.Closed += OnSceneSessionClosed;
        sceneRenderer.Attach(scenes, playerBoundary);
        WorldMaintenanceOptions worldMaintenanceOptions = WorldMaintenanceOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "world-maintenance.json"),
            logger);
        using var worldMaintenance = new TerrariaWorldMaintenanceService(
            worldMaintenanceOptions,
            logger,
            gameThreadDispatcher);
        var serverControl = new TerrariaServerControl(
            worldMaintenance,
            gameThreadDispatcher,
            logger);
        var hostMetrics = new ProcessHostMetricsReader();
        var runtimeMetrics = new SampledRuntimeMetricsReader(hostMetrics);
        WorldClockOptions worldClockOptions = WorldClockOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "world-clock.json"), logger);
        var worldClock = new MonotonicWorldClock(worldClockOptions);
        var worldEnvironment = new TerrariaWorldEnvironmentService(gameThreadDispatcher, runtimeWorldId, worldClock);
        using var rest = new LoopbackRestService(runtimeWorldId, lifecycle, operationsState, playerBoundary, networkMetrics, serverControl,
            runtimeMetrics, gameThreadDispatcher.ReadQueueMetrics, worldEnvironment, logger);
        using IDisposable restRegistration = commandDispatcher.Register(rest);
        if (!backendLaunch.IsBackend && Environment.GetEnvironmentVariable("VEGA_REST_PORT") is { Length: > 0 } restPort)
        {
            try
            {
                if (!int.TryParse(restPort, out int port)) throw new InvalidDataException("Invalid REST port.");
                rest.Start(port, Environment.GetEnvironmentVariable("VEGA_REST_TOKEN_SHA256") ?? "",
                    Environment.GetEnvironmentVariable("VEGA_REST_MAINTENANCE_TOKEN_SHA256"));
                logger.Log(OperationsLogLevel.Information, "rest", $"Scoped REST available on loopback port {port}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.HttpListenerException or UnauthorizedAccessException or ArgumentException)
            {
                logger.Log(OperationsLogLevel.Critical, "rest", "Could not start configured loopback REST listener.", ex);
                return 2;
            }
        }
        var regionReset = new BindableWorldResetParticipant("regions");
        using var worldReset = new WorldResetOrchestrator(
            new FileWorldResetBackend(),
            [regionReset],
            logger);
        AutoWorldCleanerOptions cleanerOptions = AutoWorldCleanerOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "auto-world-cleaner.json"),
            logger);
        var worldEntities = new TerrariaWorldEntityMaintenance(gameThreadDispatcher, runtimeWorldId);
        var worldTiles = new TerrariaWorldTileService(gameThreadDispatcher, runtimeWorldId);
        var worldNpcs = new TerrariaWorldNpcService(gameThreadDispatcher, runtimeWorldId, playerBoundary);
        var worldSchematics = new SchematicSnapshotService(runtimeWorldId, new TerrariaSchematicCapture(gameThreadDispatcher), gameThreadDispatcher,
            new TerrariaSchematicPlacement(gameThreadDispatcher, runtimeWorldId).Place);
        WorldBuildProtection buildProtection;
        try { buildProtection = new WorldBuildProtection(Path.Combine(runtimeDataRoot, "build-protection.json"), runtimeWorldId, playerAuthorization, worldEnvironment.GetSpawnPosition); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            logger.Log(OperationsLogLevel.Critical, "build-protection", "Build protection settings are invalid.", ex);
            return 2;
        }
        using IDisposable antiBuildRegistration = commandDispatcher.Register(new WorldBuildProtectionCommand(buildProtection, spawn: false));
        using IDisposable protectSpawnRegistration = commandDispatcher.Register(new WorldBuildProtectionCommand(buildProtection, spawn: true));
        var reloadConfiguration = new ReloadConfigurationCommand(serverConfiguration, runtimeWorldId,
            [("shared whitelist", whitelist.ReloadAsync), ("restrictions", restrictions.ReloadAsync), ("build-protection", buildProtection.ReloadAsync)]);
        using IDisposable reloadRegistration = commandDispatcher.Register(reloadConfiguration);
        using IDisposable configCommandRegistration = commandDispatcher.Register(new ConfigurationCommand(serverConfiguration, reloadConfiguration));
        using var navigationModule = new NavigationModule(commandDispatcher, playerBoundary, playerBoundary, playerBoundary,
            worldEnvironment, new WarpStore(Path.Combine(runtimeDataRoot, "warps")), worldNpcs, playerBoundary);
        navigationModule.Start();
        using IDisposable timeCommandRegistration = commandDispatcher.Register(new TimeCommand(worldEnvironment));
        using IDisposable windCommandRegistration = commandDispatcher.Register(new WindCommand(worldEnvironment));
        using IDisposable worldInfoCommandRegistration = commandDispatcher.Register(new WorldInfoCommand(worldEnvironment));
        using IDisposable worldEventCommandRegistration = commandDispatcher.Register(new WorldEventCommand(worldEnvironment));
        using IDisposable modeRegistration = commandDispatcher.Register(new WorldModeCommand(worldEnvironment));
        using IDisposable spawnRateRegistration = commandDispatcher.Register(new WorldSpawnSettingsCommand(worldEnvironment, rate: true));
        using IDisposable maxSpawnsRegistration = commandDispatcher.Register(new WorldSpawnSettingsCommand(worldEnvironment, rate: false));
        using IDisposable setSpawnRegistration = commandDispatcher.Register(new WorldPointCommand(worldEnvironment, playerBoundary, dungeon: false));
        using IDisposable setDungeonRegistration = commandDispatcher.Register(new WorldPointCommand(worldEnvironment, playerBoundary, dungeon: true));
        using IDisposable forceHalloweenRegistration = commandDispatcher.Register(new ForceSeasonCommand(worldEnvironment, halloween: true));
        using IDisposable forceChristmasRegistration = commandDispatcher.Register(new ForceSeasonCommand(worldEnvironment, halloween: false));
        using IDisposable clearAnglerRegistration = commandDispatcher.Register(new ClearAnglerCommand(worldEnvironment));
        using IDisposable hardmodeRegistration = commandDispatcher.Register(new HardmodeCommand(worldEnvironment));
        using IDisposable bossDamageRegistration = commandDispatcher.Register(new BossDamageCommand(new TerrariaBossDamageHistory(gameThreadDispatcher, runtimeWorldId)));
        var worldNativeActions = new TerrariaWorldNativeActions(gameThreadDispatcher, runtimeWorldId, playerBoundary);
        using IDisposable evilRegistration = commandDispatcher.Register(new EvilCommand(worldNativeActions));
        using IDisposable settleRegistration = commandDispatcher.Register(new SettleCommand(worldNativeActions));
        using IDisposable growRegistration = commandDispatcher.Register(new GrowCommand(worldNativeActions, playerBoundary));
        using IDisposable referenceDataRegistration = commandDispatcher.Register(new ReferenceDataCommand(new TerrariaReferenceDataExporter(runtimeDataRoot, runtimeWorldId)));
        using IDisposable spawnMobCommandRegistration = commandDispatcher.Register(new SpawnMobCommand(playerBoundary, worldNpcs, worldNpcs));
        using IDisposable spawnBossRegistration = commandDispatcher.Register(new SpawnMobCommand(playerBoundary, worldNpcs, worldNpcs, boss: true));
        using IDisposable butcherRegistration = commandDispatcher.Register(new ButcherCommand(worldNpcs, worldNpcs));
        using IDisposable renameNpcRegistration = commandDispatcher.Register(new RenameNpcCommand(worldNpcs, worldNpcs));
        using IDisposable clearCommandRegistration = commandDispatcher.Register(new ClearCommand(worldEntities, worldNpcs, gameThreadDispatcher));
        var chestBackend = new TerrariaChestBackend(gameThreadDispatcher, runtimeWorldId);
        var chestService = new ChestService(
            chestBackend,
            new AsyncEventPipeline<ChestChanging>([]),
            new AsyncEventPipeline<ChestChanged>([]));
        SoftRestartOptions softRestartOptions = SoftRestartOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "soft-restart.json"));
        RestartableModuleGraph CreateRestartableGraph(IReadOnlySet<string>? requested)
        {
            var pvp = new PvpModule(extensionState, playerBoundary, playerBoundary, playerBoundary);
            var customItems = new CustomItemsModule(new TerrariaCustomItemDelivery(gameThreadDispatcher));
            var cleaner = new AutoWorldCleanerModule(
                cleanerOptions,
                worldEntities,
                lifecycle,
                gameThreadDispatcher,
                playerBoundary,
                logger,
                worldId: runtimeWorldId);
            var mentions = new MentionRenderer();
            var chests = new ChestPolicyModule(
                chestService,
                logger,
                Path.Combine(runtimeDataRoot, "chest-templates.json"));
            var wipe = new WipeModule(
                worldReset,
                serverControl,
                configuredWorldPath.StartsWith('<') ? null : configuredWorldPath);
            var sceneConsumers = new SceneConsumersModule(scenes, visibility, playerBoundary, runtimeWorldId);
            IVegaModule[] candidates = [pvp, customItems, cleaner, mentions, chests, wipe, sceneConsumers];
            IVegaModule[] configured = backendLaunch.IsBackend && backendLaunch.EnabledModules.Count > 0
                ? candidates.Where(module =>
                {
                    string name = module.Descriptor.Name;
                    string shortName = name[(name.LastIndexOf('.') + 1)..];
                    return backendLaunch.EnabledModules.Contains(name) || backendLaunch.EnabledModules.Contains(shortName);
                }).ToArray()
                : candidates;
            foreach (IVegaModule excluded in candidates.Except(configured)) excluded.Dispose();
            bool Enabled(IVegaModule module) => configured.Contains(module);
            var moduleCommands = new List<ICommandHandler>();
            if (Enabled(cleaner)) moduleCommands.Add(new AutoWorldCleanerCommand(cleaner));
            if (Enabled(chests)) moduleCommands.Add(new ChestPolicyCommand(chests));
            if (Enabled(wipe)) moduleCommands.Add(new WipeCommand(wipe));
            if (Enabled(sceneConsumers))
            {
                moduleCommands.Add(new SceneCommand(sceneConsumers, scenes, playerBoundary));
                moduleCommands.Add(new SchematicCommand(worldSchematics));
                moduleCommands.Add(new FakeProviderCommand(sceneConsumers.FakeProviders, playerBoundary, gameThreadDispatcher, personal: false));
                moduleCommands.Add(new FakeProviderCommand(sceneConsumers.FakeProviders, playerBoundary, gameThreadDispatcher, personal: true));
            }
            IReadOnlyDictionary<string, ICommandHandler> commands = moduleCommands.ToDictionary(
                static command => command.Descriptor.Name,
                StringComparer.OrdinalIgnoreCase);
            var host = new VegaModuleHost(configured);
            return new RestartableModuleGraph(
                host,
                configured.Select(static module => module.Descriptor.Name).ToArray(),
                () => moduleRouter.Activate(new RuntimeModuleGeneration(
                    Enabled(pvp) ? pvp.PacketProcessor : null,
                    Enabled(mentions) ? mentions : null,
                    Enabled(chests) ? chests : null,
                    Enabled(cleaner) ? cleaner : null,
                    commands)));
        }

        SoftRestartCoordinator? startedModules;
        try
        {
            startedModules = new SoftRestartCoordinator(softRestartOptions, CreateRestartableGraph, logger);
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Critical, "modules", "Vega module startup failed.", ex);
            return 1;
        }

        using var softRestart = startedModules;
        moduleRouter.Attach(softRestart);
        using IDisposable moduleCommandRegistrations = moduleRouter.RegisterCommands(commandDispatcher);

        IVegaDatabase database;
        try
        {
            VegaDatabaseOptions databaseOptions = VegaDatabaseOptionsStore.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "database.json"));
            database = VegaDatabaseFactory.Create(databaseOptions, AppContext.BaseDirectory);
            regionReset.Bind(new RegionWorldResetParticipant(database.Regions));
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

        var banService = new GlobalBanService(database.GlobalBans);
        using var accountSessions = new PlayerAccountSessionService(
            database.Accounts,
            database.Authorization,
            playerAuthorization,
            localization,
            playerBoundary,
            banService,
            database.RoleLeases);
        using IDisposable rocketRegistration = commandDispatcher.Register(new RocketCommand(playerBoundary,
            (player, token) => playerBoundary.RocketAsync(player, accountSessions, token)));
        using IDisposable registerAccountCommandRegistration = commandDispatcher.Register(
            new RegisterAccountCommand(accountSessions));
        using IDisposable loginAccountCommandRegistration = commandDispatcher.Register(
            new LoginAccountCommand(accountSessions));
        using IDisposable logoutAccountCommandRegistration = commandDispatcher.Register(
            new LogoutAccountCommand(accountSessions));
        using IDisposable passwordAccountCommandRegistration = commandDispatcher.Register(
            new PasswordAccountCommand(accountSessions));
        using IDisposable languageCommandRegistration = commandDispatcher.Register(
            new LanguageCommand(localization, accountSessions));
        var inventoryService = new TerrariaPlayerInventoryService(gameThreadDispatcher, playerBoundary);
        var inspectionService = new PlayerInspectionService(playerBoundary, inventoryService);
        using IDisposable userInfoCommandRegistration = commandDispatcher.Register(
            new UserInfoCommand(playerBoundary, inspectionService));
        using IDisposable inventoryCommandRegistration = commandDispatcher.Register(
            new InventoryCommand(playerBoundary, inventoryService));
        var chat = new ChatPipeline(
            moderation: [new MuteChatModerationRule(accountSessions, database.Mutes)],
            renderers: [moduleRouter]);
        using var chatCommands = new PlayerChatCommands(commandDispatcher, playerBoundary, playerBoundary, playerBoundary, chat);

        GameplayEnforcementOptions gameplayOptions = GameplayEnforcementOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "security.json"),
            logger);
        try
        {
            connectionMetadata.ConfigureDimensions(
                gameplayOptions.EnableDimensionsPacket,
                gameplayOptions.DimensionsTrustedProxies);
            connectionMetadata.ConfigureSandboxAdmission(
                sandboxAdmissions,
                runtimeWorldId,
                backendLaunch.IsBackend);
        }
        catch (InvalidDataException ex)
        {
            logger.Log(OperationsLogLevel.Critical, "connection-metadata", "Dimensions trust configuration is invalid.", ex);
            return 2;
        }
        PrimaryCharacterPersistenceOptions primaryCharacterOptions;
        try { primaryCharacterOptions = PrimaryCharacterPersistenceOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "primary-character-persistence.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            logger.Log(OperationsLogLevel.Critical, "character-persistence", "Primary character persistence settings are invalid.", ex);
            return 2;
        }
        bool authoritativeCharacters = backendLaunch.IsBackend
            ? backendLaunch.SscMode != SandboxSscMode.Disabled : primaryCharacterOptions.Authoritative;
        string legacyPrimaryCharacterPath = Path.Combine(runtimeDataRoot, "primary-characters.json");
        if (!backendLaunch.IsBackend && primaryCharacterOptions.Authoritative && File.Exists(legacyPrimaryCharacterPath))
        {
            logger.Log(OperationsLogLevel.Critical, "character-persistence",
                "Authoritative SSC shares the Inherit character store with sandbox transfers. A legacy primary-characters.json exists; stop all worlds and run --vega-migrate-primary-ssc before enabling this mode. No character data was changed.");
            return 2;
        }
        using var sandboxSscSessions = new SandboxSscSessionCoordinator(
            playerBoundary,
            playerBoundary,
            accountSessions,
            playerBoundary,
            sandboxSscStore,
            sandboxCharacterAdapter,
            runtimeWorldId,
            backendLaunch.IsBackend ? backendLaunch.SscMode : SandboxSscMode.Inherit,
            logger,
            primaryCharacterStore: !backendLaunch.IsBackend && primaryCharacterOptions.Enabled
                ? (primaryCharacterOptions.Authoritative ? sandboxSscStore : new SandboxSscStore(legacyPrimaryCharacterPath)) : null);
        using IDisposable saveSscRegistration = commandDispatcher.Register(new SaveSscCommand(sandboxSscSessions));
        using IDisposable overrideSscRegistration = commandDispatcher.Register(new OverrideSscCommand(sandboxSscSessions, playerBoundary));
        using IDisposable uploadSscRegistration = commandDispatcher.Register(new UploadSscCommand(sandboxSscSessions, playerBoundary));

        using var gameplayEnforcement = new GameplayEnforcementPolicy(
            gameplayOptions,
            playerAuthorization,
            playerBoundary,
            playerBoundary);
        using var packetRateLimits = new PacketRateLimitPolicy(playerBoundary);
        var inventoryPolicy = new PlayerInventorySanityPolicy(
            static () => TerrariaPacketSecurityLimits.CharacterSlotCount,
            static () => TerrariaPacketSecurityLimits.LoadoutCount,
            TerrariaPacketSecurityLimits.ItemTypeCount,
            TerrariaPacketSecurityLimits.PrefixCount,
            TerrariaPacketSecurityLimits.MaximumItemStack);
        var playerPacketPolicies = new PacketProcessorPipeline<PacketView>(
        [
            packetRateLimits,
            inventoryPolicy,
            new PlayerCharacterSyncPolicy(static () => TerrariaPacketSecurityLimits.BuffSlotCount, TerrariaPacketSecurityLimits.BuffTypeCount),
            gameplayEnforcement,
            new ChestInteractionAuthorityPolicy(playerBoundary),
            new TileEntityInteractionAuthorityPolicy(playerBoundary),
            new SceneInteractionAuthorityPolicy(scenes, runtimeWorldId, playerBoundary),
            new SceneEntityInteractionAuthorityPolicy(visibility, runtimeWorldId),
            new RegionBuildAuthorizationPolicy(
                database.Regions,
                playerBoundary,
                playerAuthorization,
                accountSessions,
                playerBoundary,
                buildProtection),
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
                static () => TerrariaPacketSecurityLimits.QuickStackSlotCount,
                TerrariaPacketSecurityLimits.TileTypeCount,
                TerrariaPacketSecurityLimits.WallTypeCount,
                TerrariaPacketSecurityLimits.ItemTypeCount,
                TerrariaPacketSecurityLimits.PrefixCount,
                TerrariaPacketSecurityLimits.PaintColorCount),
            new PlayerPacketOwnershipPolicy(),
            restrictionPolicy,
            moduleRouter
        ]);
        using IDisposable? sandboxCommandRegistration = sandboxControl is null
            ? null
            : commandDispatcher.Register(new SandboxCommand(sandboxControl, sandboxRouting, logger));
        using IDisposable sandboxTransferCommandRegistration = commandDispatcher.Register(new SandboxPlayerTransferCommand(
            sandboxDirectory,
            sandboxRouting,
            new TerrariaSandboxPlayerTransfer(
                gameThreadDispatcher,
                connectionMetadata,
                sandboxAdmissions,
                sandboxSscStore,
                sandboxCharacterAdapter,
                playerBoundary),
            playerBoundary,
            playerAuthorization,
            accountSessions));

        using IDisposable banCommandRegistration = commandDispatcher.Register(
            new BanCommand(playerBoundary, playerBoundary, banService));
        using IDisposable muteCommandRegistration = commandDispatcher.Register(
            new MuteCommand(playerBoundary, accountSessions, database.Mutes));
        var accountCommand = new AccountCommand(database.Accounts, database.Authorization, database.RoleLeases);
        using IDisposable setupRegistration = commandDispatcher.Register(new SetupCommand());
        using IDisposable accountCommandRegistration = commandDispatcher.Register(accountCommand);
        using IDisposable accountInfoRegistration = commandDispatcher.Register(new AccountInfoCommand(accountCommand));
        using IDisposable tempGroupRegistration = commandDispatcher.Register(new TempGroupCommand(accountCommand));
        using IDisposable groupCommandRegistration = commandDispatcher.Register(
            new GroupCommand(database.Authorization));
        using IDisposable regionCommandRegistration = commandDispatcher.Register(
            new RegionCommand(database.Regions));
        using var banEnforcer = new PersistentBanEnforcer(
            banService,
            playerBoundary,
            playerBoundary,
            playerBoundary,
            logger,
            accounts: accountSessions,
            dispatcher: gameThreadDispatcher);
        using var worldImages = new TerrariaWorldImageService(logger, lifecycle, worldImageOptions.Enabled);
        var backupBarrier = new TerrariaBackupConsistencyBarrier(serverControl, worldImages);
        BackupOptions backupOptions = BackupOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "backups.json"));
        using var backups = backendLaunch.IsBackend
            ? null
            : new DeploymentBackupService(
                AppContext.BaseDirectory,
                backupOptions,
                backupBarrier,
                database,
                () => backupBarrier.CurrentWorldPath,
                logger);
        using IDisposable? backupCommandRegistration = backups is null
            ? null
            : commandDispatcher.Register(new BackupCommand(backups));
        using IDisposable saveCommandRegistration = commandDispatcher.Register(
            new SaveCommand(serverControl));
        using IDisposable shutdownCommandRegistration = commandDispatcher.Register(
            new ShutdownCommand(serverControl));
        using IDisposable shutdownNoSaveRegistration = commandDispatcher.Register(new ShutdownCommand(serverControl, save: false));
        using IDisposable serverPasswordRegistration = commandDispatcher.Register(new ServerPasswordCommand(serverControl));
        var staticModules = new StaticModuleOperationsCatalog(CreateModuleSnapshots(softRestart));
        ServerPluginHotReloadOptions hotReloadOptions = ServerPluginHotReloadOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "modules.json"),
            logger);
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(
            runtimeWorldId,
            worldEnvironment,
            worldTiles,
            chestService,
            worldNpcs,
            scenes,
            inventoryService,
            visibility, worldSchematics));
        var worldEvents = new WorldGameplayEventHub();
        using var serverPlugins = new ServerPluginHotReloadHost(
            Path.Combine(AppContext.BaseDirectory, "ServerPlugins"),
            hotReloadOptions,
            commandDispatcher,
            logger,
            playerDirectory: playerBoundary,
            playerMessenger: playerBoundary,
            playerOperations: playerBoundary,
            playerLifecycle: playerBoundary,
            gameThread: gameThreadDispatcher,
            pluginDataRoot: Path.Combine(runtimeDataRoot, "plugins"),
            pluginConfigurationRoot: backendLaunch.IsBackend ? Path.Combine(runtimeDataRoot, "plugin-config") : null,
            worldEvents: worldEvents);
        serverPlugins.Start();
        softRestart.SetProcessRestartGuard(() => serverPlugins.Read().IsRestartRequired
            ? "ServerPlugins contains a change or cleanup failure that requires a process restart."
            : null);
        using IDisposable softRestartCommandRegistration = commandDispatcher.Register(
            new SoftRestartCommand(softRestart, logger));
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
            moduleRouter,
            worldContexts: sandboxControl is null
                ? null
                : new SandboxWorldContextCatalog(sandboxControl, operationsState, playerBoundary),
            securitySignals: securitySignals,
            regions: database.Regions,
            bans: banService,
            mutes: database.Mutes,
            scenes: scenes,
            visibility: visibility,
            backups: backups,
            localWorldId: runtimeWorldId);
        using var backendReporter = backendLaunch.IsBackend
            ? new SandboxBackendReporter(backendLaunch, lifecycle, operationsState, playerBoundary)
            : null;

        var operationsCommands = new OperationsCommandService(
            commandDispatcher,
            localization,
            localization,
            runtimeWorldId);
        using var startupProgress = new TerrariaStartupProgress(logger);
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
            runtimeWorldId,
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
            accountSessions,
            database.Mutes,
            chat,
            moduleRouter,
            scenes,
            visibility,
            sceneRenderer,
            localization,
            localization,
            ServerNetworkOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "network.json"), logger),
            whitelist,
            primaryGameplayReadiness: authoritativeCharacters ? sandboxSscSessions.IsCharacterReady
                : !backendLaunch.IsBackend && primaryCharacterOptions.GameplayGateEnabled
                    ? player => sandboxSscSessions.IsGameplayAllowed(player, primaryCharacterOptions.EffectiveRequireCharacterReady) : null,
            earlyJoinCharacters: authoritativeCharacters || !backendLaunch.IsBackend && primaryCharacterOptions.Enabled ? sandboxCharacterAdapter : null,
            joinInventoryPolicy: inventoryPolicy,
            authoritativeCharacters: authoritativeCharacters,
            worldEvents: worldEvents);
    }
}

IDisposable RegisterCoreRussianCatalog(ILocalizationService localization) =>
    localization.RegisterCatalog(
        "vega",
        "ru-RU",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command.lang.current"] = "Текущий язык: {0}. Используйте /lang list, /lang <культура> или /lang reset.",
            ["command.lang.list"] = "Доступные языки: {0}.",
            ["command.lang.reset"] = "Язык сброшен на серверный по умолчанию: {0}.",
            ["command.lang.changed"] = "Язык изменён на {0}.",
            ["command.lang.unavailable"] = "Язык '{0}' недоступен. Доступные языки: {1}.",
            ["command.lang.usage"] = "Использование: lang | lang list | lang <культура> | lang reset.",
            ["command.time.current"] = "Время в мире: {0}.",
            ["command.time.applied"] = "Время установлено на {0}.",
            ["command.time.usage"] = "Использование: time [day|noon|night|midnight|HH:mm].",
            ["command.time.unavailable"] = "Не удалось получить или изменить время мира.",
            ["command.wind.applied"] = "Скорость ветра установлена на {0} миль/ч.",
            ["command.wind.usage"] = "Использование: wind <скорость от -40 до 40 миль/ч>.",
            ["command.wind.unavailable"] = "Не удалось изменить ветер в мире.",
            ["navigation.spawn"] = "Вы телепортированы на спавн мира.",
            ["navigation.teleported"] = "Игрок {0} телепортирован к {1}.",
            ["navigation.teleported_position"] = "Телепортация к тайлу ({0}, {1}) выполнена.",
            ["navigation.position"] = "Игрок {0} находится в тайле ({1}, {2}).",
            ["navigation.teleports_allowed"] = "Входящие телепортации разрешены.",
            ["navigation.teleports_blocked"] = "Входящие телепортации запрещены.",
            ["navigation.player_not_found"] = "Игрок не найден в этом мире.",
            ["navigation.teleport_disabled"] = "Игрок запретил входящие телепортации.",
            ["navigation.same_player"] = "Исходный и целевой игроки должны различаться.",
            ["navigation.ambiguous"] = "Найдено несколько игроков: {0}.",
            ["navigation.usage"] = "Использование: {0}.",
            ["navigation.failed"] = "Телепортация не выполнена.",
            ["command.permission_denied"] = "Недостаточно прав для этой команды.",
            ["command.not_found"] = "Неизвестная команда."
        });

JsonFileOperationsLogSink? TryCreateFileSink(SandboxBackendLaunchOptions backendLaunch)
{
    try
    {
        string path = backendLaunch.IsBackend
            ? Path.Combine(AppContext.BaseDirectory, "logs", "sandboxes", backendLaunch.WorldId.Value, "vega-operations.jsonl")
            : Path.Combine(AppContext.BaseDirectory, "logs", "vega-operations.jsonl");
        return new JsonFileOperationsLogSink(path);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Vega file logging is unavailable; startup continues: {ex.Message}");
        return null;
    }
}

IReadOnlyList<ModuleOperationsSnapshot> CreateModuleSnapshots(IModuleOperationsCatalog moduleHost) =>
[
    Module(
        "Vega.Runtime",
        Assembly.GetExecutingAssembly(),
        ["Vega.Backups", "Vega.Cluster", "Vega.Core", "Vega.Modules.Administration", "Vega.Modules.AutoWorldCleaner", "Vega.Modules.Navigation", "Vega.Modules.Pvp", "Vega.Modules.Restrictions", "Vega.Operations", "Vega.Persistence", "Vega.Regions", "Vega.Sandboxes", "Vega.Terraria", "Vega.TerminalUI"]),
    Module("Vega.Backups", typeof(DeploymentBackupService).Assembly, ["Vega.Contracts"]),
    Module("Vega.Core", typeof(CommandDispatcher).Assembly, ["Vega.Contracts"]),
    Module("Vega.Modules.Administration", typeof(AccountCommand).Assembly, ["Vega.Contracts"]),
    Module("Vega.Modules.Navigation", typeof(NavigationModule).Assembly, ["Vega.Contracts"]),
    Module("Vega.Modules.Restrictions", typeof(RestrictionStore).Assembly, ["Vega.Contracts", "Multiplicity"]),
    Module("Vega.Cluster", typeof(UnavailableClusterClient).Assembly, ["Vega.Contracts"]),
    Module("Vega.Operations", typeof(OperationsSnapshotProvider).Assembly, ["Vega.Contracts"]),
    Module("Vega.Persistence", typeof(IVegaDatabase).Assembly, ["Vega.Contracts", "System.Data.Common"]),
    Module("Vega.Regions", typeof(RegionCommand).Assembly, ["Vega.Contracts", "Multiplicity"]),
    Module("Vega.Sandboxes", typeof(SandboxControlService).Assembly, ["Vega.Contracts"]),
    Module("Vega.Terraria", typeof(TerrariaServerHost).Assembly, ["Vega.Contracts", "TerrariaRuntime", "Multiplicity"]),
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
