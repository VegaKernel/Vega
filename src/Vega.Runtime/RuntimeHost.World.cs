using Vega.Core.Operations;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Contracts.Sandboxes;
using Vega.Core.Bans;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Configuration;
using Vega.Core.Events;
using Vega.Core.Modules;
using Vega.Core.Scenes;
using Vega.Core.Worlds;
using Vega.Modules.Administration;
using Vega.Modules.AutoWorldCleaner;
using Vega.Modules.Chests;
using Vega.Modules.Mentions;
using Vega.Modules.Navigation;
using Vega.Modules.Restrictions;
using Vega.Modules.Pvp;
using Vega.Modules.Scenes;
using Vega.Modules.Wipe;
using Vega.Operations.Metrics;
using Vega.Regions;
using Vega.Sandboxes;
using Vega.Terraria.Networking;
using Vega.Terraria.Operations;
using Vega.Terraria.Players;
using Vega.Terraria.Scenes;
using Vega.Terraria.Worlds;

namespace Vega.Runtime;

internal static partial class RuntimeHost
{
    private static Admission? CreateAdmission(RuntimeResourceScope resources, Launch launch, Foundation foundation)
    {
        var backendLaunch = launch.BackendLaunch;
        var playerBoundary = foundation.PlayerBoundary;
        var logger = foundation.Logger;
        ConnectionWhitelist whitelist;
        try { whitelist = new ConnectionWhitelist(Path.Combine(AppContext.BaseDirectory, "data", "whitelist.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.Log(OperationsLogLevel.Critical, "admission", "Invalid whitelist configuration; refusing to start.", ex);
            return null;
        }
        resources.Own(foundation.CommandDispatcher.Register(new WhitelistCommand(whitelist)));
        RestrictionStore restrictions;
        try { restrictions = new RestrictionStore(Path.Combine(foundation.RuntimeDataRoot, "restrictions.json"),
            TerrariaPacketSecurityLimits.ItemTypeCount, TerrariaPacketSecurityLimits.ProjectileTypeCount, TerrariaPacketSecurityLimits.TileTypeCount); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.Log(OperationsLogLevel.Critical, "restrictions", "Invalid content restriction configuration; refusing to start.", ex);
            return null;
        }
        var restrictionPolicy = new ContentRestrictionPolicy(restrictions, foundation.PlayerAuthorization, playerBoundary);
        playerBoundary.ConfigureContentRestrictions(restrictionPolicy);
        SandboxOptions sandboxOptions = SandboxOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "sandboxes.json"));
        string sandboxDataRoot = Path.Combine(AppContext.BaseDirectory, "data", "sandboxes");
        string sandboxSscPath = backendLaunch.IsBackend && !string.IsNullOrWhiteSpace(backendLaunch.SscStorePath)
            ? backendLaunch.SscStorePath
            : Path.Combine(sandboxDataRoot, "ssc.json");
        var sandboxSscStore = new SandboxSscStore(sandboxSscPath);
        var sandboxCharacterAdapter = new TerrariaSandboxSscCharacterAdapter(foundation.GameThreadDispatcher, playerBoundary);
        var sandboxControl = resources.Own(backendLaunch.IsBackend
            ? null
            : new SandboxControlService(
                AppContext.BaseDirectory, sandboxOptions, logger, sscStore: sandboxSscStore));
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
                int.TryParse(launch.ServerConfiguration.Current.Values["port"].DisplayValue, out int primaryPort)
                    ? primaryPort
                    : 7777),
            sandboxSscStore);
        return new(whitelist, restrictions, restrictionPolicy, sandboxDataRoot, sandboxSscStore, sandboxCharacterAdapter, sandboxControl, sandboxDirectory, sandboxAdmissions, sandboxRouting);
    }

    private static World? CreateWorld(RuntimeResourceScope resources, Launch launch, Foundation foundation, Admission admission)
    {
        var gameThreadDispatcher = foundation.GameThreadDispatcher;
        var runtimeWorldId = foundation.RuntimeWorldId;
        var runtimeDataRoot = foundation.RuntimeDataRoot;
        var playerBoundary = foundation.PlayerBoundary;
        var commandDispatcher = foundation.CommandDispatcher;
        var logger = foundation.Logger;
        var sceneRenderer = resources.Own(new TerrariaSceneRenderSink(
            gameThreadDispatcher,
            playerBoundary,
            logger,
            runtimeWorldId));
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
        var worldMaintenance = resources.Own(new TerrariaWorldMaintenanceService(
            worldMaintenanceOptions,
            logger,
            gameThreadDispatcher));
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
        var worldReset = resources.Own(new WorldResetOrchestrator(
            new FileWorldResetBackend(),
            [regionReset],
            logger));
        AutoWorldCleanerOptions cleanerOptions = AutoWorldCleanerOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "auto-world-cleaner.json"),
            logger);
        var worldEntities = new TerrariaWorldEntityMaintenance(gameThreadDispatcher, runtimeWorldId);
        var worldTiles = resources.Own(new TerrariaWorldTileService(gameThreadDispatcher, runtimeWorldId, foundation.Lifecycle));
        var worldNpcs = new TerrariaWorldNpcService(gameThreadDispatcher, runtimeWorldId, playerBoundary);
        var worldEvents = new WorldGameplayEventHub();
        var packetRates = new TerrariaInboundPacketRates(worldEvents, foundation.NetworkMetrics);
        var worldSchematics = new SchematicSnapshotService(runtimeWorldId, new TerrariaSchematicCapture(gameThreadDispatcher), gameThreadDispatcher,
            new TerrariaSchematicPlacement(gameThreadDispatcher, runtimeWorldId).Place);
        WorldBuildProtection buildProtection;
        try { buildProtection = new WorldBuildProtection(Path.Combine(runtimeDataRoot, "build-protection.json"), runtimeWorldId, foundation.PlayerAuthorization, worldEnvironment.GetSpawnPosition); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            logger.Log(OperationsLogLevel.Critical, "build-protection", "Build protection settings are invalid.", ex);
            return null;
        }
        resources.Own(commandDispatcher.Register(new WorldBuildProtectionCommand(buildProtection, spawn: false)));
        resources.Own(commandDispatcher.Register(new WorldBuildProtectionCommand(buildProtection, spawn: true)));
        var reloadConfiguration = new ReloadConfigurationCommand(launch.ServerConfiguration, runtimeWorldId,
            [("shared admission.Whitelist", admission.Whitelist.ReloadAsync), ("admission.Restrictions", admission.Restrictions.ReloadAsync), ("build-protection", buildProtection.ReloadAsync)]);
        resources.Own(commandDispatcher.Register(reloadConfiguration));
        resources.Own(commandDispatcher.Register(new ConfigurationCommand(launch.ServerConfiguration, reloadConfiguration)));
        var warpStore = new WarpStore(Path.Combine(runtimeDataRoot, "warps"));
        var navigationModule = resources.Own(new NavigationModule(commandDispatcher, playerBoundary, playerBoundary, playerBoundary,
            worldEnvironment, warpStore, worldNpcs, playerBoundary));
        navigationModule.Start();
        resources.Own(RuntimeCommandRegistration.RegisterWorld(commandDispatcher, playerBoundary, worldEnvironment,
            worldNpcs, worldEntities, gameThreadDispatcher, runtimeWorldId, runtimeDataRoot));
        var chestBackend = new TerrariaChestBackend(gameThreadDispatcher, runtimeWorldId);
        var chestService = new ChestService(
            chestBackend,
            new AsyncEventPipeline<ChestChanging>([]),
            new AsyncEventPipeline<ChestChanged>([]));
        return new(sceneRenderer, scenes, visibility, worldMaintenanceOptions, worldMaintenance, serverControl, runtimeMetrics, worldClock, worldEnvironment, regionReset, worldReset, cleanerOptions, worldEntities, worldTiles, worldNpcs, worldEvents, packetRates, worldSchematics, buildProtection, warpStore, chestService);
    }

    private static SoftRestartCoordinator? StartModules(RuntimeResourceScope resources, Launch launch, Foundation foundation, World world, string configuredWorldPath)
    {
        var backendLaunch = launch.BackendLaunch;
        var gameThreadDispatcher = foundation.GameThreadDispatcher;
        var playerBoundary = foundation.PlayerBoundary;
        var moduleRouter = foundation.ModuleRouter;
        var logger = foundation.Logger;
        SoftRestartOptions softRestartOptions = SoftRestartOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "soft-restart.json"));
        RestartableModuleGraph CreateRestartableGraph(IReadOnlySet<string>? requested)
        {
            var pvp = new PvpModule(foundation.ExtensionState, playerBoundary, playerBoundary, playerBoundary);
            var cleaner = new AutoWorldCleanerModule(
                world.CleanerOptions,
                world.WorldEntities,
                foundation.Lifecycle,
                gameThreadDispatcher,
                playerBoundary,
                logger,
                worldId: foundation.RuntimeWorldId);
            var mentions = new MentionRenderer();
            var chests = new ChestPolicyModule(
                world.ChestService,
                logger,
                Path.Combine(foundation.RuntimeDataRoot, "chest-templates.json"));
            var wipe = new WipeModule(
                world.WorldReset,
                world.ServerControl,
                configuredWorldPath.StartsWith('<') ? null : configuredWorldPath);
            var sceneConsumers = new SceneConsumersModule(world.Scenes, world.Visibility, playerBoundary, foundation.RuntimeWorldId);
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
                moduleCommands.Add(new SceneCommand(sceneConsumers, world.Scenes, playerBoundary));
                moduleCommands.Add(new SchematicCommand(world.WorldSchematics));
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
            return null;
        }

        var softRestart = resources.Own(startedModules);
        moduleRouter.Attach(softRestart);
        resources.Own(moduleRouter.RegisterCommands(foundation.CommandDispatcher));
        return softRestart;
    }

    private sealed record Admission(
        ConnectionWhitelist Whitelist,
        RestrictionStore Restrictions,
        ContentRestrictionPolicy RestrictionPolicy,
        string SandboxDataRoot,
        SandboxSscStore SandboxSscStore,
        TerrariaSandboxSscCharacterAdapter SandboxCharacterAdapter,
        SandboxControlService? SandboxControl,
        ISandboxDirectory SandboxDirectory,
        SandboxTransferAdmissionService SandboxAdmissions,
        SandboxRoutingService SandboxRouting);
    private sealed record World(
        TerrariaSceneRenderSink SceneRenderer,
        SceneRuntime Scenes,
        SceneVisibilityRuntime Visibility,
        WorldMaintenanceOptions WorldMaintenanceOptions,
        TerrariaWorldMaintenanceService WorldMaintenance,
        TerrariaServerControl ServerControl,
        SampledRuntimeMetricsReader RuntimeMetrics,
        MonotonicWorldClock WorldClock,
        TerrariaWorldEnvironmentService WorldEnvironment,
        BindableWorldResetParticipant RegionReset,
        WorldResetOrchestrator WorldReset,
        AutoWorldCleanerOptions CleanerOptions,
        TerrariaWorldEntityMaintenance WorldEntities,
        TerrariaWorldTileService WorldTiles,
        TerrariaWorldNpcService WorldNpcs,
        WorldGameplayEventHub WorldEvents,
        TerrariaInboundPacketRates PacketRates,
        SchematicSnapshotService WorldSchematics,
        WorldBuildProtection BuildProtection,
        WarpStore WarpStore,
        ChestService ChestService);
}
