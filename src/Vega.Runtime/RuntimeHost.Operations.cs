using Vega.Core.Commands.Administration;
using System.Reflection;
using Vega.Backups;
using Vega.Cluster.Transport;
using Vega.Cluster.Dispatching;
using Vega.Cluster.Scenarios.Presence;
using Vega.Contracts.Cluster;
using Vega.Contracts.Operations;
using Vega.Core.Commands.System;
using Vega.Core.Modules;
using Vega.Core.Operations;
using Vega.Operations;
using Vega.Operations.Actions;
using Vega.Operations.ReadModels;
using Vega.PluginSdk;
using Vega.Regions;
using Vega.Sandboxes;
using Vega.TerminalUI;
using Vega.Terraria.Operations;
using Vega.Terraria.Worlds;
using Vega.Modules.Navigation;
using static Vega.Runtime.RuntimeCompositionSupport;

namespace Vega.Runtime;

internal static partial class RuntimeHost
{
    private static Operations? CreateOperations(RuntimeResourceScope resources, Launch launch, Foundation foundation, Admission admission, World world, Identity identity)
    {
        var commandDispatcher = foundation.CommandDispatcher;
        var logger = foundation.Logger;
        var serverControl = world.ServerControl;
        var database = identity.Database;
        var restAdministration = new RestAdministration(foundation.RuntimeWorldId, database.Accounts, database.Authorization,
            database.Regions, identity.BanService, database.Mutes, admission.SandboxDirectory, admission.SandboxControl, Path.Combine(admission.SandboxDataRoot, "rest-templates"));
        var pluginRest = new PluginRestRegistry();
        var rest = resources.Own(new LoopbackRestService(foundation.RuntimeWorldId, foundation.Lifecycle, foundation.OperationsState, foundation.PlayerBoundary, foundation.NetworkMetrics, serverControl,
            world.RuntimeMetrics, foundation.GameThreadDispatcher.ReadQueueMetrics, world.WorldEnvironment, logger, restAdministration, pluginRest));
        resources.Own(commandDispatcher.Register(rest));
        if (!launch.BackendLaunch.IsBackend && Environment.GetEnvironmentVariable("VEGA_REST_PORT") is { Length: > 0 } restPort)
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
                return null;
            }
        }
        var worldImages = resources.Own(new TerrariaWorldImageService(logger, foundation.Lifecycle, launch.WorldImageOptions.Enabled));
        var backupBarrier = new TerrariaBackupConsistencyBarrier(serverControl, worldImages);
        BackupOptions backupOptions = BackupOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "backups.json"));
        var backups = resources.Own(launch.BackendLaunch.IsBackend
            ? null
            : new DeploymentBackupService(
                AppContext.BaseDirectory,
                backupOptions,
                backupBarrier,
                database,
                () => backupBarrier.CurrentWorldPath,
                logger));
        resources.Own(backups is null
            ? null
            : commandDispatcher.Register(new BackupCommand(backups)));
        resources.Own(commandDispatcher.Register(
            new SaveCommand(serverControl)));
        resources.Own(commandDispatcher.Register(
            new ShutdownCommand(serverControl)));
        resources.Own(commandDispatcher.Register(new ShutdownCommand(serverControl, save: false)));
        resources.Own(commandDispatcher.Register(new ServerPasswordCommand(serverControl)));
        return new(pluginRest, worldImages, backups);
    }

    private static Plugins? StartPlugins(RuntimeResourceScope resources, Launch launch, Foundation foundation, World world, Identity identity, Characters characters, Operations operations, SoftRestartCoordinator softRestart)
    {
        var runtimeWorldId = foundation.RuntimeWorldId;
        var playerBoundary = foundation.PlayerBoundary;
        var commandDispatcher = foundation.CommandDispatcher;
        var logger = foundation.Logger;
        var worldEvents = world.WorldEvents;
        var database = identity.Database;
        var staticModules = new StaticModuleOperationsCatalog(CreateModuleSnapshots(softRestart));
        ServerPluginHotReloadOptions hotReloadOptions = ServerPluginHotReloadOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "modules.json"),
            logger);
        var pluginCluster = new PluginClusterRegistry();
        AuthenticatedClusterTransport? configuredCluster = null;
        try
        {
            // Sandboxes remain isolated process worlds, and never inherit the primary cluster listener or peer identity.
            var clusterOptions = launch.BackendLaunch.IsBackend ? null : ClusterTransportOptions.Load(
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
            return null;
        }
        var clusterTransport = resources.Own(configuredCluster);
        resources.Own(clusterTransport is null ? null : new ClusterPresenceHeartbeat(clusterTransport, () =>
        {
            var state = foundation.OperationsState.Read();
            return new ClusterPresenceSample(foundation.Lifecycle.CurrentStage, playerBoundary.SnapshotOnlinePlayers().Count,
                state.MaximumPlayers, state.IsWorldReady);
        }, logger));
        resources.Own(new Vega.Regions.RegionObservations(database.Regions, playerBoundary, worldEvents,
            runtimeWorldId, foundation.GameThreadDispatcher, logger));
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(
            runtimeWorldId,
            world.WorldEnvironment,
            world.WorldTiles,
            world.ChestService,
            world.WorldNpcs,
            world.Scenes,
            identity.InventoryService,
            world.Visibility, world.WorldSchematics, characters.WorldNpcActors, Regions: database.Regions));
        var pluginServices = new ServerPluginHostServices(commandDispatcher, logger)
        {
            PacketRates = world.PacketRates,
            Players = new(playerBoundary, playerBoundary, playerBoundary, playerBoundary, foundation.GameThreadDispatcher),
            World = new(runtimeWorldId, worldEvents, identity.Chat,
                new WorldPublicWarpReader(world.WorldEnvironment, world.WarpStore), identity.Playtime),
            Identity = new(identity.AccountSessions, database.Accounts, database.Authorization,
                foundation.PlayerAuthorization, database.RoleLeases),
            Extensions = new(database.PluginData, characters.NpcEconomy, operations.PluginRest,
                Cluster: clusterTransport is null ? null : pluginCluster, ClusterSender: clusterTransport)
        };
        var serverPlugins = resources.Own(new ServerPluginHotReloadHost(
            Path.Combine(AppContext.BaseDirectory, "ServerPlugins"), hotReloadOptions, pluginServices,
            pluginDataRoot: Path.Combine(foundation.RuntimeDataRoot, "plugins"),
            pluginConfigurationRoot: launch.BackendLaunch.IsBackend ? Path.Combine(foundation.RuntimeDataRoot, "plugin-config") : null));
        serverPlugins.Start();
        softRestart.SetProcessRestartGuard(() => serverPlugins.Read().IsRestartRequired
            ? "ServerPlugins contains a change or cleanup failure that requires a process restart."
            : null);
        resources.Own(commandDispatcher.Register(
            new SoftRestartCommand(softRestart, logger)));
        resources.Own(commandDispatcher.Register(
            new ServerPluginCommand(serverPlugins)));
        var modules = new CompositeModuleOperationsCatalog(
            staticModules,
            new HotReloadableModuleOperationsCatalog(serverPlugins));
        return new(clusterTransport, serverPlugins, modules);
    }

    private static void StartDashboard(RuntimeResourceScope resources, Launch launch, Foundation foundation, Admission admission, World world, Identity identity, Operations operations, Plugins plugins)
    {
        var playerBoundary = foundation.PlayerBoundary;
        var operationsState = foundation.OperationsState;
        var lifecycle = foundation.Lifecycle;
        var logger = foundation.Logger;
        var cleanerOptions = world.CleanerOptions;
        var database = identity.Database;
        var clusterTransport = plugins.ClusterTransport;
        var snapshots = new OperationsSnapshotProvider(
            "Vega",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            DateTimeOffset.UtcNow,
            lifecycle,
            playerBoundary,
            operationsState,
            plugins.Modules,
            foundation.LogStore,
            world.RuntimeMetrics,
            foundation.NetworkMetrics,
            operations.WorldImages,
            plugins.ServerPlugins,
            world.WorldClock,
            foundation.ModuleRouter,
            worldContexts: admission.SandboxControl is null
                ? null
                : new SandboxWorldContextCatalog(admission.SandboxControl, operationsState, playerBoundary),
            securitySignals: foundation.SecuritySignals,
            regions: database.Regions,
            bans: identity.BanService,
            mutes: database.Mutes,
            scenes: world.Scenes,
            visibility: world.Visibility,
            backups: operations.Backups,
            localWorldId: foundation.RuntimeWorldId);
        resources.Own(launch.BackendLaunch.IsBackend
            ? new SandboxBackendReporter(launch.BackendLaunch, lifecycle, operationsState, playerBoundary)
            : null);

        var operationsCommands = new OperationsCommandService(
            foundation.CommandDispatcher,
            foundation.Localization,
            foundation.Localization,
            foundation.RuntimeWorldId);
        var startupProgress = resources.Own(new TerrariaStartupProgress(logger));
        var terminalUi = resources.Own(new TerminalUiHost(
            snapshots,
            operationsCommands,
            logger,
            startupProgress,
            startupProgress,
            commandCatalog: foundation.CommandDispatcher));
        startupProgress.Attach(() => terminalUi.IsRunning);
        terminalUi.Started += startupProgress.AcquireConsoleOwnership;
        terminalUi.Stopped += startupProgress.ReleaseConsoleOwnership;
        resources.Own(new TerminalUiReadinessGate(
            lifecycle,
            launch.UiOptions.EffectiveMode,
            terminalUi.TryStart));
        logger.Log(
            OperationsLogLevel.Information,
            "operations",
            $"Operations mode: {launch.UiOptions.EffectiveMode}; bounded log capacity: {foundation.LogStore.Capacity}.");
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
            $"World image mode: {(launch.WorldImageOptions.Enabled ? "on" : "off")}; " +
            $"direct_image={launch.WorldImageOptions.DirectImageRequested.ToString().ToLowerInvariant()}.");
        logger.Log(
            OperationsLogLevel.Information,
            "world-maintenance",
            $"Autosave: {(world.WorldMaintenanceOptions.AutoSave.Enabled ? "on" : "off")} " +
            $"every {world.WorldMaintenanceOptions.AutoSave.IntervalMinutes} min.");
        logger.Log(
            OperationsLogLevel.Information,
            "auto-world-cleaner",
            $"AutoWorldCleaner: {(cleanerOptions.Enabled ? "on" : "off")} " +
            $"every {cleanerOptions.IntervalMinutes} min; " +
            $"items={cleanerOptions.Items.Enabled.ToString().ToLowerInvariant()}, " +
            $"projectiles={cleanerOptions.Projectiles.Enabled.ToString().ToLowerInvariant()}.");
    }

    private sealed record Operations(
        PluginRestRegistry PluginRest,
        TerrariaWorldImageService WorldImages,
        DeploymentBackupService? Backups);
    private sealed record Plugins(
        AuthenticatedClusterTransport? ClusterTransport,
        ServerPluginHotReloadHost ServerPlugins,
        CompositeModuleOperationsCatalog Modules);
}
