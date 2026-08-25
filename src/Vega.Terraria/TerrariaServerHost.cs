using Multiplicity.Packets.Views;
using ReLogic.OS;
using Terraria.ID;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Events;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;
using Vega.Contracts.Localization;
using Vega.Contracts.Moderation;
using Vega.Contracts.Messaging;
using Vega.Contracts.Scenes;
using Vega.Terraria.Commands;
using Vega.Terraria.Hooking;
using Vega.Terraria.Operations;
using Vega.Terraria.Players;
using Vega.Terraria.Networking;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;

namespace Vega.Terraria;

/// <summary>
/// Owns the Terraria process bootstrap used by Vega.
/// </summary>
public static class TerrariaServerHost
{
    public static int Run(
        string[] args,
        WorldId runtimeWorldId,
        IRuntimeLifecycleController lifecycle,
        IEventPipeline<WorldHardmodeStarting> hardmodeStarting,
        TerrariaRuntimeGameThreadDispatcher gameThreadDispatcher,
        TerrariaPlayerBoundary playerBoundary,
        TerrariaConnectionMetadataRegistry connectionMetadata,
        TerrariaOperationsState operationsState,
        INetworkMetricsRecorder networkMetrics,
        ISecuritySignalRecorder securitySignals,
        IOperationsLogger logger,
        TerrariaWorldImageService worldImages,
        TerrariaWorldMaintenanceService worldMaintenance,
        IWorldClockController worldClock,
        IReadOnlyList<IRuntimeStartupParticipant> startupParticipants,
        IPacketProcessor<PacketView> playerOwnershipSecurity,
        ICommandDispatcher commandDispatcher,
        IPlayerAuthorizationService playerAuthorization,
        IPlayerAccountSessionService accountSessions,
        IMuteService mutes,
        IChatPipeline chat,
        IChestInteractionPolicy chestPolicy,
        ISceneRuntime scenes,
        ISceneVisibilityRuntime visibility,
        ISceneRenderSink sceneRenderer,
        ILocalizationService localization,
        IPlayerCultureService cultures)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(hardmodeStarting);
        ArgumentNullException.ThrowIfNull(gameThreadDispatcher);
        ArgumentNullException.ThrowIfNull(playerBoundary);
        ArgumentNullException.ThrowIfNull(connectionMetadata);
        ArgumentNullException.ThrowIfNull(operationsState);
        ArgumentNullException.ThrowIfNull(networkMetrics);
        ArgumentNullException.ThrowIfNull(securitySignals);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(worldImages);
        ArgumentNullException.ThrowIfNull(worldMaintenance);
        ArgumentNullException.ThrowIfNull(worldClock);
        ArgumentNullException.ThrowIfNull(startupParticipants);
        ArgumentNullException.ThrowIfNull(playerOwnershipSecurity);
        ArgumentNullException.ThrowIfNull(commandDispatcher);
        ArgumentNullException.ThrowIfNull(playerAuthorization);
        ArgumentNullException.ThrowIfNull(accountSessions);
        ArgumentNullException.ThrowIfNull(mutes);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(chestPolicy);
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(sceneRenderer);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(cultures);

        void Report(RuntimeLifecycleTransition transition)
        {
            if (transition.Stage == RuntimeLifecycleStage.NetworkReady)
            {
                operationsState.ResetTickRateStatistics();
            }

            ReportLifecycleTransition(transition, logger);
        }
        void ReportUnhandled(object sender, UnhandledExceptionEventArgs eventArgs) =>
            logger.Log(
                OperationsLogLevel.Critical,
                "runtime",
                "Unhandled Vega Terraria runtime exception.",
                eventArgs.ExceptionObject as Exception);

        lifecycle.Transitioned += Report;
        lifecycle.Advance(RuntimeLifecycleStage.Bootstrap);

        AppDomain.CurrentDomain.UnhandledException += ReportUnhandled;
        IOperationsLogSource[] logSources =
        [
            .. commandDispatcher is IOperationsLogSource commandLogSource
                ? [commandLogSource]
                : Array.Empty<IOperationsLogSource>(),
            .. chat is IOperationsLogSource chatLogSource
                ? [chatLogSource]
                : Array.Empty<IOperationsLogSource>()
        ];
        using var operationsLogForwarder = new OperationsLogSourceForwarder(logger, logSources);
        using var nativeLifecycleHooks = new NativeLifecycleHooks(lifecycle);
        using var networkMetricsHooks = new NativeNetworkMetricsHooks(networkMetrics);
        using var worldDecisionHooks = new NativeWorldDecisionHooks(hardmodeStarting);
        using var playerPacketSecurityHooks = new NativePlayerPacketSecurityHooks(
            playerOwnershipSecurity,
            connectionMetadata,
            logger,
            networkMetrics,
            securitySignals);
        using var administrationCommands = TerrariaAdministrationCommandRegistration.Register(
            commandDispatcher,
            runtimeWorldId,
            gameThreadDispatcher,
            playerBoundary);
        using var playerLifecycleCommands = TerrariaPlayerLifecycleCommandRegistration.Register(
            commandDispatcher,
            playerBoundary);
        using var gameplayUtilityCommands = TerrariaGameplayUtilityCommandRegistration.Register(
            commandDispatcher,
            runtimeWorldId,
            gameThreadDispatcher,
            playerBoundary,
            playerBoundary);
        using var clearCommand = TerrariaClearCommandRegistration.Register(
            commandDispatcher,
            runtimeWorldId,
            gameThreadDispatcher,
            playerBoundary);
        using var privateMessageCommands = TerrariaPrivateMessageCommandRegistration.Register(
            commandDispatcher,
            playerBoundary,
            playerBoundary,
            playerBoundary,
            mutes);
        using var consoleCommandHooks = new NativeConsoleCommandHooks(
            commandDispatcher,
            localization,
            cultures,
            runtimeWorldId);
        using var playerCommandHooks = new NativePlayerCommandHooks(
            commandDispatcher,
            playerAuthorization,
            playerBoundary,
            playerBoundary,
            logger,
            localization,
            cultures,
            playerBoundary,
            accountSessions,
            chat);
        using var chestPolicyHooks = new NativeChestPolicyHooks(
            playerBoundary,
            chestPolicy,
            gameThreadDispatcher,
            logger);
        using var sceneNetworkHooks = new NativeSceneNetworkHooks(
            scenes,
            sceneRenderer,
            playerBoundary);
        using var sceneVisibilityHooks = new NativeSceneVisibilityHooks(
            visibility,
            playerBoundary);
        using var worldClockSynchronizer = new TerrariaWorldClockSynchronizer(worldClock, logger);

        try
        {
            PrepareSavePath(args);
            InitialiseTerrariaInternals();

            nativeLifecycleHooks.Attach();
            gameThreadDispatcher.Attach();
            playerBoundary.Attach();
            operationsState.Attach();
            networkMetricsHooks.Attach();
            worldImages.Attach();
            worldMaintenance.Attach();
            worldClockSynchronizer.Attach();
            worldDecisionHooks.Attach();
            playerPacketSecurityHooks.Attach();
            consoleCommandHooks.Attach();
            playerCommandHooks.Attach();
            chestPolicyHooks.Attach();
            sceneNetworkHooks.Attach();
            sceneVisibilityHooks.Attach();

            lifecycle.Advance(RuntimeLifecycleStage.RuntimeReady);

            // The explicit composition root has already started and validated every Vega module.
            lifecycle.Advance(RuntimeLifecycleStage.ModulesRegistered);

            InitializeRuntimeParticipants(startupParticipants, logger);

            if (args.Any(static arg => string.Equals(arg, "-skipassemblyload", StringComparison.OrdinalIgnoreCase)))
            {
                global::Terraria.Main.SkipAssemblyLoad = true;
            }

            global::Terraria.WindowsLaunch.Main(args);
            return 0;
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Critical, "runtime", "Vega Terraria runtime failed.", ex);
            return 1;
        }
        finally
        {
            lifecycle.Transitioned -= Report;
            AppDomain.CurrentDomain.UnhandledException -= ReportUnhandled;
        }
    }

    private static void InitializeRuntimeParticipants(
        IReadOnlyList<IRuntimeStartupParticipant> participants,
        IOperationsLogger logger)
    {
        foreach (IRuntimeStartupParticipant participant in participants)
        {
            ArgumentNullException.ThrowIfNull(participant);
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            participant.InitializeAsync().AsTask().GetAwaiter().GetResult();
            TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            logger.Log(
                OperationsLogLevel.Information,
                "startup",
                $"{participant.Name} ready in {elapsed.TotalMilliseconds:F1} ms.");
        }
    }

    private static void ReportLifecycleTransition(
        RuntimeLifecycleTransition transition,
        IOperationsLogger logger)
    {
        if (transition.Stage is RuntimeLifecycleStage.WorldLoading or RuntimeLifecycleStage.WorldReady)
        {
            return;
        }

        logger.Log(
            OperationsLogLevel.Information,
            "lifecycle",
            $"Vega {transition.Stage,-18} {transition.Elapsed.TotalMilliseconds,8:F1} ms " +
            $"(+{transition.StageDuration.TotalMilliseconds:F1} ms)");

        if (transition.Stage == RuntimeLifecycleStage.RuntimeReady &&
            transition.StageDuration > TimeSpan.FromMilliseconds(100))
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "lifecycle",
                $"Vega startup budget warning: bootstrap took {transition.StageDuration.TotalMilliseconds:F1} ms (> 100 ms).");
        }
    }

    private static void PrepareSavePath(string[] args)
    {
        global::Terraria.Program.LaunchParameters = global::Terraria.Utils.ParseArguements(args);
        global::Terraria.Program.SavePath = global::Terraria.Program.LaunchParameters.ContainsKey("-savedirectory")
            ? global::Terraria.Program.LaunchParameters["-savedirectory"]
            : Platform.Get<IPathService>().GetStoragePath("Terraria");
    }

    private static void InitialiseTerrariaInternals()
    {
        ItemID.Sets.Explosives = ItemID.Sets.Factory.CreateBoolSet(
        [
            ItemID.Bomb,
            ItemID.StickyBomb,
            ItemID.BouncyBomb,
            ItemID.BombFish,
            ItemID.DirtBomb,
            ItemID.DirtStickyBomb,
            ItemID.ScarabBomb,
            ItemID.SuperBomb,
            ItemID.SuperStickyBomb,
            ItemID.FreezeBomb,
            ItemID.GrenadeLauncher,
            ItemID.RocketLauncher,
            ItemID.SnowmanCannon,
            ItemID.Celeb2,
            ItemID.RocketII,
            ItemID.RocketIV,
            ItemID.ClusterRocketII,
            ItemID.MiniNukeII,
            ItemID.DryRocket,
            ItemID.WetRocket,
            ItemID.LavaRocket,
            ItemID.HoneyRocket,
            ItemID.Dynamite,
            ItemID.Explosives,
            ItemID.StickyDynamite
        ]);

        TileID.Sets.Corrupt[TileID.CorruptGrass] = true;
        TileID.Sets.Corrupt[TileID.CorruptPlants] = true;
        TileID.Sets.Corrupt[TileID.CorruptThorns] = true;
        TileID.Sets.Corrupt[TileID.CorruptIce] = true;
        TileID.Sets.Corrupt[TileID.CorruptHardenedSand] = true;
        TileID.Sets.Corrupt[TileID.CorruptSandstone] = true;
        TileID.Sets.Corrupt[TileID.Ebonstone] = true;
        TileID.Sets.Corrupt[TileID.Ebonsand] = true;

        TileID.Sets.Crimson[TileID.FleshBlock] = true;
        TileID.Sets.Crimson[TileID.CrimsonGrass] = true;
        TileID.Sets.Crimson[TileID.FleshIce] = true;
        TileID.Sets.Crimson[TileID.CrimsonPlants] = true;
        TileID.Sets.Crimson[TileID.Crimstone] = true;
        TileID.Sets.Crimson[TileID.Crimsand] = true;
        TileID.Sets.Crimson[TileID.CrimsonVines] = true;
        TileID.Sets.Crimson[TileID.CrimsonThorns] = true;
        TileID.Sets.Crimson[TileID.CrimsonHardenedSand] = true;
        TileID.Sets.Crimson[TileID.CrimsonSandstone] = true;

        TileID.Sets.Hallow[TileID.HallowedGrass] = true;
        TileID.Sets.Hallow[TileID.HallowedPlants] = true;
        TileID.Sets.Hallow[TileID.HallowedPlants2] = true;
        TileID.Sets.Hallow[TileID.HallowedVines] = true;
        TileID.Sets.Hallow[TileID.HallowedIce] = true;
        TileID.Sets.Hallow[TileID.HallowHardenedSand] = true;
        TileID.Sets.Hallow[TileID.HallowSandstone] = true;
        TileID.Sets.Hallow[TileID.Pearlsand] = true;
        TileID.Sets.Hallow[TileID.Pearlstone] = true;
    }
}
