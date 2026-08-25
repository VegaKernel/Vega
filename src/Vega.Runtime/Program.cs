using System.Diagnostics;
using System.Reflection;
using Multiplicity.Packets.Views;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
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
    using IDisposable helpCommandRegistration = commandDispatcher.Register(new HelpCommand(commandDispatcher));
    using IDisposable aliasesCommandRegistration = commandDispatcher.Register(new AliasesCommand(commandDispatcher));
    using IDisposable configCommandRegistration = commandDispatcher.Register(
        new ConfigurationCommand(serverConfiguration));
    var lifecycle = new RuntimeLifecycleTracker(processStartedAt);
    var networkMetrics = new NetworkMetricsCounter();
    var securitySignals = new BoundedSecuritySignalStore();
    var logStore = new BoundedOperationsLogStore();
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
        using var logger = new StructuredOperationsLogger(sinks);
        SandboxOptions sandboxOptions = SandboxOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "sandboxes.json"));
        string sandboxDataRoot = Path.Combine(AppContext.BaseDirectory, "data", "sandboxes");
        string sandboxSscPath = backendLaunch.IsBackend && !string.IsNullOrWhiteSpace(backendLaunch.SscStorePath)
            ? backendLaunch.SscStorePath
            : Path.Combine(sandboxDataRoot, "ssc.json");
        var sandboxSscStore = new SandboxSscStore(sandboxSscPath);
        var sandboxCharacterAdapter = new TerrariaSandboxSscCharacterAdapter(gameThreadDispatcher);
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
            logger);
        var scenes = new SceneRuntime(sceneRenderer);
        var visibility = new SceneVisibilityRuntime();
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
        var regionReset = new BindableWorldResetParticipant("regions");
        using var worldReset = new WorldResetOrchestrator(
            new FileWorldResetBackend(),
            [regionReset],
            logger);
        AutoWorldCleanerOptions cleanerOptions = AutoWorldCleanerOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "auto-world-cleaner.json"),
            logger);
        var worldEntities = new TerrariaWorldEntityMaintenance(gameThreadDispatcher);
        var worldTiles = new TerrariaWorldTileService(gameThreadDispatcher, runtimeWorldId);
        var worldNpcs = new TerrariaWorldNpcService(gameThreadDispatcher, runtimeWorldId);
        var worldEnvironment = new TerrariaWorldEnvironmentService(gameThreadDispatcher, runtimeWorldId);
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
                logger);
            var mentions = new MentionRenderer();
            var chests = new ChestPolicyModule(
                chestService,
                logger,
                Path.Combine(AppContext.BaseDirectory, "data", "chest-templates.json"));
            var wipe = new WipeModule(
                worldReset,
                serverControl,
                configuredWorldPath.StartsWith('<') ? null : configuredWorldPath);
            var sceneConsumers = new SceneConsumersModule(scenes, visibility, playerBoundary);
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
            if (Enabled(sceneConsumers)) moduleCommands.Add(new SceneCommand(sceneConsumers, scenes, playerBoundary));
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
            database.RoleLeases,
            database.AccountIdentities,
            playerBoundary,
            logger);
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
        var inventoryService = new TerrariaPlayerInventoryService(gameThreadDispatcher);
        var inspectionService = new PlayerInspectionService(playerBoundary, inventoryService);
        using IDisposable userInfoCommandRegistration = commandDispatcher.Register(
            new UserInfoCommand(playerBoundary, inspectionService));
        using IDisposable inventoryCommandRegistration = commandDispatcher.Register(
            new InventoryCommand(playerBoundary, inventoryService));
        var chat = new ChatPipeline(
            moderation: [new MuteChatModerationRule(accountSessions, database.Mutes)],
            renderers: [moduleRouter]);

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
        using var sandboxSscSessions = new SandboxSscSessionCoordinator(
            playerBoundary,
            playerBoundary,
            accountSessions,
            playerBoundary,
            sandboxSscStore,
            sandboxCharacterAdapter,
            runtimeWorldId,
            backendLaunch.IsBackend ? backendLaunch.SscMode : SandboxSscMode.Inherit,
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
            new PlayerPacketOwnershipPolicy(),
            new PlayerActionSanityPolicy(
                TerrariaPacketSecurityLimits.PlayerCount,
                TerrariaPacketSecurityLimits.NpcCount,
                TerrariaPacketSecurityLimits.BuffTypeCount,
                TerrariaPacketSecurityLimits.ProjectileTypeCount,
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight),
            new NpcPacketSanityPolicy(
                TerrariaPacketSecurityLimits.NpcCount,
                TerrariaPacketSecurityLimits.NpcTypeCount,
                TerrariaPacketSecurityLimits.BuffTypeCount,
                TerrariaPacketSecurityLimits.PlayerCount,
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight),
            new TeleportSanityPolicy(
                TerrariaPacketSecurityLimits.NpcCount,
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight),
            new ChestInteractionAuthorityPolicy(playerBoundary),
            new TileEntityInteractionAuthorityPolicy(playerBoundary),
            new SceneInteractionAuthorityPolicy(scenes),
            new SceneEntityInteractionAuthorityPolicy(visibility),
            new RegionBuildAuthorizationPolicy(
                database.Regions,
                playerBoundary,
                playerAuthorization,
                accountSessions,
                playerBoundary),
            new NpcCoinPickupAuthorityPolicy(),
            new WorldItemSyncSanityPolicy(
                TerrariaPacketSecurityLimits.ItemTypeCount,
                TerrariaPacketSecurityLimits.PrefixCount),
            new ProjectileSpawnSanityPolicy(
                TerrariaPacketSecurityLimits.ProjectileCount,
                TerrariaPacketSecurityLimits.ProjectileTypeCount),
            new TerrariaProjectileExploitPolicy(),
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
                TerrariaPacketSecurityLimits.PrefixCount,
                TerrariaPacketSecurityLimits.PaintColorCount),
            new WorldInteractionSanityPolicy(
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight,
                TerrariaPacketSecurityLimits.ItemTypeCount,
                TerrariaPacketSecurityLimits.PrefixCount,
                TerrariaPacketSecurityLimits.ProjectileTypeCount),
            new TerrariaNpcGenerationPolicy(),
            new TerrariaPortalAuthorityPolicy(),
            new TerrariaTileRectanglePayloadPolicy(),
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
                sandboxCharacterAdapter),
            playerBoundary,
            playerAuthorization,
            accountSessions));

        using IDisposable banCommandRegistration = commandDispatcher.Register(
            new BanCommand(playerBoundary, playerBoundary, banService, accountSessions: accountSessions));
        using IDisposable muteCommandRegistration = commandDispatcher.Register(
            new MuteCommand(playerBoundary, accountSessions, database.Mutes));
        using IDisposable accountCommandRegistration = commandDispatcher.Register(
            new AccountCommand(database.Accounts, database.Authorization, database.RoleLeases));
        using IDisposable accountInfoCommandRegistration = commandDispatcher.Register(
            new AccountInfoCommand(database.Accounts, database.AccountIdentities));
        using IDisposable groupCommandRegistration = commandDispatcher.Register(
            new GroupCommand(database.Authorization));
        using IDisposable permissionInspectionCommandRegistration = PermissionInspectionCommandRegistration.Register(
            commandDispatcher,
            commandDispatcher,
            database.Authorization,
            database.Accounts);
        using IDisposable regionCommandRegistration = commandDispatcher.Register(
            new RegionCommand(database.Regions));
        using var banEnforcer = new PersistentBanEnforcer(
            banService,
            playerBoundary,
            playerBoundary,
            playerBoundary,
            logger);
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
        var hostMetrics = new ProcessHostMetricsReader();
        var runtimeMetrics = new SampledRuntimeMetricsReader(hostMetrics);
        WorldClockOptions worldClockOptions = WorldClockOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "world-clock.json"),
            logger);
        var worldClock = new MonotonicWorldClock(worldClockOptions);
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
            inventoryService));
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
            pluginDataRoot: Path.Combine(AppContext.BaseDirectory, "data", "plugins"));
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
            accounts: database.Accounts,
            authorization: database.Authorization,
            accountIdentities: database.AccountIdentities);
        using var backendReporter = backendLaunch.IsBackend
            ? new SandboxBackendReporter(backendLaunch, lifecycle, operationsState, playerBoundary)
            : null;

        var operationsCommands = new OperationsCommandService(
            commandDispatcher,
            localization,
            localization);
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
            localization);
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
            ["command.permission_denied"] = "Недостаточно прав для этой команды.",
            ["command.not_found"] = "Неизвестная команда."
        });

JsonFileOperationsLogSink? TryCreateFileSink(SandboxBackendLaunchOptions backendLaunch)
{
    try
    {
        string path = backendLaunch.IsBackend
            ? Path.Combine("logs", "sandboxes", backendLaunch.WorldId.Value, "vega-operations.jsonl")
            : Path.Combine("logs", "vega-operations.jsonl");
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
        ["Vega.Backups", "Vega.Cluster", "Vega.Core", "Vega.Modules.Administration", "Vega.Modules.AutoWorldCleaner", "Vega.Modules.Pvp", "Vega.Operations", "Vega.Persistence", "Vega.Regions", "Vega.Sandboxes", "Vega.Terraria", "Vega.TerminalUI"]),
    Module("Vega.Backups", typeof(DeploymentBackupService).Assembly, ["Vega.Contracts"]),
    Module("Vega.Core", typeof(CommandDispatcher).Assembly, ["Vega.Contracts"]),
    Module("Vega.Modules.Administration", typeof(AccountCommand).Assembly, ["Vega.Contracts"]),
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
