using Vega.Core.Operations;
using Vega.Core.Commands.Administration;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.Players;
using Vega.Core.Commands.System;
using Vega.Core.Authorization;
using Vega.Core.Configuration;
using Vega.Core.Localization;
using Vega.Core.Events;
using Vega.Core.Extensions;
using Vega.Core.Updates;
using Vega.Core.Worlds;
using Vega.Operations;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Sandboxes;
using Vega.TerminalUI;
using Vega.Terraria;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;
using Vega.Terraria.Operations;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;
using static Vega.Runtime.RuntimeCompositionSupport;

namespace Vega.Runtime;

internal static partial class RuntimeHost
{
    internal static int Run(string[] args, long processStartedAt)
    {
        if (OfflineMigrationCommands.TryRun(args, AppContext.BaseDirectory) is { } migrationExitCode) return migrationExitCode;
        if (OfflineSchematicTemplateCommand.TryRun(args) is { } schematicExitCode) return schematicExitCode;
        if (ReadLaunch(args) is not { } launch) return 2;

        string[] serverArgs = launch.TileStorageOptions.ServerArguments.ToArray();
        using var tileStorage = launch.TileStorageOptions.Enabled ? new NativeTileStorageScope() : null;
        TerrariaServerHost.PrepareSavePath(serverArgs);
        if (!PrepareWorld(launch, tileStorage, out string configuredWorldPath)) return 2;

        // Every successfully created resource is released in the original reverse construction order.
        using var resources = new RuntimeResourceScope();
        var foundation = CreateFoundation(resources, launch, processStartedAt);
        if (CreateAdmission(resources, launch, foundation) is not { } admission) return 2;
        if (CreateWorld(resources, launch, foundation, admission) is not { } world) return 2;
        if (StartModules(resources, launch, foundation, world, configuredWorldPath) is not { } softRestart) return 1;
        if (CreateIdentity(resources, foundation, world) is not { } identity) return 1;
        if (CreateCharacters(resources, launch, foundation, admission, world, identity) is not { } characters) return 2;
        var packetPolicies = CreatePacketPolicies(resources, foundation, admission, world, identity, characters);
        RegisterSandboxCommands(resources, foundation, admission, identity);
        RegisterAdministration(resources, foundation, admission, world, identity);
        if (CreateOperations(resources, launch, foundation, admission, world, identity) is not { } operations) return 2;
        if (StartPlugins(resources, launch, foundation, world, identity, characters, operations, softRestart) is not { } plugins) return 2;
        StartDashboard(resources, launch, foundation, admission, world, identity, operations, plugins);
        return RunNative(launch, foundation, admission, world, identity, characters, packetPolicies, operations, serverArgs);
    }

    private static Launch? ReadLaunch(string[] args)
    {
        SandboxBackendLaunchOptions backendLaunch;
        try
        {
            backendLaunch = SandboxBackendLaunchOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
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
            return null;
        }

        OperationsUiOptions uiOptions;
        try
        {
            uiOptions = OperationsUiModeParser.Parse(serverConfiguration.Current.Arguments);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
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
            return null;
        }
        return new(backendLaunch, serverConfiguration, uiOptions, worldImageOptions, tileStorageOptions);
    }

    private static bool PrepareWorld(Launch launch, NativeTileStorageScope? tileStorage, out string configuredWorldPath)
    {
        configuredWorldPath = "";
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
                return false;
            }
        }
        configuredWorldPath = launch.ServerConfiguration.Current.Values["world"].DisplayValue;
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
                return false;
            }
        }
        return true;
    }

    private static Foundation CreateFoundation(RuntimeResourceScope resources, Launch launch, long processStartedAt)
    {
        var backendLaunch = launch.BackendLaunch;
        string configuredCulture = launch.ServerConfiguration.Current.Values["defaultCulture"].DisplayValue;
        var localization = new LocalizationService(
            configuredCulture.StartsWith('<') ? "en-US" : configuredCulture);
        resources.Own(RegisterCoreRussianCatalog(localization));
        resources.Own(PlatformCommand.RegisterRussianCatalog(localization));
        resources.Own(UserInfoCommand.RegisterRussianCatalog(localization));
        resources.Own(PlaytimeCommand.RegisterRussianCatalog(localization));

        // Explicit composition root: no assembly scanning or reflection-based handler discovery.
        IEventPipeline<WorldHardmodeStarting> hardmodeStarting = new EventPipeline<WorldHardmodeStarting>(
            Array.Empty<IEventHandler<WorldHardmodeStarting>>(),
            Array.Empty<IEventMonitor<WorldHardmodeStarting>>());

        var gameThreadDispatcher = resources.Own(new TerrariaRuntimeGameThreadDispatcher());
        WorldId runtimeWorldId = backendLaunch.IsBackend ? backendLaunch.WorldId : WorldId.Primary;
        string runtimeDataRoot = backendLaunch.IsBackend
            ? Path.Combine(AppContext.BaseDirectory, "data", "sandboxes", "instances", runtimeWorldId.Value, "runtime", "data")
            : Path.Combine(AppContext.BaseDirectory, "data");
        var connectionMetadata = new TerrariaConnectionMetadataRegistry();
        var playerBoundary = resources.Own(new TerrariaPlayerBoundary(gameThreadDispatcher, connectionMetadata, runtimeWorldId));
        var playerAuthorization = resources.Own(new PlayerSessionAuthorizationService(playerBoundary));
        var updateChecker = resources.Own(new GitHubReleaseUpdateChecker());
        var extensionState = resources.Own(new ExtensionStateStore());
        var operationsState = resources.Own(new TerrariaOperationsState());
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
        resources.Own(RuntimeCommandRegistration.RegisterPlayer(commandDispatcher, playerBoundary, runtimeDataRoot));
        var lifecycle = new RuntimeLifecycleTracker(processStartedAt);
        var networkMetrics = new NetworkMetricsCounter();
        var securitySignals = new BoundedSecuritySignalStore();
        var logStore = new BoundedOperationsLogStore();
        resources.Own(commandDispatcher.Register(new DisplayLogsCommand(logStore)));
        var consoleSink = new ConsoleOperationsLogSink();
        JsonFileOperationsLogSink? fileSink = TryCreateFileSink(backendLaunch);
        var sinks = new List<IOperationsLogSink> { logStore };
        if (launch.UiOptions.EffectiveMode == OperationsUiMode.Plain)
        {
            sinks.Add(consoleSink);
        }

        if (fileSink is not null)
        {
            sinks.Add(fileSink);
        }

        resources.Own(fileSink);
        var logger = new StructuredOperationsLogger(sinks);
        return new(localization, hardmodeStarting, gameThreadDispatcher, runtimeWorldId, runtimeDataRoot, connectionMetadata, playerBoundary, playerAuthorization, extensionState, operationsState, moduleRouter, commandDispatcher, lifecycle, networkMetrics, securitySignals, logStore, logger);
    }

    private static int RunNative(Launch launch, Foundation foundation, Admission admission, World world, Identity identity, Characters characters, PacketPolicies packetPolicies, Operations operations, string[] serverArgs)
    {
        var primaryCharacterOptions = characters.PrimaryCharacterOptions;
        var authoritativeCharacters = characters.AuthoritativeCharacters;
        var services = new TerrariaServerHostServices(
            Runtime: new(foundation.Lifecycle, foundation.GameThreadDispatcher, foundation.OperationsState,
                [identity.Database], foundation.Logger),
            World: new(foundation.RuntimeWorldId, foundation.HardmodeStarting, operations.WorldImages,
                world.WorldMaintenance, world.WorldClock, foundation.ModuleRouter, world.WorldEvents),
            Network: new(foundation.ConnectionMetadata, packetPolicies.PlayerPacketPolicies,
                foundation.NetworkMetrics, foundation.SecuritySignals,
                ServerNetworkOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "network.json"), foundation.Logger),
                admission.Whitelist),
            Players: new(foundation.PlayerBoundary, foundation.PlayerAuthorization, identity.AccountSessions),
            Commands: new(foundation.CommandDispatcher, identity.Chat, foundation.Localization, foundation.Localization),
            Scenes: new(world.Scenes, world.Visibility, world.SceneRenderer),
            Characters: new(
                Authoritative: authoritativeCharacters,
                GameplayReadiness: authoritativeCharacters ? characters.SandboxSscSessions.IsCharacterReady
                    : !launch.BackendLaunch.IsBackend && primaryCharacterOptions.GameplayGateEnabled
                        ? player => characters.SandboxSscSessions.IsGameplayAllowed(player, primaryCharacterOptions.EffectiveRequireCharacterReady)
                        : null,
                EarlyJoin: authoritativeCharacters || !launch.BackendLaunch.IsBackend && primaryCharacterOptions.Enabled
                    ? admission.SandboxCharacterAdapter : null,
                JoinInventoryPolicy: packetPolicies.InventoryPolicy));
        return TerrariaServerHost.Run(serverArgs, services);
    }

    private sealed record Launch(
        SandboxBackendLaunchOptions BackendLaunch,
        VegaServerConfigurationState ServerConfiguration,
        OperationsUiOptions UiOptions,
        WorldImageOptions WorldImageOptions,
        NativeTileStorageOptions TileStorageOptions);
    private sealed record Foundation(
        LocalizationService Localization,
        IEventPipeline<WorldHardmodeStarting> HardmodeStarting,
        TerrariaRuntimeGameThreadDispatcher GameThreadDispatcher,
        WorldId RuntimeWorldId,
        string RuntimeDataRoot,
        TerrariaConnectionMetadataRegistry ConnectionMetadata,
        TerrariaPlayerBoundary PlayerBoundary,
        PlayerSessionAuthorizationService PlayerAuthorization,
        ExtensionStateStore ExtensionState,
        TerrariaOperationsState OperationsState,
        RuntimeModuleGenerationRouter ModuleRouter,
        CommandDispatcher CommandDispatcher,
        RuntimeLifecycleTracker Lifecycle,
        NetworkMetricsCounter NetworkMetrics,
        BoundedSecuritySignalStore SecuritySignals,
        BoundedOperationsLogStore LogStore,
        StructuredOperationsLogger Logger);
}
