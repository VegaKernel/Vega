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
using Vega.Cluster.Dispatching;
using Vega.Cluster.Scenarios.Presence;
using Vega.Contracts.Cluster;
using Vega.Core.Events;
using Vega.Core.Extensions;
using Vega.Core.Networking;
using Vega.Modules.AutoWorldCleaner;
using Vega.Modules.Chests;
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
using Vega.Terraria.Networking;
using Vega.Terraria.Players;
using Vega.Terraria.Operations;
using Vega.Terraria.Threading;
using Vega.Terraria.Scenes;
using Vega.Terraria.Worlds;
using Vega.Runtime;
using static Vega.Runtime.RuntimeCompositionSupport;

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
    if (OfflineSchematicTemplateCommand.TryRun(args) is { } schematicExitCode) return schematicExitCode;

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
    using IDisposable platformRussianCatalog = PlatformCommand.RegisterRussianCatalog(localization);
    using IDisposable userInfoRussianCatalog = UserInfoCommand.RegisterRussianCatalog(localization);
    using IDisposable playtimeRussianCatalog = PlaytimeCommand.RegisterRussianCatalog(localization);

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
    operationsState.LowTickRate += tps =>
    {
        foreach (var player in playerBoundary.SnapshotOnlinePlayers())
            if (playerAuthorization.HasPermission(player.Id, "tickrate.admin"))
                _ = playerBoundary.SendMessageAsync(player, $"[WARNING] Low TPS detected: {tps:F2}");
    };
    var moduleRouter = new RuntimeModuleGenerationRouter();
    ICommandHandler[] commandHandlers =
    [
        new ServerInfoCommand(new ProcessHostMetricsReader()),
        new TickRateCommand(operationsState, operationsState.RequestTickRateReset, operationsState.ToggleTickRateAlerts),
        new CheckUpdatesCommand(updateChecker),
        new KickCommand(playerBoundary, playerBoundary, playerAuthorization),
        new PlayingCommand(playerBoundary)
    ];
    var commandDispatcher = new CommandDispatcher(commandHandlers);
    using var playerCommandRegistrations = RuntimeCommandRegistration.RegisterPlayer(commandDispatcher, playerBoundary, runtimeDataRoot);
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
        var regionReset = new BindableWorldResetParticipant("regions");
        using var worldReset = new WorldResetOrchestrator(
            new FileWorldResetBackend(),
            [regionReset],
            logger);
        AutoWorldCleanerOptions cleanerOptions = AutoWorldCleanerOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "auto-world-cleaner.json"),
            logger);
        var worldEntities = new TerrariaWorldEntityMaintenance(gameThreadDispatcher, runtimeWorldId);
        using var worldTiles = new TerrariaWorldTileService(gameThreadDispatcher, runtimeWorldId, lifecycle);
        var worldNpcs = new TerrariaWorldNpcService(gameThreadDispatcher, runtimeWorldId, playerBoundary);
        var worldEvents = new WorldGameplayEventHub();
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
        var warpStore = new WarpStore(Path.Combine(runtimeDataRoot, "warps"));
        using var navigationModule = new NavigationModule(commandDispatcher, playerBoundary, playerBoundary, playerBoundary,
            worldEnvironment, warpStore, worldNpcs, playerBoundary);
        navigationModule.Start();
        using var worldCommandRegistrations = RuntimeCommandRegistration.RegisterWorld(commandDispatcher, playerBoundary, worldEnvironment,
            worldNpcs, worldEntities, gameThreadDispatcher, runtimeWorldId, runtimeDataRoot);
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
            IVegaModule[] candidates = [pvp, cleaner, mentions, chests, wipe, sceneConsumers];
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
        using var playtime = new PlayerPlaytimeService(playerBoundary, playerBoundary, playerBoundary,
            accountSessions, database.Playtime, runtimeWorldId, logger);
        using IDisposable setHomeRegistration = commandDispatcher.Register(new HomeCommand(HomeCommandAction.Set,
            playerBoundary, playerBoundary, worldEnvironment, accountSessions, database.Authorization, database.Homes));
        using IDisposable deleteHomeRegistration = commandDispatcher.Register(new HomeCommand(HomeCommandAction.Delete,
            playerBoundary, playerBoundary, worldEnvironment, accountSessions, database.Authorization, database.Homes));
        using IDisposable myHomeRegistration = commandDispatcher.Register(new HomeCommand(HomeCommandAction.Teleport,
            playerBoundary, playerBoundary, worldEnvironment, accountSessions, database.Authorization, database.Homes));
        using IDisposable playtimeRegistration = commandDispatcher.Register(new PlaytimeCommand(playerBoundary, playtime));
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
        using IDisposable userInfoCommandRegistration = commandDispatcher.Register(
            new UserInfoCommand(playerBoundary, accountSessions, database.RoleLeases));
        using IDisposable inventoryCommandRegistration = commandDispatcher.Register(
            new InventoryCommand(playerBoundary, inventoryService));
        var chat = new ChatPipeline(
            moderation: [new MuteChatModerationRule(accountSessions, database.Mutes)],
            renderers: [moduleRouter],
            presentation: player => accountSessions.TryGetBinding(player, out var binding) &&
                accountSessions.TryRunWithBinding(binding, () => playerAuthorization.GetChatPresentation(player.Id), out var style) ? style : null);
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
        if (!backendLaunch.IsBackend && primaryCharacterOptions.Enabled && File.Exists(Path.Combine(runtimeDataRoot, "primary-characters.json")))
        {
            logger.Log(OperationsLogLevel.Critical, "character-persistence",
                "A pre-release primary-characters.json exists. The release uses only the shared SSC store. Preserve the old file and export it with the matching development version if its data is needed.");
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
            primaryCharacterStore: !backendLaunch.IsBackend && primaryCharacterOptions.Enabled ? sandboxSscStore : null);
        var npcAccountStore = new FileNpcAccountStore(Path.Combine(runtimeDataRoot, "npc-account-progress"));
        var npcEconomy = new PluginNpcEconomyRegistry(npcAccountStore);
        using var worldNpcActors = new TerrariaNpcActorService(gameThreadDispatcher, runtimeWorldId, playerBoundary, worldEvents, logger,
            accountSessions, npcAccountStore, npcEconomy, authoritativeCharacters ? sandboxSscSessions.IsCharacterReady : null,
            sandboxSscSessions.CheckpointActorTransactionAsync);
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
            new SceneManagedChestGuiPolicy(scenes, runtimeWorldId, playerBoundary),
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
            moduleRouter,
            new PlayerGameplayIntentPolicy(playerBoundary, playerBoundary, worldEvents, gameThreadDispatcher, runtimeWorldId,
                player => playerBoundary.TryGetSessionPlayer(player, out var native)
                    ? new CurrentPlayerIntentState(new PlayerPosition(native.position.X, native.position.Y), native.hostile, native.team) : null, logger)
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
            new BanCommand(playerBoundary, playerBoundary, banService, accountSessions: accountSessions, accounts: database.Accounts));
        using IDisposable muteCommandRegistration = commandDispatcher.Register(
            new MuteCommand(playerBoundary, accountSessions, database.Mutes, accounts: database.Accounts, groups: database.Authorization,
                playerAuthorization: playerAuthorization, roleLeases: database.RoleLeases));
        string groupOptionsPath = Path.Combine(AppContext.BaseDirectory, "data", "ControlGroupOption.json");
        var groupOptions = File.Exists(groupOptionsPath)
            ? System.Text.Json.JsonSerializer.Deserialize<GroupAssignmentOptions>(File.ReadAllText(groupOptionsPath))
                ?? throw new InvalidDataException("ControlGroupOption.json is empty.")
            : null;
        if (groupOptions is not null && (groupOptions.Groups is null || groupOptions.Groups.Any(entry => entry.Value is null)))
            throw new InvalidDataException("ControlGroupOption.json must contain a valid Groups object.");
        var accountCommand = new AccountCommand(database.Accounts, database.Authorization, database.RoleLeases, groupAssignments: groupOptions?.Groups);
        using IDisposable setupRegistration = commandDispatcher.Register(new SetupCommand());
        using IDisposable accountCommandRegistration = commandDispatcher.Register(accountCommand);
        using IDisposable accountInfoRegistration = commandDispatcher.Register(new AccountInfoCommand(accountCommand));
        using IDisposable tempGroupRegistration = commandDispatcher.Register(new TempGroupCommand(accountCommand));
        using IDisposable tempStatusRegistration = commandDispatcher.Register(new TempStatusCommand(accountCommand));
        using IDisposable groupCommandRegistration = commandDispatcher.Register(
            new GroupCommand(database.Authorization));
        using IDisposable itemBanRegistration = commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Item,
            name => TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Item, name), database.Authorization));
        using IDisposable projectileBanRegistration = commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Projectile,
            name => TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Projectile, name), database.Authorization));
        using IDisposable tileBanRegistration = commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Tile,
            name => TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Tile, name), database.Authorization));
        using IDisposable regionCommandRegistration = commandDispatcher.Register(
            new RegionCommand(database.Regions, playerBoundary, playerBoundary, database.Accounts, database.Authorization, worldEnvironment, accountSessions));
        using var banEnforcer = new PersistentBanEnforcer(
            banService,
            playerBoundary,
            playerBoundary,
            playerBoundary,
            logger,
            accounts: accountSessions,
            dispatcher: gameThreadDispatcher);
        var restAdministration = new RestAdministration(runtimeWorldId, database.Accounts, database.Authorization,
            database.Regions, banService, database.Mutes, sandboxDirectory, sandboxControl, Path.Combine(sandboxDataRoot, "rest-templates"));
        var pluginRest = new PluginRestRegistry();
        using var rest = new LoopbackRestService(runtimeWorldId, lifecycle, operationsState, playerBoundary, networkMetrics, serverControl,
            runtimeMetrics, gameThreadDispatcher.ReadQueueMetrics, worldEnvironment, logger, restAdministration, pluginRest);
        using IDisposable restRegistration = commandDispatcher.Register(rest);
        if (!backendLaunch.IsBackend && Environment.GetEnvironmentVariable("VEGA_REST_PORT") is { Length: > 0 } restPort)
        {
            try
            {
                if (!int.TryParse(restPort, out int port)) throw new InvalidDataException("Invalid REST port.");
                rest.Start(port, Environment.GetEnvironmentVariable("VEGA_REST_TOKEN_SHA256") ?? "",
                    Environment.GetEnvironmentVariable("VEGA_REST_MAINTENANCE_TOKEN_SHA256"),
                    Environment.GetEnvironmentVariable("VEGA_REST_ADMIN_TOKEN_SHA256"));
                logger.Log(OperationsLogLevel.Information, "rest", $"Scoped REST available on loopback port {port}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.HttpListenerException or UnauthorizedAccessException or ArgumentException)
            {
                logger.Log(OperationsLogLevel.Critical, "rest", "Could not start configured loopback REST listener.", ex);
                return 2;
            }
        }
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
        var pluginCluster = new PluginClusterRegistry();
        AuthenticatedClusterTransport? configuredCluster = null;
        try
        {
            // Sandboxes remain isolated process worlds, and never inherit the primary cluster listener or peer identity.
            var clusterOptions = backendLaunch.IsBackend ? null : ClusterTransportOptions.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "cluster.json"));
            if (clusterOptions is not null)
            {
                var clusterDispatcher = new ClusterMessageDispatcher(new ClusterMessageDeduplicator(retention: TimeSpan.FromMinutes(3)));
                clusterDispatcher.RegisterEvent(pluginCluster);
                configuredCluster = new AuthenticatedClusterTransport(clusterOptions, clusterDispatcher);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or System.Text.Json.JsonException or System.Net.HttpListenerException or UnauthorizedAccessException)
        {
            logger.Log(OperationsLogLevel.Critical, "cluster", "Configured cluster transport could not start. Check cluster.json, local listener and peer token environment variables.");
            return 2;
        }
        using var clusterTransport = configuredCluster;
        using var clusterPresence = clusterTransport is null ? null : new ClusterPresenceHeartbeat(clusterTransport, () =>
        {
            var state = operationsState.Read();
            return new ClusterPresenceSample(lifecycle.CurrentStage, playerBoundary.SnapshotOnlinePlayers().Count,
                state.MaximumPlayers, state.IsWorldReady);
        }, logger);
        using var regionObservations = new Vega.Regions.RegionObservations(database.Regions, playerBoundary, worldEvents,
            runtimeWorldId, gameThreadDispatcher, logger);
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(
            runtimeWorldId,
            worldEnvironment,
            worldTiles,
            chestService,
            worldNpcs,
            scenes,
            inventoryService,
            visibility, worldSchematics, worldNpcActors, Regions: database.Regions));
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
            worldEvents: worldEvents, activityWorldId: runtimeWorldId,
            authentication: accountSessions, publicChat: chat,
            accounts: database.Accounts, groups: database.Authorization, playerPermissions: playerAuthorization,
            warps: new WorldPublicWarpReader(worldEnvironment, warpStore), playtime: playtime, pluginDataStore: database.PluginData,
            roleLeases: database.RoleLeases, npcEconomyRegistry: npcEconomy, pluginRestRegistry: pluginRest,
            pluginClusterRegistry: clusterTransport is null ? null : pluginCluster, clusterSender: clusterTransport);
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
            clusterTransport is null ? "Cluster transport disabled; standalone operation remains enabled."
                : $"Cluster notifications and presence available as {clusterTransport.LocalServerId}, {clusterTransport.Peers.Count} configured peers.");
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
